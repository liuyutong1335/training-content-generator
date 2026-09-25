using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Storage;

namespace TrainingContent.App.Views;

/// <summary>
/// 手順確認（Review）。Current Project の Steps を編集し（B1）、Screenshot の BlackBox redaction（C）を行う。
///
/// <para>
/// 編集対象は detached draft（<see cref="ReviewDraft"/>) のみ。canonical な
/// <see cref="Core.Models.TrainingStep"/> の instance を editable state として保持せず、
/// editable control も draft にだけ TwoWay bind する。保存は
/// <see cref="ProjectWorkspace.UpdateReviewedStepsAsync"/> だけを通す
/// （この View から ProjectStore を直接呼ばない）。
/// </para>
/// <para>
/// Redaction は <see cref="ScreenshotRedactionCoordinator"/> に委譲する。この View は
/// preview の表示・選択矩形（control 座標）の取得・結果の反映だけを行い、
/// path 解決 / 出力命名 / Storage 更新 / 画像処理は持たない。
/// </para>
/// <para>
/// 範囲: Title / Description / Caution / ExpectedResult の編集、Step の削除、↑↓ による並べ替え、
/// 保存、dirty 表示、破棄、screenshot preview、矩形選択と BlackBox redaction。
/// 手動追加（B2）・Screenshot 差し替え（B3）・StartMs/EndMs・Action/Target/ScreenshotPath/SourceEventIds の編集、
/// BlackBox 以外の redaction（Pixelate / Blur / marker）は持たない。
/// </para>
/// <para>
/// 意図的に持たないもの: MVVM framework / 独自の diff エンジン / 独自の path 解決 / 独自の永続化 /
/// selection resize handle / undo history / Dispatcher を跨ぐ非同期設計。
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

    /// <summary>未保存の編集がある状態で redaction を要求されたときの案内（§11）。</summary>
    private const string DirtyDraftGuidance = "先に手順の変更を保存または破棄してください。";

    private const string NoScreenshotMessage = "スクリーンショットなし";
    private const string ScreenshotUnavailableMessage = "スクリーンショットを表示できません。";

    private readonly CurrentProjectContext _currentProject;
    private readonly ProjectWorkspace _workspace;
    private readonly ScreenshotRedactionCoordinator _screenshotRedaction;

    private ReviewDraft? _draft;
    private Guid? _draftProjectId;

    /// <summary>
    /// canonical mutation（project.json 保存 / Screenshot redaction）実行中。
    /// 自身が起こす CurrentProjectChanged を external change と区別し、この間は編集・離脱・再実行を受け付けない。
    /// </summary>
    private bool _isMutatingCanonical;

    /// <summary>external change の確認中（confirmation 中の再入で無限 refresh にしないための guard）。</summary>
    private bool _handlingExternalChange;

    // ---- Screenshot preview / redaction 選択（control 座標で保持し、実行時に bitmap pixel へ変換する） ----
    private int _previewBitmapWidth;
    private int _previewBitmapHeight;
    private Point? _dragStart;
    private ScreenshotRect? _selection;

    public ReviewView(
        CurrentProjectContext currentProject,
        ProjectWorkspace workspace,
        ScreenshotRedactionCoordinator screenshotRedaction)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(screenshotRedaction);

        InitializeComponent();

        _currentProject = currentProject;
        _workspace = workspace;
        _screenshotRedaction = screenshotRedaction;

        _currentProject.CurrentProjectChanged += OnCurrentProjectChanged;

        RebuildAndRender(preserveSelection: false);
    }

    /// <summary>Save の結果などを MainWindow の Status 表示へ渡す。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>未保存の編集があるか（navigation / window close の保護に使う）。</summary>
    public bool HasUnsavedChanges => _draft?.IsDirty == true;

    /// <summary>
    /// canonical mutation（project.json 保存 / Screenshot redaction）の進行中か。
    /// この間は編集・離脱・再実行を受け付けない（<see cref="MainWindow"/> の close 拒否にも使う）。
    /// </summary>
    public bool IsSaving => _isMutatingCanonical;

    /// <summary>
    /// Review から離れてよいかを確認する。dirty な draft があるときだけ確認し、
    /// 破棄に同意された場合は draft を捨てて最新の canonical を表示する。
    /// </summary>
    /// <returns>true = 離脱してよい（同意した場合は破棄済み）。false = 留まる（draft はそのまま）。</returns>
    public bool ConfirmDiscardIfNeeded()
    {
        // 保存中は破棄させない。保存は進行しているため「破棄しました」と表示しても実体は保存され、
        // 表示と状態が食い違う（保存完了後の再構築に任せる）。
        if (_isMutatingCanonical)
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
    /// Save 自身が起こした変更（<see cref="_isMutatingCanonical"/> 中）は external change として扱わない
    /// （draft の再構築は Save 成功後の処理が明示的に行う）。
    /// </para>
    /// </summary>
    private void OnCurrentProjectChanged(object? sender, EventArgs e)
    {
        if (_isMutatingCanonical || _handlingExternalChange)
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
    /// <param name="selectStepId">
    /// 指定した場合はその Step を選択する（manual Step 追加直後に新 Step を選ぶため）。
    /// </param>
    private void RebuildAndRender(bool preserveSelection, Guid? selectStepId = null)
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

        // 明示指定 > 直前の選択 > 先頭 の順で選ぶ。
        var targetStepId = selectStepId ?? previousStepId;
        var index = targetStepId is { } stepId ? newDraft.IndexOf(stepId) : 0;
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

        UpdateScreenshotPreview(step);

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

        MoveUpButton.IsEnabled = hasDraft && !_isMutatingCanonical && _draft!.CanMoveUp(index);
        MoveDownButton.IsEnabled = hasDraft && !_isMutatingCanonical && _draft!.CanMoveDown(index);
        DeleteStepButton.IsEnabled = hasDraft && !_isMutatingCanonical && index >= 0 && index < stepCount;

        // manual Step 追加（B2）は canonical mutation。可否は pure helper が持つ
        // （dirty / busy / Recording 無しでは押させない）。Steps が空でも追加できる。
        var addDecision = ResolveManualStepAddDecision();
        AddStepButton.IsEnabled = addDecision.CanAdd;
        NoStepsAddStepButton.IsEnabled = addDecision.CanAdd;

        // clean のときは Save を押させない（実質変更なしの no-op は Storage 側でも保証される）。
        SaveButton.IsEnabled = dirty && !_isMutatingCanonical;
        DiscardButton.IsEnabled = dirty && !_isMutatingCanonical;

        // 保存 / redaction 中は編集を受け付けない。canonical mutation 中の入力が
        // 成功後の draft 再構築で黙って消えるのを防ぐ（TwoWay binding は 1 打鍵ごとに draft へ
        // 反映済みなので、ここで無効化しても未確定の入力は残らない）。
        StepListBox.IsEnabled = !_isMutatingCanonical;
        EditorContentPanel.IsEnabled = !_isMutatingCanonical;

        // redaction は canonical mutation 中と selection 無効時には押させない。
        // （dirty のときは「先に保存または破棄」を案内するため押せるままにする）
        UpdateRedactAvailability();
    }

    /// <summary>Redaction button / selection 操作の可否を selection と busy state から決める。</summary>
    private void UpdateRedactAvailability()
    {
        var canSelect = !_isMutatingCanonical && _previewBitmapWidth > 0 && _previewBitmapHeight > 0;

        SelectionOverlay.IsEnabled = canSelect;
        RedactButton.IsEnabled = canSelect && _selection is not null;
        ClearSelectionButton.IsEnabled = canSelect && _selection is not null;
    }

    // ---------------------------------------------------------------------
    // Manual Step 追加（B2）
    // ---------------------------------------------------------------------

    private ManualStepAddDecision ResolveManualStepAddDecision()
    {
        var project = _currentProject.CurrentProject;
        var selectedStepId = (StepListBox.SelectedItem as ReviewDraftStep)?.StepId;

        return ManualStepAddPolicy.Resolve(
            hasProject: project is not null,
            hasRecording: project?.Recording is not null,
            isDirty: HasUnsavedChanges,
            isMutatingCanonical: _isMutatingCanonical,
            selectedStepId: selectedStepId);
    }

    /// <summary>
    /// manual Step を選択中 Step の直後（selection が無ければ最後の後ろ、Steps が空なら最初）へ追加する。
    ///
    /// <para>
    /// draft 内に仮追加はしない（既存 <c>StepReviewUpdate</c> は existing Step edit 専用で unknown StepId を
    /// reject する契約のため）。Dialog → independent canonical mutation → draft rebuild の順で行う。
    /// </para>
    /// </summary>
    private async void AddStep_Click(object sender, RoutedEventArgs e)
    {
        var project = _currentProject.CurrentProject;
        var projectId = _draftProjectId;

        var decision = ResolveManualStepAddDecision();
        if (!decision.CanAdd || project is null || projectId is null)
        {
            if (decision.Guidance is { } guidance)
            {
                SetStatus(guidance);
            }

            return;
        }

        var dialog = new AddManualStepWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true)
        {
            return; // Cancel
        }

        _isMutatingCanonical = true;
        UpdateCommandStates();

        try
        {
            var result = await _workspace.InsertManualStepAsync(
                projectId.Value, decision.AnchorStepId, dialog.StepTitle);

            if (result.Succeeded && result.StepId is { } newStepId)
            {
                // canonical が更新されているので draft を作り直し、新しい Step を選択して editor を出す。
                RebuildAndRender(preserveSelection: false, selectStepId: newStepId);
                SetStatus("手順を追加しました。");
                return;
            }

            SetStatus(DescribeManualStepInsertFailure(result.Status));
        }
        catch (ProjectStoreException ex)
        {
            Trace.TraceError("ReviewView: manual Step の追加に失敗しました — {0}", ex);
            SetStatus("手順を追加できませんでした。");
        }
        finally
        {
            _isMutatingCanonical = false;
            UpdateCommandStates();
        }
    }

    /// <summary>manual Step 追加の failure を user-facing message にする（raw exception は出さない）。</summary>
    private static string DescribeManualStepInsertFailure(ManualStepInsertStatus status) => status switch
    {
        ManualStepInsertStatus.RecordingMissing => ManualStepAddPolicy.RecordingMissingGuidance,
        ManualStepInsertStatus.AnchorNotFound =>
            "選択中の手順が見つかりません。手順一覧を選び直してください。",
        ManualStepInsertStatus.NoTimeSpace =>
            "この位置には時間上の余裕がないため手順を追加できません。",
        ManualStepInsertStatus.InvalidTitle => "タイトルを入力してください。",
        _ => "手順を追加できませんでした。",
    };

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

        if (draft is null || projectId is null || _isMutatingCanonical)
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

        // _isMutatingCanonical を立てた後は、保存後の UI 更新も含めて必ず finally を通す
        // （フラグが立ったままになると Save / Discard が恒久的に無効化されるため）。
        _isMutatingCanonical = true;

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
            _isMutatingCanonical = false;
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
            // （Save 自身が起こした CurrentProjectChanged は _isMutatingCanonical で無視されている）。
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
    // Screenshot preview / redaction 選択
    // ---------------------------------------------------------------------

    /// <summary>
    /// 選択中 Step の ScreenshotPath を preview に読み込む。読み込めない場合は generic message を表示し、
    /// redaction を無効化する（absolute path / exception message は UI に出さない）。
    /// </summary>
    private void UpdateScreenshotPreview(ReviewDraftStep? step)
    {
        ClearSelection();
        ScreenshotImage.Source = null;
        _previewBitmapWidth = 0;
        _previewBitmapHeight = 0;
        ScreenshotMessageText.Text = string.Empty;
        ScreenshotStatusText.Text = string.Empty;

        if (step is null || _draftProjectId is not { } projectId)
        {
            UpdateRedactAvailability();
            return;
        }

        if (string.IsNullOrWhiteSpace(step.ScreenshotPath))
        {
            ScreenshotMessageText.Text = NoScreenshotMessage;
            UpdateRedactAvailability();
            return;
        }

        // path 解決は App 側 boundary（coordinator → resolver）に集約する。rooted / '..' / 非 png はここで弾かれる。
        var resolved = _screenshotRedaction.ResolveScreenshotPath(projectId, step.ScreenshotPath);
        if (!resolved.Succeeded || resolved.AbsolutePath is null || !File.Exists(resolved.AbsolutePath))
        {
            Trace.TraceWarning("ReviewView: screenshot を解決できません — {0}", resolved.ErrorMessage);
            ScreenshotMessageText.Text = ScreenshotUnavailableMessage;
            UpdateRedactAvailability();
            return;
        }

        var bitmap = TryLoadBitmap(resolved.AbsolutePath);
        if (bitmap is null)
        {
            ScreenshotMessageText.Text = ScreenshotUnavailableMessage;
            UpdateRedactAvailability();
            return;
        }

        ScreenshotImage.Source = bitmap;
        _previewBitmapWidth = bitmap.PixelWidth;
        _previewBitmapHeight = bitmap.PixelHeight;
        UpdateRedactAvailability();
    }

    /// <summary>
    /// file lock を残さない読み込み。<see cref="BitmapCacheOption.OnLoad"/> で stream を閉じた後も表示できる。
    /// 失敗は <c>null</c>（呼出側が generic message を出す）。
    /// </summary>
    private static BitmapImage? TryLoadBitmap(string absolutePath)
    {
        try
        {
            using var stream = new FileStream(absolutePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            Trace.TraceWarning("ReviewView: screenshot を読み込めません — {0}", ex.GetType().Name);
            return null;
        }
    }

    private void SelectionOverlay_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_isMutatingCanonical || _previewBitmapWidth <= 0 || _previewBitmapHeight <= 0)
        {
            return;
        }

        _dragStart = e.GetPosition(SelectionOverlay);
        _selection = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
        UpdateRedactAvailability();
        SelectionOverlay.CaptureMouse();
    }

    private void SelectionOverlay_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start || !SelectionOverlay.IsMouseCaptured)
        {
            return;
        }

        UpdateSelectionRectangle(start, e.GetPosition(SelectionOverlay));
    }

    private void SelectionOverlay_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragStart is not { } start)
        {
            return;
        }

        SelectionOverlay.ReleaseMouseCapture();
        _dragStart = null;

        var end = e.GetPosition(SelectionOverlay);
        _selection = NormalizeSelection(start, end);
        UpdateSelectionRectangle(start, end);
        UpdateRedactAvailability();
    }

    /// <summary>drag 中の矩形表示のみを更新する（確定は mouse release 時）。</summary>
    private void UpdateSelectionRectangle(Point start, Point end)
    {
        var rect = NormalizeSelection(start, end);

        Canvas.SetLeft(SelectionRectangle, rect.X);
        Canvas.SetTop(SelectionRectangle, rect.Y);
        SelectionRectangle.Width = rect.Width;
        SelectionRectangle.Height = rect.Height;
        SelectionRectangle.Visibility = rect.Width > 0 && rect.Height > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>右下 / 左上どちらの drag でも正の幅・高さになるよう正規化する。</summary>
    private static ScreenshotRect NormalizeSelection(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        ClearSelection();
        UpdateRedactAvailability();
    }

    /// <summary>選択状態を破棄する（Step 切替・redaction 成功後・破棄時にも呼ぶ）。</summary>
    private void ClearSelection()
    {
        _dragStart = null;
        _selection = null;
        SelectionRectangle.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 選択範囲を BlackBox redaction し、新しい edited PNG 経由で ScreenshotPath を更新する。
    /// 実際の処理（path 解決 / 出力命名 / Storage 更新）は <see cref="ScreenshotRedactionCoordinator"/> が持つ。
    /// </summary>
    private async void Redact_Click(object sender, RoutedEventArgs e)
    {
        var step = StepListBox.SelectedItem as ReviewDraftStep;
        var projectId = _draftProjectId;

        if (step is null || projectId is null || _isMutatingCanonical)
        {
            return;
        }

        // 未保存の text edit があると、ScreenshotPath update が CurrentProject を更新したときに
        // draft と canonical の ownership が競合する。先に保存 / 破棄を促す。
        if (HasUnsavedChanges)
        {
            SetScreenshotStatus(DirtyDraftGuidance);
            return;
        }

        if (_selection is not { } selection)
        {
            SetScreenshotStatus("黒塗りする範囲を画像上でドラッグして選んでください。");
            return;
        }

        // control 座標 → 実 bitmap pixel（letterbox と はみ出しは intersection で吸収する）。
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            _previewBitmapWidth,
            _previewBitmapHeight,
            SelectionOverlay.ActualWidth,
            SelectionOverlay.ActualHeight,
            selection);

        if (region is null)
        {
            SetScreenshotStatus("画像の範囲を選んでください。");
            return;
        }

        _isMutatingCanonical = true;
        SetScreenshotStatus("黒塗りしています…");
        UpdateCommandStates();

        try
        {
            var outcome = await _screenshotRedaction.RedactAsync(
                projectId.Value, step.StepId, step.ScreenshotPath, region);

            SetScreenshotStatus(outcome.Message);

            if (outcome.Succeeded)
            {
                // canonical が更新されているので draft を作り直す（preview も新 path で再読込され、選択は clear される）。
                RebuildAndRender(preserveSelection: true);
                SetStatus("スクリーンショットを更新しました");
            }
            else
            {
                StatusChanged?.Invoke(this, outcome.Message);
            }
        }
        finally
        {
            _isMutatingCanonical = false;
            UpdateCommandStates();
        }
    }

    private void SetScreenshotStatus(string message) => ScreenshotStatusText.Text = message;

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
