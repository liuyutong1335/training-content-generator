namespace TrainingContent.Manual;

/// <summary>
/// Manual 生成の入力となる表示用ドキュメント（docs/development-plan.md §19、phase0-contract.md §6 / §12）。
/// TrainingProject のうち Manual の表示に必要な項目だけを持つスナップショット。
/// <para>
/// 含めない項目: Target（Step.Title と重複するため）、SourceEventIds、StartMs / EndMs、
/// Recording、ProjectOutputs、Revision、CreatedAtUtc / UpdatedAtUtc。
/// Optional 項目は null / 空を null として保持し、文章の補完はしない。
/// Markdown / HTML の文言・エスケープは Writer 側の責務。
/// </para>
/// </summary>
public sealed class ManualDocument
{
    public string Title { get; init; } = "";

    public string? Objective { get; init; }

    public string? TargetAudience { get; init; }

    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    public IReadOnlyList<ManualStep> Steps { get; init; } = [];
}

/// <summary>
/// Manual の 1 手順（§19 の Step 表示項目: Screenshot / Description / Caution / Expected Result）。
/// Target・SourceEventIds・timestamp は Manual の表示対象外のため含めない。
/// </summary>
public sealed class ManualStep
{
    public int Order { get; init; }

    public string Title { get; init; } = "";

    public string? Description { get; init; }

    public string? Caution { get; init; }

    public string? ExpectedResult { get; init; }

    /// <summary>
    /// Project-relative のパス（契約 §17 / §18）。未設定は null。値の変換はしない。
    /// Writer（C2-2 以降）では、manual/manual.md・manual/manual.html からの相対参照として
    /// <c>../{ScreenshotPath}</c> の形式で出力する（確定事項。本 Gate では未実装）。
    /// </summary>
    public string? ScreenshotPath { get; init; }
}
