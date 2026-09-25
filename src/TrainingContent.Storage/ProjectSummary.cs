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

    /// <summary>
    /// Manual 成果物（Markdown + HTML の pair）の metadata と実ファイルが揃っている場合のみ true。
    /// 意味は <see cref="ManualStatus"/> != <see cref="ArtifactGenerationState.Missing"/> と一致する
    /// （旧「どちらか 1 件あれば true」の OR semantics は廃止）。
    /// </summary>
    public bool HasManual { get; init; }

    /// <summary>Video 成果物の metadata があり、かつ実ファイルが存在する場合のみ true。</summary>
    public bool HasVideo { get; init; }

    /// <summary>
    /// Manual 成果物の 3 値 status。Manual は Markdown + HTML の pair なので、
    /// metadata・実ファイル・<c>SourceRevision</c> のいずれかが pair で揃わない場合は Missing / Stale になる。
    /// </summary>
    public ArtifactGenerationState ManualStatus { get; init; }

    /// <summary>
    /// Video 成果物の 3 値 status。Contract §16 の <c>SourceRevision != Revision</c> → stale に従う。
    /// <see cref="HasVideo"/> は互換のため残しているが、UI の判定はこちらを使う。
    /// </summary>
    public ArtifactGenerationState VideoStatus { get; init; }

    /// <summary>
    /// TrainingProject から summary を作る。
    /// <paramref name="artifactExists"/> は Project 相対パスを受け取り、実ファイルの有無を返す。
    /// </summary>
    internal static ProjectSummary From(TrainingProject project, Func<string, bool> artifactExists)
    {
        var outputs = project.Outputs;

        // Video は artifact が 1 つなので metadata と実ファイルと Revision だけで判定できる。
        // SourceRevision > Revision の異常値も「一致しない」ので Stale になる。
        var video = outputs.TrainingVideo;
        var videoStatus = video is null || !artifactExists(video.Path)
            ? ArtifactGenerationState.Missing
            : video.SourceRevision == project.Revision
                ? ArtifactGenerationState.Current
                : ArtifactGenerationState.Stale;

        // Manual は pair。metadata が片方でも欠ける / 実ファイルが片方でも無い場合は Missing とし、
        // 「片方だけ current」を正常状態として扱わない。pair はあるが Revision が食い違う場合は Stale。
        var markdown = outputs.ManualMarkdown;
        var html = outputs.ManualHtml;
        var manualStatus =
            markdown is null || html is null
                ? ArtifactGenerationState.Missing
                : !artifactExists(markdown.Path) || !artifactExists(html.Path)
                    ? ArtifactGenerationState.Missing
                    : markdown.SourceRevision == html.SourceRevision
                      && markdown.SourceRevision == project.Revision
                        ? ArtifactGenerationState.Current
                        : ArtifactGenerationState.Stale;

        return new ProjectSummary
        {
            Id = project.Id,
            Title = project.Title,
            CreatedAtUtc = project.CreatedAtUtc,
            UpdatedAtUtc = project.UpdatedAtUtc,
            DurationMs = project.Recording?.DurationMs,
            StepCount = project.Steps.Count,
            HasManual = manualStatus != ArtifactGenerationState.Missing,
            HasVideo = videoStatus != ArtifactGenerationState.Missing,
            ManualStatus = manualStatus,
            VideoStatus = videoStatus,
        };
    }
}

/// <summary>
/// 生成 artifact の状態。Shared Contract の型ではない（Storage 側の表示用）。
/// </summary>
public enum ArtifactGenerationState
{
    /// <summary>metadata が無い、または metadata はあるが実ファイルが無い。</summary>
    Missing,

    /// <summary>metadata と実ファイルがあり、<c>SourceRevision == Revision</c>。</summary>
    Current,

    /// <summary>metadata と実ファイルがあり、<c>SourceRevision != Revision</c>（要再生成）。</summary>
    Stale,
}
