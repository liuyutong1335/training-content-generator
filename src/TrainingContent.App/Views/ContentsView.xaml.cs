using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrainingContent.App.Services;
using TrainingContent.Storage;
using TrainingContent.Video;

namespace TrainingContent.App.Views;

/// <summary>
/// Content Manager。保存済み Project の一覧・更新・新規作成・オープン・名前変更・削除を担う。
///
/// <para>
/// 責任分離: ContentsView → ProjectWorkspace / ProjectStore → Filesystem。
/// この View は File / Directory を直接操作せず、project.json も直接読まない。
/// </para>
/// <para>
/// Create / Open は Home と同じ <see cref="ProjectWorkspace"/> を通す。normalization や
/// Current Project の更新をこの View 用に書き直さない。
/// </para>
/// </summary>
public partial class ContentsView : UserControl
{
    private enum ContentsState
    {
        Loading,
        Empty,
        Error,
        Ready,
    }

    /// <summary>生成開始直後の初期表示（Video Core の最初の report が届く前）。</summary>
    private const string GeneratingText = "動画を生成しています…";

    private const string CancelRequestedText = "キャンセルしています…";

    private readonly ProjectStore _projectStore;
    private readonly ProjectWorkspace _workspace;
    private readonly VideoGenerationCoordinator _videoGeneration;
    private readonly ObservableCollection<ProjectSummary> _projects = [];

    private bool _isLoading;
    private bool _isGenerating;
    private bool _isCancelRequested;
    private bool _hasLoaded;

    /// <summary>
    /// 実行中の video 生成 session の CTS。<b>1 generation = 1 CTS</b> で、完了時に必ず Dispose して null に戻す。
    /// </summary>
    private CancellationTokenSource? _videoGenerationCts;

    /// <summary>Status Bar 表示用の単純な通知。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Project を作成・オープンし、Home 表示へ切り替えるべきとき。</summary>
    public event EventHandler<string>? ProjectActivated;

    /// <summary>video 生成の開始 / 終了で発火する（UI thread）。Shell navigation lock の更新に使う。</summary>
    public event EventHandler? GenerationActivityChanged;

    /// <summary>video 生成中かどうか。Shell の navigation lock 判定に使う。</summary>
    public bool IsGenerating => _isGenerating;

    public ContentsView(
        ProjectStore projectStore,
        ProjectWorkspace workspace,
        VideoGenerationCoordinator videoGeneration)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(videoGeneration);

        InitializeComponent();

        _projectStore = projectStore;
        _workspace = workspace;
        _videoGeneration = videoGeneration;

        ProjectGrid.ItemsSource = _projects;

