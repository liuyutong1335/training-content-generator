using TrainingContent.Core.Models;

namespace TrainingContent.Storage;

/// <summary>
/// Content Manager 表示用の軽量サマリ（D3 で使用）。
/// Phase 0 Shared Contract の型ではないため Core.Models には置かず、
/// Storage 側の型として定義する。
/// </summary>
public sealed class ProjectSummary
{
    public Guid Id { get; init; }

    public string Title { get; init; } = "";

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    /// <summary>録画が未実施なら null。</summary>
    public long? DurationMs { get; init; }

    public int StepCount { get; init; }

    /// <summary>Manual 成果物の metadata があり、かつ実ファイルが存在する場合のみ true。</summary>
    public bool HasManual { get; init; }

    /// <summary>Video 成果物の metadata があり、かつ実ファイルが存在する場合のみ true。</summary>
    public bool HasVideo { get; init; }

    /// <summary>
    /// TrainingProject から summary を作る。
    /// <paramref name="artifactExists"/> は Project 相対パスを受け取り、実ファイルの有無を返す。
    /// </summary>
    internal static ProjectSummary From(TrainingProject project, Func<string, bool> artifactExists)
    {
        var outputs = project.Outputs;

        return new ProjectSummary
        {
            Id = project.Id,
            Title = project.Title,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc,
            DurationMs = project.Recording?.DurationMs,
            StepCount = project.Steps.Count,
            HasManual =
                (outputs.ManualMarkdown is { } md && artifactExists(md.Path)) ||
                (outputs.ManualHtml is { } html && artifactExists(html.Path)),
            HasVideo = outputs.TrainingVideo is { } video && artifactExists(video.Path),
        };
    }
}
