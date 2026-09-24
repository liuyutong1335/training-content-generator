using TrainingContent.Core.Models;

namespace TrainingContent.Video;

/// <summary>
/// 合成リクエストの入力 guard（監査 m-5 対応）。
/// 契約 §6.2 では Recording は Optional だが、Video の入力は契約 §28 どおり
/// Recording + TrainingProject である。Recording が無いと Step 区間がすべて
/// Duration クランプで消失し、「字幕 0 件の動画が成功として返る」ため、合成前に拒否する。
/// </summary>
public static class CompositionInputGuard
{
    /// <summary>Project.Recording が無い場合に合成を拒否する（契約 §7 の RecordingInfo が必要）。</summary>
    public static void EnsureRecordingPresent(TrainingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Recording is null)
        {
            throw new ArgumentException(
                "Project.Recording が null です。動画合成には契約 §7 の RecordingInfo（録画 DurationMs）が必要です。");
        }
    }
}
