using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.App.Views;
using TrainingContent.Storage;

namespace TrainingContent.App;

/// <summary>
/// Application Shell。責任は Navigation と View composition のみ。
/// Project の業務ロジックはここへ置かない（各 View と ProjectWorkspace の責任）。
///
/// <para>
/// Project の作成・オープン後に Home へ切り替えるのは View ではなく MainWindow の責任。
/// View は ProjectActivated を通知するだけで、MainWindow を探索したり直接操作したりしない。
/// </para>
/// <para>
/// 録画 session 中は Navigation を制限し、Window を閉じさせない（他 Project の Open/Delete で
/// session ownership が壊れるのを防ぐ）。
/// </para>
/// </summary>
public partial class MainWindow : Window
{
    // View インスタンスは 1 回だけ生成して使い回す（切替のたびに作り直さない）。
    private readonly HomeView _homeView;
    private readonly RecordingView _recordingView;
    private readonly ReviewView _reviewView;
    private readonly ContentsView _contentsView;
    private readonly RecordingCoordinator _recordingCoordinator;

    public MainWindow(
        ProjectStore projectStore,
        CurrentProjectContext currentProject,
        ProjectWorkspace workspace,
        RecordingCoordinator recordingCoordinator,
        VideoGenerationCoordinator videoGenerationCoordinator,
        ScreenshotRedactionCoordinator screenshotRedaction)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(recordingCoordinator);
        ArgumentNullException.ThrowIfNull(videoGenerationCoordinator);
        ArgumentNullException.ThrowIfNull(screenshotRedaction);

        InitializeComponent();

        _recordingCoordinator = recordingCoordinator;

        // ProjectStore / CurrentProjectContext / ProjectWorkspace は App から受け取る。
        // ここで new したり Path を組み立てたりしない。
        _homeView = new HomeView(currentProject, workspace);
        _homeView.StatusChanged += OnStatusChanged;
        _homeView.ProjectActivated += OnProjectActivated;

        _recordingView = new RecordingView(recordingCoordinator, currentProject);
        _recordingView.StatusChanged += OnStatusChanged;

        // Review は Current Project の Steps を編集し（B1）、Screenshot の BlackBox redaction（C）を行う。
        // editable control は detached draft にだけ bind し、保存 / redaction は Services 経由でのみ行う。
        _reviewView = new ReviewView(currentProject, workspace, screenshotRedaction);
        _reviewView.StatusChanged += OnStatusChanged;

        _contentsView = new ContentsView(projectStore, workspace, videoGenerationCoordinator);
        _contentsView.StatusChanged += OnStatusChanged;
        _contentsView.ProjectActivated += OnProjectActivated;

        _recordingCoordinator.ActivityChanged += OnRecordingActivityChanged;

