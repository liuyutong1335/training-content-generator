using TrainingContent.Core.Models;

namespace TrainingContent.Video.Timeline;

/// <summary>1 Step 分の表示区間（Canonical Timeline の ms）。ASS の Dialogue 時刻にそのまま使う。</summary>
public sealed record StepDisplayInterval(TrainingStep Step, long StartMs, long EndMs);

/// <summary>
/// TrainingStep 列 → 表示区間への変換（開発計画書 §12 Timeline 相当）。
/// 区間規則は契約 §14 に基づく:
/// - EndMs あり → [StartMs, EndMs] をそのまま使う
/// - EndMs=null（単発操作）→ [StartMs, 次 Step の StartMs]（最終 Step は fallback 秒）
/// - 区間は録画 Duration にクランプし、Duration を超える Step はスキップする
/// Canonical 0ms == MP4 0 秒（CaptureStarted 同期）が前提のため、シフトは行わない。
/// </summary>
public static class StepTimelineBuilder
{
    /// <summary>区間が壊れていた場合の最低表示時間（ms）。startMs == endMs は契約 §14 が禁止しているが、耐える。</summary>
    public const long MinimumDisplayMs = 500;

    public static IReadOnlyList<StepDisplayInterval> Build(
        IReadOnlyList<TrainingStep> steps,
        long recordingDurationMs,
        double fallbackSeconds = 4.0)
    {
        ArgumentNullException.ThrowIfNull(steps);
        var result = new List<StepDisplayInterval>(steps.Count);

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.StartMs >= recordingDurationMs)
            {
                continue; // 録画の外側（契約 §29 違反に対する耐性）
            }

            var end = step.EndMs
                      ?? (i + 1 < steps.Count ? steps[i + 1].StartMs : step.StartMs + (long)(fallbackSeconds * 1000));
            if (end <= step.StartMs)
            {
                end = step.StartMs + MinimumDisplayMs;
            }

            if (end > recordingDurationMs)
            {
                end = recordingDurationMs;
            }

            if (end <= step.StartMs)
            {
                continue;
            }

            result.Add(new StepDisplayInterval(step, step.StartMs, end));
        }

        return result;
    }
}
