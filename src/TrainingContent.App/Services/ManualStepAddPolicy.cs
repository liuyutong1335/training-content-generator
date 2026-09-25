namespace TrainingContent.App.Services;

/// <summary>
/// manual Step 追加（B2）の可否と anchor を決める pure helper。
///
/// <para>
/// 追加は <b>canonical mutation</b> なので、draft が dirty のままでは実行しない
/// （未保存の編集と canonical 挿入を競合させない。Redaction と同じ思想）。
/// 録画が無い Project には追加できない（timestamp の基準が無い）。
/// </para>
/// </summary>
/// <param name="Guidance">追加できない理由の user-facing message。追加可能なら null。</param>
/// <param name="AnchorStepId">
/// 挿入位置の基準。「選択中 Step の直後」なら選択中 Id、selection が無ければ null（= Steps が空なら最初、
/// それ以外は最後の後ろ）。
/// </param>
public readonly record struct ManualStepAddDecision(
    bool CanAdd,
    string? Guidance,
    Guid? AnchorStepId);

/// <inheritdoc cref="ManualStepAddDecision"/>
public static class ManualStepAddPolicy
{
    /// <summary>draft が dirty のときの案内（Redaction と同じ文言）。</summary>
    public const string DirtyDraftGuidance = "先に手順の変更を保存または破棄してください。";

    /// <summary>Recording が無いときの理由。</summary>
    public const string RecordingMissingGuidance = "録画がないため手順を追加できません。";

    /// <summary>Project が無いときの理由。</summary>
    public const string NoProjectGuidance = "プロジェクトが選択されていません。";

    /// <param name="hasProject">Current Project があるか（Steps が空でも追加できるため Project の有無で判定する）。</param>
    /// <param name="hasRecording">Project に Recording があるか。</param>
    /// <param name="isDirty">draft が dirty か。</param>
    /// <param name="isMutatingCanonical">保存 / redaction / manual add などの canonical mutation 中か。</param>
    /// <param name="selectedStepId">選択中 draft Step の Id（selection 無しは null）。</param>
    public static ManualStepAddDecision Resolve(
        bool hasProject,
        bool hasRecording,
        bool isDirty,
        bool isMutatingCanonical,
        Guid? selectedStepId)
    {
        if (!hasProject)
        {
            return new ManualStepAddDecision(false, NoProjectGuidance, null);
        }

        if (!hasRecording)
        {
            return new ManualStepAddDecision(false, RecordingMissingGuidance, null);
        }

        if (isMutatingCanonical)
        {
            // busy 中は理由を出さない（進行中の処理が status を持つ）。
            return new ManualStepAddDecision(false, null, null);
        }

        if (isDirty)
        {
            return new ManualStepAddDecision(false, DirtyDraftGuidance, null);
        }

        return new ManualStepAddDecision(true, null, selectedStepId);
    }
}
