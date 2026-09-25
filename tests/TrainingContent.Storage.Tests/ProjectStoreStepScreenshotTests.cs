using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// 計画 §1.11 D 側 Task A: Step の ScreenshotPath 差し替えと Revision++ を
/// 1 回の project mutation / save boundary として扱う
/// <see cref="ProjectStore.UpdateStepScreenshotAsync"/> のテスト。
///
/// <para>
/// 守るべき invariant: 「新しい ScreenshotPath + 古い Revision」を永続化しない /
/// 保存失敗時は canonical な project.json が旧状態を維持する /
/// 対象 Step は Id で特定し Order を lookup key にしない。
/// </para>
/// </summary>
public class ProjectStoreStepScreenshotTests
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

    /// <summary>Redaction 後の想定 path（§1.11 の命名規則の形）。</summary>
    private const string NewPath =
        "screenshots/edited/step-11111111111111111111111111111111-22222222222222222222222222222222.png";

    private const string OriginalPath = "screenshots/original/event-000001.png";

    /// <summary>Step を 1 件持つ Project を作る（Order は契約 §29 に従い 1）。</summary>
    private static async Task<TrainingProject> CreateProjectWithStepAsync(
        ProjectStore store,
        string? screenshotPath = null)
    {
        var project = await store.CreateProjectAsync("赤入れ教材");

        project.Steps.Add(new TrainingStep
        {
            Id = Guid.NewGuid(),
            Order = 1,
            StartMs = 0,
            Action = "click",
            Title = "「新規申請」をクリックします",
            ScreenshotPath = screenshotPath,
        });

        await store.SaveProjectAsync(project);
        return project;
    }

    private static string ProjectJsonPath(ProjectStore store, Guid id) =>
        Path.Combine(store.GetProjectDirectory(id), ProjectStore.ProjectFileName);

    // =====================================================================
    // A1. 正常系 — path 更新と Revision++
    // =====================================================================
    [Fact]
    public async Task T_A_01_ValidStepId_UpdatesPathAndBumpsRevision()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, OriginalPath);
        var revisionBefore = project.Revision;
        var updatedAtBefore = project.UpdatedAtUtc;

        var updated = await store.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath);

        Assert.Equal(NewPath, updated.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore + 1, updated.Revision);
        Assert.True(updated.UpdatedAtUtc >= updatedAtBefore);

        // 永続化されていること（path 更新と Revision++ が同じ save で書かれている）。
        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(NewPath, loaded!.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore + 1, loaded.Revision);
    }

    // =====================================================================
    // A2. 未知の StepId — failure / Project 不変
    // =====================================================================
    [Fact]
    public async Task T_A_02_UnknownStepId_FailsAndLeavesProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, OriginalPath);

        await Assert.ThrowsAsync<ProjectStoreException>(
            () => store.UpdateStepScreenshotAsync(project.Id, Guid.NewGuid(), NewPath));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(OriginalPath, loaded!.Steps[0].ScreenshotPath);
        Assert.Equal(project.Revision, loaded.Revision);
    }

    // =====================================================================
    // A3. 不正な project-relative path — failure / Project 不変
    // =====================================================================
    [Theory]
    [InlineData("/screenshots/edited/step.png")]          // leading /
    [InlineData("screenshots/../../outside.png")]         // ..
    [InlineData(@"screenshots\edited\step.png")]          // 区切りが \
    [InlineData("C:/screenshots/edited/step.png")]        // 絶対パス
    [InlineData("C:screenshots/edited/step.png")]         // drive-relative
    [InlineData("screenshots//edited/step.png")]          // 空 segment
    [InlineData("screenshots/./edited/step.png")]         // . segment
    [InlineData("screenshots/edited/")]                   // 末尾空 segment
    [InlineData("")]                                      // empty
    [InlineData("   ")]                                   // whitespace only
    public async Task T_A_03_InvalidProjectRelativePath_FailsAndLeavesProjectUnchanged(string invalidPath)
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, OriginalPath);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, invalidPath));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(OriginalPath, loaded!.Steps[0].ScreenshotPath);
        Assert.Equal(project.Revision, loaded.Revision);
    }

    // =====================================================================
    // A3b. null path — failure（ArgumentNullException）
    // =====================================================================
    [Fact]
    public async Task T_A_03b_NullPath_FailsAndLeavesProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, OriginalPath);

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => store.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, null!));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(OriginalPath, loaded!.Steps[0].ScreenshotPath);
        Assert.Equal(project.Revision, loaded.Revision);
    }

    // =====================================================================
    // A4. 同じ path の再指定 — no-op（Revision も UpdatedAtUtc も動かさない）
    // =====================================================================
    [Fact]
    public async Task T_A_04_SamePath_IsNoOpAndDoesNotBumpRevision()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, NewPath);
        var revisionBefore = project.Revision;
        var updatedAtBefore = project.UpdatedAtUtc;

        var updated = await store.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath);

        Assert.Equal(NewPath, updated.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore, updated.Revision);
        Assert.Equal(updatedAtBefore, updated.UpdatedAtUtc);

        // 保存していないので disk 側も変化しない。
        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(revisionBefore, loaded!.Revision);
        Assert.Equal(updatedAtBefore, loaded.UpdatedAtUtc);
    }

    // =====================================================================
    // A5. Project が存在しない — failure
    // =====================================================================
    [Fact]
    public async Task T_A_05_ProjectNotFound_Fails()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        await Assert.ThrowsAsync<ProjectStoreException>(
            () => store.UpdateStepScreenshotAsync(Guid.NewGuid(), Guid.NewGuid(), NewPath));
    }

    // =====================================================================
    // A6. 保存失敗 — canonical な project.json は旧状態を維持
    // =====================================================================
    [Fact]
    public async Task T_A_06_SaveFailure_KeepsPreviousCanonicalState()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, OriginalPath);
        var revisionBefore = project.Revision;

        // 読みは許可しつつ置換だけを失敗させる（FileShare.Delete を与えない）。
        // VideoArtifactTransactionTests と同じ seam。
        //
        // 期待する例外型: Windows では File.Move(overwrite) が共有違反で
        // UnauthorizedAccessException を投げる（IOException ではない）。OS 差を許容して両方を受ける。
        Exception? failure;
        using (new FileStream(ProjectJsonPath(store, project.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = await Record.ExceptionAsync(
                () => store.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath));
        }

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"保存失敗として IOException / UnauthorizedAccessException を期待したが {failure?.GetType().Name ?? "例外なし"} だった。");

        // ProjectStoreException は IOException 派生なので、上の assertion だけでは
        // 「Step lookup 等の早期失敗」と「実際の保存失敗」を区別できない。
        // ここで save path に到達したことを固定する。
        Assert.IsNotType<ProjectStoreException>(failure);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(OriginalPath, loaded!.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore, loaded.Revision);

        // 失敗した保存の temp が残らない。
        Assert.False(File.Exists(
            Path.Combine(store.GetProjectDirectory(project.Id), ProjectStore.ProjectTempFileName)));
    }

    // =====================================================================
    // 付録. Revision++ により既生成 Video が Stale になること（計画 §8）
    // =====================================================================
    [Fact]
    public async Task T_A_07_RevisionBump_MakesGeneratedVideoStale()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepAsync(store, OriginalPath);

        // 現在の Revision で Video を生成済みにする。
        project.Outputs.TrainingVideo = new GeneratedArtifact
        {
            Path = VideoArtifactTransaction.CanonicalRelativePath,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = project.Revision,
        };
        await store.SaveProjectAsync(project);

        await File.WriteAllBytesAsync(
            Path.Combine(store.GetProjectDirectory(project.Id), "output", "training_video.mp4"),
            [1, 2, 3]);

        var before = (await store.ListProjectsAsync()).Single(s => s.Id == project.Id);
        Assert.Equal(ArtifactGenerationState.Current, before.VideoStatus);

        await store.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath);

        var after = (await store.ListProjectsAsync()).Single(s => s.Id == project.Id);
        Assert.Equal(ArtifactGenerationState.Stale, after.VideoStatus);
    }
}
