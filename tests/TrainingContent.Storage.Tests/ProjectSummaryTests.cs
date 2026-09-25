using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// D6-V2 <see cref="ProjectSummary.VideoStatus"/> のテスト。
/// Contract §16 の <c>SourceRevision != Revision</c> → stale を一覧表示用に写したもの。
/// </summary>
public class ProjectSummaryTests
{
    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; }

        public TempProjectsRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "tc-storage-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string CanonicalVideoPath(ProjectStore store, Guid projectId) =>
        Path.Combine(store.GetProjectDirectory(projectId), "output", "training_video.mp4");

    /// <summary>Video artifact の有無 / Revision 差を指定して Project を作る。</summary>
    private static async Task<TrainingProject> CreateProjectAsync(
        ProjectStore store,
        bool withMetadata = true,
        bool withFile = true,
        int sourceRevisionOffset = 0)
    {
        var project = await store.CreateProjectAsync("動画つき教材");

        if (withMetadata)
        {
            project.Outputs.TrainingVideo = new GeneratedArtifact
            {
                Path = VideoArtifactTransaction.CanonicalRelativePath,
                GeneratedAtUtc = DateTimeOffset.UtcNow,
                SourceRevision = project.Revision + sourceRevisionOffset,
            };
            await store.SaveProjectAsync(project);
        }

        if (withFile)
        {
            await File.WriteAllBytesAsync(CanonicalVideoPath(store, project.Id), new byte[] { 1, 2, 3 });
        }

        return project;
    }

    private static async Task<ProjectSummary> SummaryOfAsync(ProjectStore store, Guid projectId)
    {
        var list = await store.ListProjectsAsync();
        return list.Single(s => s.Id == projectId);
    }

    // =====================================================================
    // 1. metadata なし → Missing
    // =====================================================================
    [Fact]
    public async Task T_D6V2_01_VideoStatus_WithoutMetadata_IsMissing()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store, withMetadata: false, withFile: false);

        var summary = await SummaryOfAsync(store, project.Id);

        Assert.Equal(ArtifactGenerationState.Missing, summary.VideoStatus);
        Assert.False(summary.HasVideo);
    }

    // =====================================================================
    // 2. metadata あり / 実ファイルなし → Missing
    // =====================================================================
    [Fact]
    public async Task T_D6V2_02_VideoStatus_MetadataWithoutFile_IsMissing()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store, withMetadata: true, withFile: false);

        var summary = await SummaryOfAsync(store, project.Id);

        Assert.Equal(ArtifactGenerationState.Missing, summary.VideoStatus);
        Assert.False(summary.HasVideo);
    }

    // =====================================================================
    // 3. metadata + file あり / SourceRevision == Revision → Current
    // =====================================================================
    [Fact]
    public async Task T_D6V2_03_VideoStatus_MatchingRevision_IsCurrent()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);

        var summary = await SummaryOfAsync(store, project.Id);

        Assert.Equal(ArtifactGenerationState.Current, summary.VideoStatus);
        Assert.True(summary.HasVideo);
    }

    // =====================================================================
    // 4. metadata + file あり / SourceRevision != Revision → Stale
    // =====================================================================
    [Theory]
    [InlineData(1)]   // 古い Revision の動画（編集後に再生成していない）
    [InlineData(-1)]  // SourceRevision > Revision の異常値も Stale 扱い
    public async Task T_D6V2_04_VideoStatus_DifferentRevision_IsStale(int offset)
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store, sourceRevisionOffset: offset);

        var summary = await SummaryOfAsync(store, project.Id);

        Assert.Equal(ArtifactGenerationState.Stale, summary.VideoStatus);
    }

    // =====================================================================
    // 5. Stale でも実ファイルがあれば HasVideo == true（互換維持）
    // =====================================================================
    [Fact]
    public async Task T_D6V2_05_VideoStatus_Stale_KeepsHasVideoTrue()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store, sourceRevisionOffset: 1);

        var summary = await SummaryOfAsync(store, project.Id);

        Assert.Equal(ArtifactGenerationState.Stale, summary.VideoStatus);
        Assert.True(summary.HasVideo);
    }

    // =====================================================================
    // 6. Rename（Revision++）だけで Stale になる — 追加 mutation は不要
    // =====================================================================
    [Fact]
    public async Task T_D6V2_06_Rename_MakesVideoStatusStale_WithoutTouchingArtifact()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);

        Assert.Equal(ArtifactGenerationState.Current, (await SummaryOfAsync(store, project.Id)).VideoStatus);

        // Rename は ProjectStore が Revision を進める（Contract §16）。video metadata はそのまま。
        await store.RenameProjectAsync(project.Id, "改名した教材");

        var summary = await SummaryOfAsync(store, project.Id);

        Assert.Equal(ArtifactGenerationState.Stale, summary.VideoStatus);
        Assert.True(summary.HasVideo);

        // artifact の実ファイルは触られていない
        Assert.True(File.Exists(CanonicalVideoPath(store, project.Id)));
    }
}
