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
/// </summary>
public partial class MainWindow : Window
{
    // View インスタンスは 1 回だけ生成して使い回す（切替のたびに作り直さない）。
    private readonly HomeView _homeView;
    private readonly RecordingView _recordingView = new();
    private readonly ReviewView _reviewView = new();
    private readonly ContentsView _contentsView;

    public MainWindow(ProjectStore projectStore, CurrentProjectContext currentProject, ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(workspace);

        InitializeComponent();

        // ProjectStore / CurrentProjectContext / ProjectWorkspace は App から受け取る。
        // ここで new したり Path を組み立てたりしない。
        _homeView = new HomeView(currentProject, workspace);
        _homeView.StatusChanged += OnStatusChanged;
        _homeView.ProjectActivated += OnProjectActivated;

        _contentsView = new ContentsView(projectStore, workspace);
        _contentsView.StatusChanged += OnStatusChanged;
        _contentsView.ProjectActivated += OnProjectActivated;

        NavigateHome();
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void NavRecording_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavRecordingButton, _recordingView);

    private void NavReview_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavReviewButton, _reviewView);

    private async void NavContents_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(NavContentsButton, _contentsView);

        // 初回表示時に一度だけ読み込む。2 回目以降の navigation では再走査しない。
        await _contentsView.EnsureLoadedAsync();
    }

    /// <summary>Home へ遷移する。Project の作成・オープン後の遷移もここを通る。</summary>
    private void NavigateHome() => ShowPage(NavHomeButton, _homeView);

    private void OnStatusChanged(object? sender, string message) => StatusText.Text = message;

    private void OnProjectActivated(object? sender, string message)
    {
        NavigateHome();
        StatusText.Text = message;
    }

    /// <summary>メイン領域の View を差し替え、選択中ナビ項目とステータスを更新する。</summary>
    private void ShowPage(Button navButton, UserControl view)
    {
        PageHost.Content = view;

        var pageName = navButton.Content?.ToString() ?? string.Empty;
        StatusText.Text = $"{pageName} を表示中";

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
