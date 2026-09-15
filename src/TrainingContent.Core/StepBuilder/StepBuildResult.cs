using TrainingContent.Core.Models;

namespace TrainingContent.Core;

/// <summary>
/// StepBuilder の実行結果（契約 §26）。
/// Error が 1 件でもある場合 <see cref="Steps"/> は空であり、部分的な TrainingStep を返さない。
/// Warning は処理を継続した事実（Unknown Event / sensitive textEntry / MVP リスト外の key）。
/// </summary>
public sealed class StepBuildResult
{
    public IReadOnlyList<TrainingStep> Steps { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}
