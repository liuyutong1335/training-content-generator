using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TrainingContent.App.Services;
using TrainingContent.App.State;

namespace TrainingContent.App.Views;

/// <summary>
/// 手順確認（Review）。Current Project の Steps を編集する（B1）。
///
/// <para>
/// 編集対象は detached draft（<see cref="ReviewDraft"/>) のみ。canonical な
/// <see cref="Core.Models.TrainingStep"/> の instance を editable state として保持せず、
/// editable control も draft にだけ TwoWay bind する。保存は
/// <see cref="ProjectWorkspace.UpdateReviewedStepsAsync"/> だけを通す
/// （この View から ProjectStore を直接呼ばない）。
/// </para>
/// <para>
/// 今回の範囲: Title / Description / Caution / ExpectedResult の編集、Step の削除、
/// ↑↓ による並べ替え、保存、dirty 表示、破棄。手動追加（B2）・Screenshot 差し替え（B3）・
/// Redaction（C）・StartMs/EndMs・Action/Target/ScreenshotPath/SourceEventIds の編集は持たない。
/// </para>
/// <para>
/// 意図的に持たないもの: MVVM framework / 独自の diff エンジン / path resolver /
/// 画像の読込 / 独自の永続化 / Dispatcher を跨ぐ非同期設計。
/// </para>
/// </summary>
public partial class ReviewView : UserControl
{
    private const string BlankTitleMessage = "タイトルを入力してください。";

    private const string SaveFailedMessage =
        "保存できませんでした。編集内容は保持しています。時間をおいて再度お試しください。";

    private const string SavedButNotRefreshedMessage =
        "保存しました。表示の更新に失敗したため、画面を切り替えて最新の内容をご確認ください。";

    private const string SaveInProgressMessage = "保存中です。完了後にもう一度お試しください。";

    private readonly CurrentProjectContext _currentProject;
    private readonly ProjectWorkspace _workspace;

    private ReviewDraft? _draft;
    private Guid? _draftProjectId;

    /// <summary>Save 実行中。Save 自身が起こす CurrentProjectChanged を external change と区別する。</summary>
    private bool _isSaving;

    /// <summary>external change の確認中（confirmation 中の再入で無限 refresh にしないための guard）。</summary>
    private bool _handlingExternalChange;

    public ReviewView(CurrentProjectContext currentProject, ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(workspace);

        InitializeComponent();

        _currentProject = currentProject;
        _workspace = workspace;

        _currentProject.CurrentProjectChanged += OnCurrentProjectChanged;

        RebuildAndRender(preserveSelection: false);
    }

    /// <summary>Save の結果などを MainWindow の Status 表示へ渡す。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>未保存の編集があるか（navigation / window close の保護に使う）。</summary>
    public bool HasUnsavedChanges => _draft?.IsDirty == true;

    /// <summary>保存処理が進行中か（この間は編集も離脱も受け付けない）。</summary>
    public bool IsSaving => _isSaving;

    /// <summary>
    /// Review から離れてよいかを確認する。dirty な draft があるときだけ確認し、
    /// 破棄に同意された場合は draft を捨てて最新の canonical を表示する。
    /// </summary>
    /// <returns>true = 離脱してよい（同意した場合は破棄済み）。false = 留まる（draft はそのまま）。</returns>
    public bool ConfirmDiscardIfNeeded()
    {
        // 保存中は破棄させない。保存は進行しているため「破棄しました」と表示しても実体は保存され、
        // 表示と状態が食い違う（保存完了後の再構築に任せる）。
        if (_isSaving)
        {
            SetStatus(SaveInProgressMessage);
            return false;
        }

        if (!HasUnsavedChanges)
        {
            return true;
        }

        if (ShowConfirm("未保存の変更があります。変更を破棄しますか？") != MessageBoxResult.OK)
        {
            return false;
        }

        RebuildAndRender(preserveSelection: false);
        SetStatus("変更を破棄しました。");
        return true;
    }

    // ---------------------------------------------------------------------
    // Current Project の反映
    // ---------------------------------------------------------------------

    /// <summary>
    /// Current Project が設定・更新・解除されたときの処理。
    ///
    /// <para>
    /// 未編集なら黙って最新 canonical を読み直す。dirty のまま外部から変更された場合は
    /// ユーザーの編集を silently discard せず、破棄の同意を取る。
    /// Save 自身が起こした変更（<see cref="_isSaving"/> 中）は external change として扱わない
    /// （draft の再構築は Save 成功後の処理が明示的に行う）。
    /// </para>
    /// </summary>
    private void OnCurrentProjectChanged(object? sender, EventArgs e)
    {
        if (_isSaving || _handlingExternalChange)
        {
            return;
        }

        if (!HasUnsavedChanges)
        {
            RebuildAndRender(preserveSelection: false);
            return;
        }

        _handlingExternalChange = true;
        try
        {
            if (ShowConfirm("未保存の変更があります。変更を破棄して表示を更新しますか？") == MessageBoxResult.OK)
            {
                RebuildAndRender(preserveSelection: false);
                SetStatus("表示を更新しました。");
            }
            else
            {
                SetStatus("未保存の変更を保持しています（表示は最新ではありません）。");
            }
        }
        finally
        {
            _handlingExternalChange = false;
        }
    }