        NavigateHome();
    }

    // ---------------------------------------------------------------------
    // Recording session lock（§26 / §27）
    // ---------------------------------------------------------------------

    private void OnRecordingActivityChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyRecordingLock();
            return;
        }

        Dispatcher.BeginInvoke(new Action(ApplyRecordingLock));
    }

    /// <summary>
    /// 録画 session 中は他画面へ移動させない（Contents で別 Project を Open/Delete できなくする）。
    /// 停止後は Navigation を復元する。
    /// </summary>
    private void ApplyRecordingLock()
    {
        var active = _recordingCoordinator.IsSessionActive;

        NavHomeButton.IsEnabled = !active;
        NavReviewButton.IsEnabled = !active;
        NavContentsButton.IsEnabled = !active;
        NavRecordingButton.IsEnabled = true;

        if (active && !ReferenceEquals(PageHost.Content, _recordingView))
        {
            // 録画中の強制遷移では Status 表示を上書きしない。
            ShowPage(NavRecordingButton, _recordingView, StatusText.Text);
        }
    }

    /// <summary>録画中は終了させない（自動 Stop は行わない。ユーザーが録画を停止してから閉じる）。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        // 録画 session の lock を先に判定する（録画中は編集の確認より優先して終了を止める）。
        if (_recordingCoordinator.IsSessionActive)
        {
            MessageBox.Show(
                this,
                "録画中はアプリを終了できません。録画を停止してから終了してください。",
                "録画中",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            e.Cancel = true;
            return;
        }

        // canonical mutation（手順の保存 / Screenshot redaction）中は終了させない
        // （進行中の mutation と破棄の同意が食い違うため）。
        if (_reviewView.IsSaving)
        {
            MessageBox.Show(
                this,
                "手順を更新しています。完了してから終了してください。",
                "手順の編集",
                MessageBoxButton.OK,
                MessageBoxImage.Information);

            e.Cancel = true;
            return;
        }

        // Review に未保存の編集がある場合は破棄の同意を取る。
        if (_reviewView.HasUnsavedChanges
            && MessageBox.Show(
                this,
                "手順の編集内容が保存されていません。破棄して終了しますか？",
                "手順の編集",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void NavRecording_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveReviewIfNeeded(_recordingView))
        {
            return;
        }

        ShowPage(NavRecordingButton, _recordingView);
    }

    private void NavReview_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveReviewIfNeeded(_reviewView))
        {
            return;
        }

        ShowPage(NavReviewButton, _reviewView);
    }

    private async void NavContents_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmLeaveReviewIfNeeded(_contentsView))
        {
            return;
        }

        ShowPage(NavContentsButton, _contentsView);

        // 初回表示時に一度だけ読み込む。2 回目以降の navigation では再走査しない。
        await _contentsView.EnsureLoadedAsync();
    }

    /// <summary>Home へ遷移する。Project の作成・オープン後の遷移もここを通る。</summary>
    private void NavigateHome()
    {
        if (!ConfirmLeaveReviewIfNeeded(_homeView))
        {
            return;
        }

        ShowPage(NavHomeButton, _homeView);
    }

    /// <summary>
    /// Review から他の View へ移る前に、未保存の編集がないか確認する（B1 の navigation 保護）。
    /// Review に留まる / Review へ向かう場合と、そもそも Review を表示していない場合は確認しない。
    /// </summary>
    /// <returns>true = 遷移してよい。false = Review に留まる。</returns>
    private bool ConfirmLeaveReviewIfNeeded(UserControl targetView)
    {
        if (ReferenceEquals(targetView, _reviewView) || !ReferenceEquals(PageHost.Content, _reviewView))
        {
            return true;
        }

        return _reviewView.ConfirmDiscardIfNeeded();
    }

    private void OnStatusChanged(object? sender, string message) => StatusText.Text = message;

    private void OnProjectActivated(object? sender, string message)
    {
        NavigateHome();
        StatusText.Text = message;
    }

    /// <summary>メイン領域の View を差し替え、選択中ナビ項目とステータスを更新する。</summary>
    private void ShowPage(Button navButton, UserControl view)
    {
        var pageName = navButton.Content?.ToString() ?? string.Empty;
        ShowPage(navButton, view, $"{pageName}を表示中");
    }

    /// <summary>Status 表示を明示指定して View を差し替える（録画中の強制遷移など）。</summary>
    private void ShowPage(Button navButton, UserControl view, string statusMessage)
    {
        PageHost.Content = view;
        StatusText.Text = statusMessage;
        SetActiveNav(navButton);
    }

    /// <summary>選択中のナビ項目だけを強調表示にする。</summary>
    private void SetActiveNav(Button active)
    {
        var navButtons = new[] { NavHomeButton, NavRecordingButton, NavReviewButton, NavContentsButton };
        var activeBrush = (Brush)FindResource("NavButtonActiveBrush");

        foreach (var button in navButtons)
        {
            var isActive = ReferenceEquals(button, active);
            button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            button.Background = isActive ? activeBrush : Brushes.Transparent;
        }
    }
}
