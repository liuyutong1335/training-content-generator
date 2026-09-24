using System.ComponentModel;
using System.Runtime.CompilerServices;
using TrainingContent.Core.Models;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>
/// Review UI の 1 行分の <b>detached draft</b>。
///
/// <para>
/// canonical な <see cref="TrainingStep"/> の instance を editable state として
/// 保持しない。編集対象 4 項目（Title / Description / Caution / ExpectedResult）は値の copy として持ち、
/// 表示専用項目（Action / Target / StartMs / EndMs / ScreenshotPath）は読み取り専用で複製する。
/// この型をどう編集しても canonical Project は変化しない。
/// </para>
/// <para>
/// 編集対象 property は <see cref="INotifyPropertyChanged"/> を通知する。
/// <see cref="ReviewDraft"/> がこれを購読して dirty の再評価を伝えるためと、
/// UI の TwoWay binding（draft に対してのみ許可される）のため。
/// </para>
/// <para>
/// 意図的に持たないもの: canonical <c>TrainingStep</c> への参照 / ProjectStore /
/// 保存処理 / Order の永続値（表示順は <see cref="DisplayOrder"/> として UI 位置から与える）。
/// </para>
/// </summary>
public sealed class ReviewDraftStep : INotifyPropertyChanged
{
    private string _title;
    private string _description;
    private string _caution;
    private string _expectedResult;
    private int _displayOrder;

    /// <param name="step">複製元の canonical Step。参照は保持せず、値だけを copy する。</param>
    /// <param name="displayOrder">UI 上の並び順（1..N）。</param>
    public ReviewDraftStep(TrainingStep step, int displayOrder)
    {
        ArgumentNullException.ThrowIfNull(step);

        StepId = step.Id;

        _title = step.Title;
        _description = step.Description ?? string.Empty;
        _caution = step.Caution ?? string.Empty;
        _expectedResult = step.ExpectedResult ?? string.Empty;
        _displayOrder = displayOrder;

        // 表示専用（この View では編集しないため、複製して読み取り専用で公開する）。
        Action = step.Action;
        Target = step.Target;
        StartMs = step.StartMs;
        EndMs = step.EndMs;
        ScreenshotPath = step.ScreenshotPath;
    }

    /// <summary>canonical Step の Id（draft の identity。編集しても変化しない）。</summary>
    public Guid StepId { get; }

    // ---------------------------------------------------------------------
    // 編集対象（TwoWay binding してよいのは canonical Step ではなくこの draft に対して）
    // ---------------------------------------------------------------------

    /// <summary>タイトル。blank は Save 時に UI 側で reject される。</summary>
    public string Title
    {
        get => _title;
        set => SetField(ref _title, value ?? string.Empty);
    }

    /// <summary>説明。空白のみは保存時に canonical null へ正規化される（Storage の責務）。</summary>
    public string Description
    {
        get => _description;
        set => SetField(ref _description, value ?? string.Empty);
    }

    /// <summary>注意。空白のみは保存時に canonical null へ正規化される。</summary>
    public string Caution
    {
        get => _caution;
        set => SetField(ref _caution, value ?? string.Empty);
    }

    /// <summary>期待される結果。空白のみは保存時に canonical null へ正規化される。</summary>
    public string ExpectedResult
    {
        get => _expectedResult;
        set => SetField(ref _expectedResult, value ?? string.Empty);
    }

    /// <summary>UI 上の並び順（1..N）。<see cref="ReviewDraft"/> が collection の position から設定する。</summary>
    public int DisplayOrder
    {
        get => _displayOrder;
        internal set => SetField(ref _displayOrder, value);
    }

    // ---------------------------------------------------------------------
    // 表示専用（読み取りのみ）
    // ---------------------------------------------------------------------

    public string Action { get; }

    public string? Target { get; }

    public long StartMs { get; }

    public long? EndMs { get; }

    public string? ScreenshotPath { get; }

    public string ActionText => string.IsNullOrWhiteSpace(Action) ? "—" : Action;

    public string TargetText => string.IsNullOrWhiteSpace(Target) ? "—" : Target;

    public string StartMsText => $"{StartMs} ms";

    public string EndMsText => EndMs is { } end ? $"{end} ms" : "—";

    /// <summary>保存されている場合のみ値が入る（未設定なら空文字 = 表示上は何も出ない）。</summary>
    public string ScreenshotPathText => ScreenshotPath ?? string.Empty;

    public string ScreenshotPlaceholderText => string.IsNullOrWhiteSpace(ScreenshotPath)
        ? "スクリーンショットなし"
        : "画像プレビューは未対応です";

    /// <summary>
    /// Storage の write request へ変換する。Order は request list の位置で決まるため、
    /// この型では表現しない（canonical な Order を draft から直接書けない）。
    /// </summary>
    public StepReviewUpdate ToUpdate() => new(StepId, Title, Description, Caution, ExpectedResult);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