    /// <summary>canonical から detached draft を作り直して再描画する。保存成功・破棄・外部変更で使う。</summary>
    private void RebuildAndRender(bool preserveSelection)
    {
        var previousStepId = preserveSelection && StepListBox.SelectedItem is ReviewDraftStep selected
            ? selected.StepId
            : (Guid?)null;

        var project = _currentProject.CurrentProject;

        // 新しい draft を先に作り、成功してから現在の状態と差し替える
        // （途中で例外が出ても、保持していた draft と編集内容を失わないため）。
        var newDraft = project is null || project.Steps.Count == 0 ? null : ReviewDraft.Create(project);

        if (_draft is not null)
        {
            _draft.Changed -= OnDraftChanged;
        }

        _draft = newDraft;
        _draftProjectId = project?.Id;

        if (newDraft is null)
        {
            StepListBox.ItemsSource = null;
            ShowPanel(project is null ? NoProjectPanel : NoStepsPanel);
            UpdateEditorFromSelection();
            UpdateCommandStates();
            return;
        }

        newDraft.Changed += OnDraftChanged;

        ShowPanel(ReviewPanel);
        StepListBox.ItemsSource = newDraft.Steps;

        // Project が変わった場合（previousStepId なし）は先頭、保存後などは同じ Step を選び直す。
        var index = previousStepId is { } stepId ? newDraft.IndexOf(stepId) : 0;
        StepListBox.SelectedIndex = index >= 0 ? index : 0;

        UpdateEditorFromSelection();
        UpdateCommandStates();
    }

    private void ShowPanel(FrameworkElement panel)
    {
        NoProjectPanel.Visibility = Visibility.Collapsed;
        NoStepsPanel.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------------
    // Selection / editor
    // ---------------------------------------------------------------------

    private void StepListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateEditorFromSelection();
        UpdateCommandStates();
    }

    /// <summary>
    /// 選択中の draft を editor / screenshot の binding source にする。
    /// canonical <c>TrainingStep</c> はここへ入れない（編集は draft にだけ行う）。
    /// </summary>
    private void UpdateEditorFromSelection()
    {
        var step = StepListBox.SelectedItem as ReviewDraftStep;

        ReviewPanel.DataContext = step;

        EditorContentPanel.Visibility = step is null ? Visibility.Collapsed : Visibility.Visible;
        EditorEmptyPanel.Visibility = step is null ? Visibility.Visible : Visibility.Collapsed;

        // 全件削除した状態では、保存で確定することを案内する。
        var draftIsEmpty = _draft is not null && _draft.Steps.Count == 0;
        EditorEmptyHint.Text = draftIsEmpty
            ? "「保存」すると、すべての手順を削除した状態が確定します。"
            : string.Empty;
        EditorEmptyHint.Visibility = draftIsEmpty ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCommandStates()
    {
        var index = StepListBox.SelectedIndex;
        var hasDraft = _draft is not null;
        var dirty = HasUnsavedChanges;
        var stepCount = hasDraft ? _draft!.Steps.Count : 0;

        MoveUpButton.IsEnabled = hasDraft && !_isSaving && _draft!.CanMoveUp(index);
        MoveDownButton.IsEnabled = hasDraft && !_isSaving && _draft!.CanMoveDown(index);
        DeleteStepButton.IsEnabled = hasDraft && !_isSaving && index >= 0 && index < stepCount;

        // clean のときは Save を押させない（実質変更なしの no-op は Storage 側でも保証される）。
        SaveButton.IsEnabled = dirty && !_isSaving;
        DiscardButton.IsEnabled = dirty && !_isSaving;

        // 保存中は編集を受け付けない。保存成功時の draft 再構築で、保存中に入った編集が
        // 黙って消えるのを防ぐ（TwoWay binding は 1 打鍵ごとに draft へ反映済みなので、
        // ここで無効化しても未確定の入力は残らない）。
        StepListBox.IsEnabled = !_isSaving;
        EditorContentPanel.IsEnabled = !_isSaving;
    }

    /// <summary>draft が変化した（dirty が動いた）ときの再評価。</summary>
    private void OnDraftChanged(object? sender, EventArgs e)
    {
        // 編集が再開されたので、直前の保存結果や validation の案内は残さない。
        SetStatus(string.Empty);
        UpdateCommandStates();
    }

    // ---------------------------------------------------------------------
    // 並べ替え / 削除
    // ---------------------------------------------------------------------

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(1);

    private void MoveSelected(int delta)
    {
        if (_draft is null)
        {
            return;
        }

        var index = StepListBox.SelectedIndex;
        var moved = delta < 0 ? _draft.MoveUp(index) : _draft.MoveDown(index);

        if (!moved)
        {
            return;
        }

        // 移動しても選択は同じ draft を指し続けるように、移動先を明示的に選ぶ。
        StepListBox.SelectedIndex = index + delta;
        UpdateCommandStates();
    }

    private void DeleteStep_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            return;
        }