        ApplyState(ContentsState.Loading);
        UpdateButtons();
    }

    /// <summary>初回表示時に一度だけ読み込む。2 回目以降の navigation では再走査しない。</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_hasLoaded)
        {
            return;
        }

        await LoadAsync();
    }

    // ---------------------------------------------------------------------
    // Load / Refresh
    // ---------------------------------------------------------------------

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
    }

    /// <summary>
    /// 一覧を取得して表示を置き換える。Current Project には関与しない
    /// （一覧と Current Project は別状態。Refresh で Current Project を解除しない）。
    /// </summary>
    private async Task LoadAsync()
    {
        if (_isLoading)
        {
            return;
        }

        _isLoading = true;
        ApplyState(ContentsState.Loading);
        UpdateButtons();
        SetStatus("コンテンツを読み込んでいます...");

        try
        {
            var previousId = (ProjectGrid.SelectedItem as ProjectSummary)?.Id;

            var summaries = await _projectStore.ListProjectsAsync();

            _projects.Clear();
            foreach (var summary in summaries)
            {
                _projects.Add(summary);
            }

            _hasLoaded = true;

            if (_projects.Count == 0)
            {
                ProjectGrid.SelectedItem = null;
                ApplyState(ContentsState.Empty);
                SetStatus("保存済みのコンテンツはありません。");
            }
            else
            {
                ApplyState(ContentsState.Ready);

                if (previousId is { } id)
                {
                    ProjectGrid.SelectedItem = _projects.FirstOrDefault(p => p.Id == id);
                }

                SetStatus($"{_projects.Count} 件のコンテンツ");
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("ContentsView: 一覧の読み込みに失敗しました — {0}", ex);

            _projects.Clear();
            ProjectGrid.SelectedItem = null;
            ApplyState(ContentsState.Error);
            SetStatus("コンテンツの読み込みに失敗しました。");
        }
        finally
        {
            _isLoading = false;
            UpdateButtons();
        }
    }

    // ---------------------------------------------------------------------
    // New Project（Home と同じ ProjectWorkspace 経路）
    // ---------------------------------------------------------------------

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Input is null)
        {
            return; // Cancel
        }

        try
        {
            var project = await _workspace.CreateProjectAsync(dialog.Input);

            // 一覧にも反映してから Home へ。
            await LoadAsync();
            ProjectActivated?.Invoke(this, $"「{project.Title}」を作成しました。");
        }
        catch (Exception ex)
        {
            Trace.TraceError("ContentsView: Project の作成に失敗しました — {0}", ex);

            SetStatus("プロジェクトの作成に失敗しました。");
            MessageBox.Show(
                Window.GetWindow(this),
                "プロジェクトの作成に失敗しました。",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------------
    // Open
    // ---------------------------------------------------------------------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        await OpenSelectedProjectAsync();
    }

    private async void ProjectRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 行のダブルクリック。Open ボタンと同じ処理に集約する。
        await OpenSelectedProjectAsync();
    }

    /// <summary>選択中の Project を開く。Open ボタンとダブルクリックの共通経路。</summary>
    private async Task OpenSelectedProjectAsync()
    {
        // 生成中は対象 Project を途中変更させない（ボタン disable に加えた shared guard）。
        if (_isGenerating || _isLoading)
        {
            return;
        }

        if (ProjectGrid.SelectedItem is not ProjectSummary selected)
        {
            return;
        }

        try
        {
            var project = await _workspace.OpenProjectAsync(selected.Id);

            if (project is null)
            {
                // Case A: 一覧取得後に外部から削除された。
                SetStatus("プロジェクトが見つかりません。");
                await LoadAsync();
                return;
            }

            ProjectActivated?.Invoke(this, $"「{project.Title}」を開きました。");
        }
        catch (ProjectStoreException ex)
        {
            // Case B: project.json が破損・未対応 schema など。Current Project は変更しない。
            Trace.TraceError("ContentsView: Project を開けませんでした — {0}", ex);

            SetStatus("プロジェクトを開けませんでした。");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            // Case C: 予期しない IO error など。Current Project は維持する。
            Trace.TraceError("ContentsView: Project のオープンで予期しないエラー — {0}", ex);

            SetStatus("プロジェクトを開けませんでした。");
            MessageBox.Show(
                Window.GetWindow(this),
                "プロジェクトを開けませんでした。",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------------
    // Selection
    // ---------------------------------------------------------------------

    private void ProjectGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var selected = ProjectGrid.SelectedItem as ProjectSummary;
        var hasSelection = selected is not null;
        var state = ContentsGenerationUiStateResolver.Resolve(_isLoading, _isGenerating, _isCancelRequested);

        RefreshButton.IsEnabled = state.IsContentMutationEnabled;
        NewProjectButton.IsEnabled = state.IsContentMutationEnabled;
        EmptyNewProjectButton.IsEnabled = state.IsContentMutationEnabled;
        OpenButton.IsEnabled = state.IsContentMutationEnabled && hasSelection;
        RenameButton.IsEnabled = state.IsContentMutationEnabled && hasSelection;
        DeleteButton.IsEnabled = state.IsContentMutationEnabled && hasSelection;
        ProjectGrid.IsEnabled = state.IsGridEnabled;

        // ここは convenience の事前確認。最終的な precondition は VideoGenerationCoordinator が authority。
        GenerateButton.IsEnabled = state.IsGenerateEnabled(
            selected is { StepCount: > 0, DurationMs: not null });
        // 未選択時に「再生成」と出さない（selected が null のとき ?. 比較は false に落ちる）。
        GenerateButton.Content = _isGenerating
            ? "生成中..."
            : selected is null || selected.VideoStatus == ArtifactGenerationState.Missing
                ? "動画を生成"
                : "動画を再生成";

        GenerationPanel.Visibility = state.IsProgressVisible ? Visibility.Visible : Visibility.Collapsed;
        CancelGenerateButton.Visibility = state.IsCancelVisible ? Visibility.Visible : Visibility.Collapsed;
        CancelGenerateButton.IsEnabled = state.IsCancelEnabled;
    }

    // ---------------------------------------------------------------------
    // Video Generate
    // ---------------------------------------------------------------------

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        if (_isGenerating || _isLoading || ProjectGrid.SelectedItem is not ProjectSummary selected)
        {
            return;
        }

        // 対象 Project を capture する。生成中に selection が動いても追従しない。
        var projectId = selected.Id;

        // 1 generation = 1 CTS。generation 中に 2 つ目の生成は開始させない（_isGenerating guard）。
        var cts = new CancellationTokenSource();
        _videoGenerationCts = cts;
        _isGenerating = true;
        _isCancelRequested = false;

        ResetProgress();
        UpdateButtons();
        SetStatus("動画を生成しています...");
        GenerationActivityChanged?.Invoke(this, EventArgs.Empty);

        // Progress<T> は生成元（UI thread）の SynchronizationContext を捕捉するので、Report は UI thread へ戻る。
        var progress = new Progress<VideoCompositionProgress>(OnCompositionProgress);

        try
        {
            var result = await _videoGeneration.GenerateAsync(projectId, progress, cts.Token);

            // 一覧を作り直して VideoStatus を更新する（selection は LoadAsync が復元する）。
            // cancel は artifact も metadata も変えていないので再読込しない。
            if (result.Status is VideoGenerationStatus.Generated
                or VideoGenerationStatus.ProjectNotFound
                or VideoGenerationStatus.SourceChanged)
            {
                await LoadAsync();
            }

            ApplyGenerationResult(result);
        }
        finally
        {
            // cancel 要求で解除せず、GenerateAsync が戻ってから解除する。
            _isGenerating = false;
            _isCancelRequested = false;
            if (ReferenceEquals(_videoGenerationCts, cts))
            {
                _videoGenerationCts = null;
            }

            cts.Dispose();

            // generation failure / cancel でも permanent busy にしない。
            ResetProgress();
            UpdateButtons();
            GenerationActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Cancel button。連打は無視し、実際の解除は <c>GenerateAsync</c> の完了後に行う。</summary>
    private void CancelGenerate_Click(object sender, RoutedEventArgs e)
    {
        if (!_isGenerating || _isCancelRequested)
        {
            return;
        }

        var cts = _videoGenerationCts;
        if (cts is null || cts.IsCancellationRequested)
        {
            return;
        }

        // 先に状態を倒してから Cancel する（Cancel の同期 callback 中に再 click されても無視される）。
        _isCancelRequested = true;
        SetProgressDetail(CancelRequestedText);
        UpdateButtons();
        SetStatus("キャンセルしています...");

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // generation が直前に完了していた（finally の Dispose と競合）。何もしない。
        }
    }

    /// <summary>video 生成の進捗（UI thread で呼ばれる）。値は Video Core のものをそのまま表示する。</summary>
    private void OnCompositionProgress(VideoCompositionProgress progress)
    {
        // 完了後に遅れて届いた report は無視する（完了状態を進捗で上書きしない）。
        if (!_isGenerating || _isCancelRequested)
        {
            return;
        }

        VideoProgressBar.Value = Math.Clamp(progress.OverallProgress, 0.0, 1.0) * 100.0;
        SetProgressDetail(progress.StageDetail);
    }

    /// <summary>生成開始直後の初期表示（Video Core の最初の report が届く前）。</summary>
    private void ResetProgress()
    {
        VideoProgressBar.Value = 0;
        SetProgressDetail(GeneratingText);
    }

    private void SetProgressDetail(string? detail) =>
        VideoProgressText.Text = string.IsNullOrWhiteSpace(detail) ? GeneratingText : detail;

    /// <summary>
    /// <see cref="VideoGenerationResult"/> を user-facing な status / dialog にする。
    /// Exception.ToString() / stack trace は UI に出さない。
    /// </summary>
    private void ApplyGenerationResult(VideoGenerationResult result)
    {
        var presentation = VideoGenerationPresentation.Describe(result);
        SetStatus(presentation.StatusText);

        switch (presentation.Dialog)
        {
            case VideoResultDialog.Error:
                ShowError(presentation.StatusText, presentation.DialogDetail, MessageBoxImage.Error);
                break;

            case VideoResultDialog.RecoveryWarning:
                // recovery 用 backup は UI 側から cleanup しない（自動削除もしない）。
                MessageBox.Show(
                    Window.GetWindow(this),
                    "動画の保存に失敗し、旧動画の自動復旧にも失敗しました。" + Environment.NewLine +
                    "復旧用バックアップを以下に保持しています。" + Environment.NewLine + Environment.NewLine +
                    presentation.DialogDetail + Environment.NewLine + Environment.NewLine +
                    "このフォルダーを削除しないでください。",
                    "動画の生成に失敗しました",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                break;
        }
    }

    private void ShowError(string message, string? detail, MessageBoxImage icon)
    {
        var text = string.IsNullOrWhiteSpace(detail)
            ? message
            : message + Environment.NewLine + Environment.NewLine + detail;

        MessageBox.Show(
            Window.GetWindow(this),
            text,
            "エラー",
            MessageBoxButton.OK,
            icon);
    }

    // ---------------------------------------------------------------------
    // Rename
    // ---------------------------------------------------------------------

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is not ProjectSummary selected)
        {
            return;
        }

        var dialog = new RenameProjectWindow(selected.Title) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return; // Cancel
        }

        if (dialog.ProjectTitle == selected.Title)
        {
            return;
        }

        try
        {
            // Current Project なら Current Project も更新される（workspace が面倒を見る）。
            await _workspace.RenameProjectAsync(selected.Id, dialog.ProjectTitle);
            await LoadAsync();
            SetStatus("名前を変更しました");
        }
        catch (Exception ex)
        {
            Trace.TraceError("ContentsView: 名前の変更に失敗しました — {0}", ex);
            MessageBox.Show(
                Window.GetWindow(this),
                "名前の変更に失敗しました。",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------------
    // Delete
    // ---------------------------------------------------------------------

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is not ProjectSummary selected)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            $"「{selected.Title}」を削除しますか？{Environment.NewLine}{Environment.NewLine}この操作は元に戻せません。",
            "削除の確認",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No); // 既定は No

        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // Current Project を削除した場合のみ Current Project が解除される。
            await _workspace.DeleteProjectAsync(selected.Id);

            ProjectGrid.SelectedItem = null;
            await LoadAsync();
            SetStatus("削除しました");
        }
        catch (Exception ex)
        {
            Trace.TraceError("ContentsView: 削除に失敗しました — {0}", ex);
            MessageBox.Show(
                Window.GetWindow(this),
                "削除に失敗しました。",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------------
    // State / Status
    // ---------------------------------------------------------------------

    private void ApplyState(ContentsState state)
    {
        ProjectGrid.Visibility = state == ContentsState.Ready ? Visibility.Visible : Visibility.Collapsed;
        LoadingText.Visibility = state == ContentsState.Loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = state == ContentsState.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = state == ContentsState.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message) => StatusChanged?.Invoke(this, message);
}
