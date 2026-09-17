using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TrainingContent.App.Services;
using TrainingContent.Storage;

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

    private readonly ProjectStore _projectStore;
    private readonly ProjectWorkspace _workspace;
    private readonly VideoGenerationCoordinator _videoGeneration;
    private readonly ObservableCollection<ProjectSummary> _projects = [];

    private bool _isLoading;
    private bool _isGenerating;
    private bool _hasLoaded;

    /// <summary>Status Bar 表示用の単純な通知。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Project を作成・オープンし、Home 表示へ切り替えるべきとき。</summary>
    public event EventHandler<string>? ProjectActivated;

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
        var idle = !_isLoading && !_isGenerating;

        RefreshButton.IsEnabled = idle;
        NewProjectButton.IsEnabled = idle;
        EmptyNewProjectButton.IsEnabled = idle;
        OpenButton.IsEnabled = idle && hasSelection;
        RenameButton.IsEnabled = idle && hasSelection;
        DeleteButton.IsEnabled = idle && hasSelection;

        // 生成中は一覧そのものを止める（selection 変更・行ダブルクリックも含めて）。
        ProjectGrid.IsEnabled = !_isGenerating;

        // ここは convenience の事前確認。最終的な precondition は VideoGenerationCoordinator が authority。
        GenerateButton.IsEnabled =
            idle && selected is { StepCount: > 0, DurationMs: not null };
        // 未選択時に「再生成」と出さない（selected が null のとき ?. 比較は false に落ちる）。
        GenerateButton.Content = _isGenerating
            ? "生成中..."
            : selected is null || selected.VideoStatus == ArtifactGenerationState.Missing
                ? "動画を生成"
                : "動画を再生成";
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

        _isGenerating = true;
        UpdateButtons();
        SetStatus("動画を生成しています...");

        try
        {
            var result = await _videoGeneration.GenerateAsync(projectId);

            // 一覧を作り直して VideoStatus を更新する（selection は LoadAsync が復元する）。
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
            // generation failure でも permanent busy にしない。
            _isGenerating = false;
            UpdateButtons();
        }
    }

    /// <summary>
    /// <see cref="VideoGenerationResult"/> を user-facing な status / dialog にする。
    /// Exception.ToString() / stack trace は UI に出さない。
    /// </summary>
    private void ApplyGenerationResult(VideoGenerationResult result)
    {
        switch (result.Status)
        {
            case VideoGenerationStatus.Generated:
                SetStatus("動画を生成しました。");
                break;

            case VideoGenerationStatus.ProjectNotFound:
                SetStatus("プロジェクトが見つかりません。");
                break;

            case VideoGenerationStatus.RecordingMissing:
                SetStatus("録画がありません。先に録画を作成してください。");
                break;

            case VideoGenerationStatus.StepsMissing:
                SetStatus("手順がありません。動画を生成するには手順の確定が必要です。");
                break;

            case VideoGenerationStatus.SourceChanged:
                SetStatus("生成中に内容が変更されました。最新の内容で再度生成してください。");
                break;

            case VideoGenerationStatus.ComposerUnavailable:
                SetStatus("動画生成に必要な FFmpeg が見つかりません。");
                ShowError(
                    "動画生成に必要な FFmpeg が見つかりません。",
                    result.ErrorMessage,
                    MessageBoxImage.Error);
                break;

            default:
                ApplyFailedResult(result);
                break;
        }
    }

    /// <summary>
    /// 通常の failure と「手動 recovery が必要な failure」を分ける。
    /// recovery 用 backup は UI 側から cleanup しない（自動削除もしない）。
    /// </summary>
    private void ApplyFailedResult(VideoGenerationResult result)
    {
        if (result.RecoveryDirectory is { } recoveryDirectory)
        {
            SetStatus("動画の保存に失敗しました。復旧用バックアップを保持しています。");

            MessageBox.Show(
                Window.GetWindow(this),
                "動画の保存に失敗し、旧動画の自動復旧にも失敗しました。" + Environment.NewLine +
                "復旧用バックアップを以下に保持しています。" + Environment.NewLine + Environment.NewLine +
                recoveryDirectory + Environment.NewLine + Environment.NewLine +
                "このフォルダーを削除しないでください。",
                "動画の生成に失敗しました",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetStatus("動画の生成に失敗しました。");
        ShowError("動画の生成に失敗しました。", result.ErrorMessage, MessageBoxImage.Error);
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