        var index = StepListBox.SelectedIndex;
        if (index < 0)
        {
            return;
        }

        _draft.RemoveAt(index);

        // 削除後の選択: 次の Step、なければ前の Step、どちらも無ければ選択なし。
        StepListBox.SelectedIndex = _draft.Steps.Count == 0
            ? -1
            : Math.Min(index, _draft.Steps.Count - 1);

        UpdateEditorFromSelection();
        UpdateCommandStates();
    }

    // ---------------------------------------------------------------------
    // 保存 / 破棄
    // ---------------------------------------------------------------------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var draft = _draft;
        var projectId = _draftProjectId;

        if (draft is null || projectId is null || _isSaving)
        {
            return;
        }

        // 通常の入力エラーは UI 側で先に案内する（Storage boundary でも reject される）。
        if (draft.HasBlankTitle)
        {
            SetStatus(BlankTitleMessage);

            if (draft.FirstBlankTitleStepId is { } blankStepId)
            {
                var index = draft.IndexOf(blankStepId);
                if (index >= 0)
                {
                    StepListBox.SelectedIndex = index;
                }
            }

            TitleBox.Focus();
            return;
        }

        var updates = draft.BuildUpdates();

        // 全件削除は、この UI からは元に戻せない（手動追加 B2 が未実装のため）ので確認する。
        if (updates.Count == 0
            && ShowConfirm("手順をすべて削除します。この操作は元に戻せません。よろしいですか？") != MessageBoxResult.OK)
        {
            return;
        }

        var saved = false;

        // _isSaving を立てた後は、保存後の UI 更新も含めて必ず finally を通す
        // （フラグが立ったままになると Save / Discard が恒久的に無効化されるため）。
        _isSaving = true;

        try
        {
            SetStatus("保存しています…");
            UpdateCommandStates();

            await _workspace.UpdateReviewedStepsAsync(projectId.Value, updates);
            saved = true;
        }
        catch (Exception ex)
        {
            // 失敗: draft は保持し、dirty のままにする。Current Project は Storage が触っていないため旧状態。
            // raw exception / path / ex.Message は UI に出さない（Trace にのみ残す）。
            Trace.TraceError("ReviewView: Step の保存に失敗しました — {0}", ex);
            SetStatus(SaveFailedMessage);
            StatusChanged?.Invoke(this, SaveFailedMessage);
        }
        finally
        {
            _isSaving = false;
            UpdateCommandStates();
        }

        if (!saved)
        {
            return;
        }

        // 保存後の UI 更新は Storage の成功とは別の失敗面なので、ここで例外が出ても
        // 「保存に失敗しました」とは報告しない（書込みは既に成功している）。
        try
        {
            // 成功: Workspace が CurrentProject を差し替え済み。draft は最新 canonical から作り直す
            // （Save 自身が起こした CurrentProjectChanged は _isSaving で無視されている）。
            RebuildAndRender(preserveSelection: true);
            SetStatus("保存しました。");
            StatusChanged?.Invoke(this, "手順を保存しました");
        }
        catch (Exception ex)
        {
            Trace.TraceError("ReviewView: 保存後の表示更新に失敗しました — {0}", ex);
            SetStatus(SavedButNotRefreshedMessage);
            StatusChanged?.Invoke(this, SavedButNotRefreshedMessage);
        }
    }

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_draft is null)
        {
            return;
        }

        if (HasUnsavedChanges && ShowConfirm("未保存の変更を破棄して、保存済みの内容に戻しますか？") != MessageBoxResult.OK)
        {
            return;
        }

        RebuildAndRender(preserveSelection: false);
        SetStatus("変更を破棄しました。");
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private void SetStatus(string message) => ReviewStatusText.Text = message;

    /// <summary>OK / Cancel の確認。owner が取れる場合は window に従属させる。</summary>
    private MessageBoxResult ShowConfirm(string message)
    {
        const string caption = "手順の編集";
        var owner = Window.GetWindow(this);

        return owner is null
            ? MessageBox.Show(message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning)
            : MessageBox.Show(owner, message, caption, MessageBoxButton.OKCancel, MessageBoxImage.Warning);
    }
}
