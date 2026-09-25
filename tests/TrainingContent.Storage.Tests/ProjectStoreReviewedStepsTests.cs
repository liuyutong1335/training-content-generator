using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// B1-A: Review UI の編集結果を反映する narrow な mutation boundary
/// <see cref="ProjectStore.UpdateReviewedStepsAsync"/> のテスト。
///
/// <para>
/// 守るべき invariant: 編集できるのは Title / Description / Caution / ExpectedResult のみ /
/// 未知 StepId（新規追加）と重複 StepId は reject / request list 順が Order 1..N /
/// 実質変更が無ければ保存しない / 実変更時のみ Revision++ /
/// caller 由来の <see cref="TrainingStep"/> を受け取らない。
/// </para>
/// </summary>
public class ProjectStoreReviewedStepsTests
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

    private const string EditedTitle = "編集後のタイトル";

    /// <summary>編集可能 4 項目に既知の値を持つ Step を <paramref name="count"/> 件持つ Project を作る。</summary>
    private static async Task<TrainingProject> CreateProjectWithStepsAsync(ProjectStore store, int count = 3)
    {
        var project = await store.CreateProjectAsync("赤入れ教材");

        for (var i = 0; i < count; i++)
        {
            var order = i + 1;
            project.Steps.Add(new TrainingStep
            {
                Id = Guid.NewGuid(),
                Order = order,
                StartMs = order * 1000,
                EndMs = order * 1000 + 500,
                Action = "click",
                Target = $"target-{order}",
                Title = $"手順 {order}",
                Description = $"説明 {order}",
                Caution = $"注意 {order}",
                ExpectedResult = $"結果 {order}",
                ScreenshotPath = $"screenshots/original/event-{order:D6}.png",
                SourceEventIds = [Guid.NewGuid()],
            });
        }

        await store.SaveProjectAsync(project);
        return project;
    }

    /// <summary>既存 Step の現在値から request を作る（= 変更なしの request）。</summary>
    private static StepReviewUpdate UpdateOf(TrainingStep step) =>
        new(step.Id, step.Title, step.Description, step.Caution, step.ExpectedResult);

    private static List<StepReviewUpdate> UpdatesOf(IEnumerable<TrainingStep> steps) =>
        [.. steps.Select(UpdateOf)];

    private static string ProjectJsonPath(ProjectStore store, Guid id) =>
        Path.Combine(store.GetProjectDirectory(id), ProjectStore.ProjectFileName);

    // =====================================================================
    // S1. 通常の編集 — 4 項目更新 / Revision++ / 永続化
    // =====================================================================
    [Fact]
    public async Task S01_NormalEdit_UpdatesFourFieldsAndBumpsRevision()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var revisionBefore = project.Revision;
        var updatedAtBefore = project.UpdatedAtUtc;

        var updates = UpdatesOf(project.Steps);
        updates[1] = updates[1] with
        {
            Title = EditedTitle,
            Description = "説明を更新",
            Caution = "注意を更新",
            ExpectedResult = "結果を更新",
        };

        var updated = await store.UpdateReviewedStepsAsync(project.Id, updates);

        Assert.Equal(revisionBefore + 1, updated.Revision);
        Assert.True(updated.UpdatedAtUtc >= updatedAtBefore);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(revisionBefore + 1, loaded!.Revision);

        var step = loaded.Steps.Single(s => s.Id == updates[1].StepId);
        Assert.Equal(EditedTitle, step.Title);
        Assert.Equal("説明を更新", step.Description);
        Assert.Equal("注意を更新", step.Caution);
        Assert.Equal("結果を更新", step.ExpectedResult);
    }

    // =====================================================================
    // S2. 並べ替え — request list 順が Order 1..N になる
    // =====================================================================
    [Fact]
    public async Task S02_Reorder_SavesRequestOrderAsOneToN()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        var reversed = UpdatesOf(project.Steps.AsEnumerable().Reverse());
        var expectedIds = reversed.Select(u => u.StepId).ToList();

        var updated = await store.UpdateReviewedStepsAsync(project.Id, reversed);

        Assert.Equal(expectedIds, updated.Steps.OrderBy(s => s.Order).Select(s => s.Id).ToList());
        Assert.Equal(new[] { 1, 2, 3 }, updated.Steps.OrderBy(s => s.Order).Select(s => s.Order).ToArray());

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(expectedIds, loaded!.Steps.OrderBy(s => s.Order).Select(s => s.Id).ToList());
    }

    // =====================================================================
    // S3. 削除 — request に無い Step が消え、残りが 1..N
    // =====================================================================
    [Fact]
    public async Task S03_Delete_RemovesOmittedStepAndRenumbers()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var deletedId = project.Steps[1].Id;

        var kept = UpdatesOf(project.Steps.Where(s => s.Id != deletedId));

        var updated = await store.UpdateReviewedStepsAsync(project.Id, kept);

        Assert.Equal(2, updated.Steps.Count);
        Assert.DoesNotContain(updated.Steps, s => s.Id == deletedId);
        Assert.Equal(new[] { 1, 2 }, updated.Steps.OrderBy(s => s.Order).Select(s => s.Order).ToArray());

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(2, loaded!.Steps.Count);
        Assert.DoesNotContain(loaded.Steps, s => s.Id == deletedId);
    }

    // =====================================================================
    // S4. 全削除 — 空 list は valid（Steps=[] / Revision++）
    // =====================================================================
    [Fact]
    public async Task S04_DeleteAll_EmptyListClearsStepsAndBumpsRevision()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var revisionBefore = project.Revision;

        var updated = await store.UpdateReviewedStepsAsync(project.Id, []);

        Assert.Empty(updated.Steps);
        Assert.Equal(revisionBefore + 1, updated.Revision);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Empty(loaded!.Steps);
        Assert.Equal(revisionBefore + 1, loaded.Revision);
    }

    // =====================================================================
    // S5. 編集対象外 field の保持
    // =====================================================================
    [Fact]
    public async Task S05_NonEditableFields_ArePreserved()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        var target = project.Steps[1];
        var updates = UpdatesOf(project.Steps);
        updates[1] = updates[1] with { Title = EditedTitle };

        var updated = await store.UpdateReviewedStepsAsync(project.Id, updates);

        var step = updated.Steps.Single(s => s.Id == target.Id);
        Assert.Equal(target.Id, step.Id);
        Assert.Equal(target.StartMs, step.StartMs);
        Assert.Equal(target.EndMs, step.EndMs);
        Assert.Equal(target.Action, step.Action);
        Assert.Equal(target.Target, step.Target);
        Assert.Equal(target.ScreenshotPath, step.ScreenshotPath);
        Assert.Equal(target.SourceEventIds, step.SourceEventIds);

        // 永続化後も同じ
        var loaded = await store.LoadProjectAsync(project.Id);
        var persisted = loaded!.Steps.Single(s => s.Id == target.Id);
        Assert.Equal(target.StartMs, persisted.StartMs);
        Assert.Equal(target.ScreenshotPath, persisted.ScreenshotPath);
        Assert.Equal(target.SourceEventIds, persisted.SourceEventIds);
    }

    // =====================================================================
    // S6. 未知 StepId — reject（B1 では新規追加不可）
    // =====================================================================
    [Fact]
    public async Task S06_UnknownStepId_IsRejectedAndProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        var updates = UpdatesOf(project.Steps);
        updates.Add(new StepReviewUpdate(Guid.NewGuid(), "新規追加しようとした手順", null, null, null));

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateReviewedStepsAsync(project.Id, updates));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(project.Steps.Count, loaded!.Steps.Count);
        Assert.Equal(project.Revision, loaded.Revision);
    }

    // =====================================================================
    // S7. StepId 重複 — reject
    // =====================================================================
    [Fact]
    public async Task S07_DuplicateStepId_IsRejectedAndProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        var updates = UpdatesOf(project.Steps);
        updates.Add(updates[0]);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateReviewedStepsAsync(project.Id, updates));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(project.Steps.Count, loaded!.Steps.Count);
        Assert.Equal(project.Revision, loaded.Revision);
    }

    // =====================================================================
    // S8. Guid.Empty — reject
    // =====================================================================
    [Fact]
    public async Task S08_EmptyGuid_IsRejected()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateReviewedStepsAsync(project.Id, [new StepReviewUpdate(Guid.Empty, "x", null, null, null)]));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(project.Revision, loaded!.Revision);
    }

    // =====================================================================
    // S9. Title が null / empty / whitespace-only — reject
    // =====================================================================
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task S09_BlankTitle_IsRejectedAndProjectUnchanged(string? title)
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        var updates = UpdatesOf(project.Steps);
        updates[0] = updates[0] with { Title = title! };

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpdateReviewedStepsAsync(project.Id, updates));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal("手順 1", loaded!.Steps.Single(s => s.Id == updates[0].StepId).Title);
        Assert.Equal(project.Revision, loaded.Revision);
    }

    // =====================================================================
    // S10. optional field の blank は canonical null に正規化
    // =====================================================================
    [Fact]
    public async Task S10_OptionalBlank_IsNormalizedToNull()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        var updates = UpdatesOf(project.Steps);
        updates[0] = updates[0] with { Description = "", Caution = "   ", ExpectedResult = null };

        await store.UpdateReviewedStepsAsync(project.Id, updates);

        var loaded = await store.LoadProjectAsync(project.Id);
        var step = loaded!.Steps.Single(s => s.Id == updates[0].StepId);
        Assert.Null(step.Description);
        Assert.Null(step.Caution);
        Assert.Null(step.ExpectedResult);
    }

    // =====================================================================
    // S11. no-op — 保存しない / Revision・UpdatedAtUtc 不変
    // =====================================================================
    [Fact]
    public async Task S11_NoOp_DoesNotSaveAndKeepsRevision()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var revisionBefore = project.Revision;
        var updatedAtBefore = project.UpdatedAtUtc;

        var updates = UpdatesOf(project.Steps);

        // project.json を lock しても例外にならない = SaveProjectAsync が走っていない。
        // （読みは FileShare.Read で許可されるため、load は成功する。）
        TrainingProject updated;
        using (new FileStream(ProjectJsonPath(store, project.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            updated = await store.UpdateReviewedStepsAsync(project.Id, updates);
        }

        Assert.Equal(revisionBefore, updated.Revision);
        Assert.Equal(updatedAtBefore, updated.UpdatedAtUtc);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal(revisionBefore, loaded!.Revision);
        Assert.Equal(updatedAtBefore, loaded.UpdatedAtUtc);
        Assert.Equal(project.Steps.Count, loaded.Steps.Count);
    }

    // =====================================================================
    // S11b. 正規化後に同一なら no-op（"" と null の差で Revision を増やさない）
    // =====================================================================
    [Fact]
    public async Task S11b_NormalizedEquivalent_IsNoOp()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);

        // canonical を null にしておく。
        var cleared = UpdatesOf(project.Steps).Select(u => u with { Description = null, Caution = null, ExpectedResult = null }).ToList();
        var afterClear = await store.UpdateReviewedStepsAsync(project.Id, cleared);
        var revisionBefore = afterClear.Revision;

        // UI 由来の "" / whitespace を渡しても正規化後に同一 → no-op。
        var blanks = cleared.Select(u => u with { Description = "", Caution = "  ", ExpectedResult = "" }).ToList();
        var updated = await store.UpdateReviewedStepsAsync(project.Id, blanks);

        Assert.Equal(revisionBefore, updated.Revision);
    }

    // =====================================================================
    // S12. 並べ替えのみ（内容同一）— Revision++
    // =====================================================================
    [Fact]
    public async Task S12_ReorderOnly_BumpsRevision()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var revisionBefore = project.Revision;

        var reversed = UpdatesOf(project.Steps.AsEnumerable().Reverse());

        var updated = await store.UpdateReviewedStepsAsync(project.Id, reversed);

        Assert.Equal(revisionBefore + 1, updated.Revision);
    }

    // =====================================================================
    // S13. artifact metadata は触らず、Revision++ で stale 判定が成立する
    // =====================================================================
    [Fact]
    public async Task S13_RevisionBump_MakesGeneratedVideoStale_WithoutTouchingArtifactMetadata()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var revisionBefore = project.Revision;

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

        var updates = UpdatesOf(project.Steps);
        updates[0] = updates[0] with { Title = EditedTitle };

        var updated = await store.UpdateReviewedStepsAsync(project.Id, updates);

        Assert.Equal(revisionBefore + 1, updated.Revision);
        // artifact metadata 自体は変更しない（SourceRevision は据え置き）。
        Assert.Equal(revisionBefore, updated.Outputs.TrainingVideo!.SourceRevision);

        var after = (await store.ListProjectsAsync()).Single(s => s.Id == project.Id);
        Assert.Equal(ArtifactGenerationState.Stale, after.VideoStatus);
    }

    // =====================================================================
    // S14. Project が存在しない
    // =====================================================================
    [Fact]
    public async Task S14_ProjectNotFound_IsRejected()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        await Assert.ThrowsAsync<ProjectStoreException>(
            () => store.UpdateReviewedStepsAsync(Guid.NewGuid(), []));
    }

    // =====================================================================
    // S15. 保存失敗 — canonical な project.json は旧状態
    // =====================================================================
    [Fact]
    public async Task S15_SaveFailure_KeepsPreviousCanonicalState()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectWithStepsAsync(store);
        var revisionBefore = project.Revision;

        var updates = UpdatesOf(project.Steps);
        updates[0] = updates[0] with { Title = EditedTitle };

        Exception? failure;
        using (new FileStream(ProjectJsonPath(store, project.Id), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = await Record.ExceptionAsync(
                () => store.UpdateReviewedStepsAsync(project.Id, updates));
        }

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"保存失敗として IOException / UnauthorizedAccessException を期待したが {failure?.GetType().Name ?? "例外なし"} だった。");
        Assert.IsNotType<ProjectStoreException>(failure);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal("手順 1", loaded!.Steps.Single(s => s.Id == updates[0].StepId).Title);
        Assert.Equal(revisionBefore, loaded.Revision);

        Assert.False(File.Exists(
            Path.Combine(store.GetProjectDirectory(project.Id), ProjectStore.ProjectTempFileName)));
    }
}
