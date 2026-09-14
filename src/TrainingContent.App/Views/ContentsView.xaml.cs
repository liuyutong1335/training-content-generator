using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TrainingContent.Storage;

namespace TrainingContent.App.Views;

/// <summary>
/// Content Manager。保存済み Project の一覧表示・更新・名前変更・削除を担う。
///
/// <para>
/// 責任分離: ContentsView → ProjectStore → Filesystem。
/// この View は File / Directory を直接操作しない。project.json も直接読まない
/// （破損 Project の skip は ProjectStore の責任）。
/// </para>
/// <para>
/// Create / Open は D4、Recording は D5、Review は D6 の範囲。ここでは実装しない。
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
    private readonly ObservableCollection<ProjectSummary> _projects = [];

    private bool _isLoading;
    private bool _hasLoaded;

    /// <summary>Status Bar 表示用の単純な通知。EventBus や Status Service は作らない。</summary>
    public event EventHandler<string>? StatusChanged;

    public ContentsView(ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);

        InitializeComponent();

        _projectStore = projectStore;
        ProjectGrid.ItemsSource = _projects;

        // 最初の navigation で直ちに読み込むため、初期表示は Loading にしておく。
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

    /// <summary>一覧を取得して表示を置き換える。失敗してもアプリを落とさない。</summary>
    private async Task LoadAsync()
    {
        // 連打・二重起動の防止（ボタン disable と二重の防御）
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

                // 可能なら同じ Project の選択を維持する（Rename 直後の体験のため）
                if (previousId is { } id)
                {
                    ProjectGrid.SelectedItem = _projects.FirstOrDefault(p => p.Id == id);
                }

                SetStatus($"{_projects.Count} 件のコンテンツ");
            }
        }
        catch (Exception ex)
        {
            // 詳細は通常 UI に出さない。Debug 時に追跡できるよう Trace へ残す。
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
    // Selection
    // ---------------------------------------------------------------------

    private void ProjectGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void UpdateButtons()
    {
        var hasSelection = ProjectGrid.SelectedItem is ProjectSummary;

        RefreshButton.IsEnabled = !_isLoading;
        RenameButton.IsEnabled = !_isLoading && hasSelection;
        DeleteButton.IsEnabled = !_isLoading && hasSelection;
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
            return; // Cancel（Dialog 側で blank は拒否されるため、ここへは正常値のみ来る）
        }

        if (dialog.ProjectTitle == selected.Title)
        {
            return;
        }

        try
        {
            // Directory 操作は Storage API の責任。UI からは触らない。
            await _projectStore.RenameProjectAsync(selected.Id, dialog.ProjectTitle);
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
            // Directory.Delete は呼ばない。削除は必ず ProjectStore を経由する。
            await _projectStore.DeleteProjectAsync(selected.Id);

            ProjectGrid.SelectedItem = null;
            await LoadAsync();
            SetStatus("削除しました");
        }
        catch (Exception ex)
        {
            Trace.TraceError("ContentsView: 削除に失敗しました: {0}", ex);
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
        EmptyText.Visibility = state == ContentsState.Empty ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Visibility = state == ContentsState.Error ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetStatus(string message) => StatusChanged?.Invoke(this, message);
}
