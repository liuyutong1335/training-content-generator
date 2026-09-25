namespace TrainingContent.App.Services;

/// <summary>
/// screenshot import / replacement（B3）の可否と button label を決める pure helper。
///
/// <para>
/// import も canonical mutation なので、draft が dirty のままでは dialog すら開かない
/// （Redaction / B2 と同じ policy）。selection が無い Step には適用できない。
/// </para>
/// </summary>
/// <param name="Guidance">実行できない理由の user-facing message。実行可能なら null。</param>
/// <param name="ButtonLabel">現在の ScreenshotPath に応じた label（null → 追加 / それ以外 → 差し替え）。</param>
public readonly record struct ScreenshotReplacementDecision(
    bool CanReplace,
    string? Guidance,
    string ButtonLabel);

/// <inheritdoc cref="ScreenshotReplacementDecision"/>
public static class ScreenshotReplacementPolicy
{
    public const string AddLabel = "画像を追加";

    public const string ReplaceLabel = "画像を差し替え";

    /// <param name="hasSelectedStep">Screenshot の対象になる Step が選択されているか。</param>
    /// <param name="isDirty">draft が dirty か。</param>
    /// <param name="isMutatingCanonical">保存 / redaction / replacement などの canonical mutation 中か。</param>
    /// <param name="currentScreenshotPath">選択中 Step の現在の ScreenshotPath（null / blank = 未設定）。</param>
    public static ScreenshotReplacementDecision Resolve(
        bool hasSelectedStep,
        bool isDirty,
        bool isMutatingCanonical,
        string? currentScreenshotPath)
    {
        var label = string.IsNullOrWhiteSpace(currentScreenshotPath) ? AddLabel : ReplaceLabel;

        if (!hasSelectedStep)
        {
            return new ScreenshotReplacementDecision(false, null, label);
        }

        if (isMutatingCanonical)
        {
            // busy 中は理由を出さない（進行中の処理が status を持つ）。既存 canonical mutation state を使う。
            return new ScreenshotReplacementDecision(false, null, label);
        }

        if (isDirty)
        {
            return new ScreenshotReplacementDecision(false, ManualStepAddPolicy.DirtyDraftGuidance, label);
        }

        return new ScreenshotReplacementDecision(true, null, label);
    }
}
