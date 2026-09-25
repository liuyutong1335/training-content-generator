using System.IO;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// B2: <see cref="ProjectStore.InsertManualStepAsync"/> の mutation semantics（S1〜S15）。
/// </summary>
public class ProjectStoreManualStepTests
{
    private const long DurationMs = 10000;

    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tc-storage-tests", Guid.NewGuid().ToString("N"));

        public TempProjectsRoot() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private static async Task<TrainingProject> CreateAsync(ProjectStore store, params long[] stepStartMs)
    {
        var project = await store.CreateProjectAsync("B2 test");
        project.Recording = new RecordingInfo
        {
            MediaPath = ProjectStore.RecordingMediaPath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = DurationMs,
            HasSystemAudio = true,
            HasMicrophone = false,
        };

        for (var i = 0; i < stepStartMs.Length; i++)
        {
            project.Steps.Add(new TrainingStep
            {
                Id = Guid.NewGuid(),
                Order = i + 1,
                StartMs = stepStartMs[i],
                EndMs = null,
                Action = StepActions.Click,
                Target = "AlphaClick",
                Title = $"step {i + 1}",
                Description = $"desc {i + 1}",
                Caution = $"caution {i + 1}",
                ExpectedResult = $"expected {i + 1}",
                ScreenshotPath = $"screenshots/original/event-00000{i + 1}.png",
                SourceEventIds = [Guid.NewGuid()],
            });
        }

        await store.SaveProjectAsync(project);
        return project;
    }

    private static TrainingProject Snapshot(TrainingProject project) => project;

    // =====================================================================
    // S1 / S2 / S3 — 作成と fields
    // =====================================================================

    [Fact]
    public async Task S1_Steps_が空でも_Recording_があれば_manual_Step_を作れる()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store);

        var result = await store.InsertManualStepAsync(project.Id, null, "最初の手順");

        Assert.Equal(ManualStepInsertStatus.Inserted, result.Status);
        var saved = await store.LoadProjectAsync(project.Id);
        var step = Assert.Single(saved!.Steps);
        Assert.Equal("最初の手順", step.Title);
        Assert.Equal(0, step.StartMs);
        Assert.Equal(1, step.Order);
        Assert.Equal(step.Id, result.StepId);
    }

    [Fact]
    public async Task S2_manual_Step_の_fields_は_frozen_contract_どおり()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store);

        await store.InsertManualStepAsync(project.Id, null, "手順 (fields)");

        var saved = await store.LoadProjectAsync(project.Id);
        var step = Assert.Single(saved!.Steps);
        Assert.Equal(StepActions.Manual, step.Action);
        Assert.Null(step.Target);
        Assert.Null(step.EndMs);
        Assert.Null(step.ScreenshotPath);
        Assert.Null(step.Description);
        Assert.Null(step.Caution);
        Assert.Null(step.ExpectedResult);
        Assert.Empty(step.SourceEventIds);
        Assert.Equal("手順 (fields)", step.Title);
    }

    [Fact]
    public async Task S2b_non_blank_Title_の_leading_trailing_whitespace_はそのまま保存される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store);

        // B1 の Review edit と同じく trim しない（create と edit で normalization を変えない）。
        var result = await store.InsertManualStepAsync(project.Id, null, "  手順 A  ");

        Assert.Equal(ManualStepInsertStatus.Inserted, result.Status);
        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal("  手順 A  ", saved!.Steps[0].Title);
    }

    [Fact]
    public async Task S3_StepId_は毎回_fresh_Guid()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store);

        var first = await store.InsertManualStepAsync(project.Id, null, "A");
        var second = await store.InsertManualStepAsync(project.Id, null, "B");

        Assert.NotNull(first.StepId);
        Assert.NotNull(second.StepId);
        Assert.NotEqual(first.StepId, second.StepId);

        var saved = await store.LoadProjectAsync(project.Id);
        var ids = saved!.Steps.Select(step => step.Id).ToList();
        Assert.Contains(first.StepId!.Value, ids);
        Assert.Contains(second.StepId!.Value, ids);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // =====================================================================
    // S4 / S5 / S6 — placement
    // =====================================================================

    [Fact]
    public async Task S4_Step_の間へは_midpoint_で挿入される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 5000);
        var anchor = project.Steps[0].Id;

        var result = await store.InsertManualStepAsync(project.Id, anchor, "between");

        Assert.Equal(ManualStepInsertStatus.Inserted, result.Status);
        Assert.Equal(3000, result.StartMs);

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(3, saved!.Steps.Count);
        Assert.Equal("between", saved.Steps[1].Title);
        Assert.Equal(3000, saved.Steps[1].StartMs);
    }

    [Fact]
    public async Task S5_最後の_Step_の後ろへは_Duration_までの中間で挿入される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 4000);

        var result = await store.InsertManualStepAsync(project.Id, project.Steps[1].Id, "after last");

        Assert.Equal(7000, result.StartMs); // 4000 + (10000-4000)/2
        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal("after last", saved!.Steps[^1].Title);
    }

    [Fact]
    public async Task S6_Order_は_1_N_へ_normalize_される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 5000);

        await store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "between");

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal([1, 2, 3], saved!.Steps.Select(s => s.Order));
        Assert.Equal(["step 1", "between", "step 2"], saved.Steps.Select(s => s.Title));
    }

    // =====================================================================
    // S7 / S8 / S9 / S10 — 既存 Step 不変・Revision・Outputs
    // =====================================================================

    [Fact]
    public async Task S7_既存_Step_は_Order_以外_変更されない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 5000);
        var before = (await store.LoadProjectAsync(project.Id))!.Steps[1];

        var beforeFields = (
            before.Id, before.StartMs, before.EndMs, before.Action, before.Target,
            before.Title, before.Description, before.Caution, before.ExpectedResult,
            before.ScreenshotPath);

        await store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "between");

        var after = (await store.LoadProjectAsync(project.Id))!.Steps[2];
        var afterFields = (
            after.Id, after.StartMs, after.EndMs, after.Action, after.Target,
            after.Title, after.Description, after.Caution, after.ExpectedResult,
            after.ScreenshotPath);

        // 既存 Step は Order だけが変わる（Order は S6 で確認）。
        Assert.Equal(beforeFields, afterFields);
        Assert.Equal(before.SourceEventIds, after.SourceEventIds);
    }

    [Fact]
    public async Task S8_Revision_が_1_進む()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000);
        var revisionBefore = (await store.LoadProjectAsync(project.Id))!.Revision;

        await store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "manual");

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(revisionBefore + 1, saved!.Revision);
    }

    [Fact]
    public async Task S9_UpdatedAtUtc_が更新される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000);
        var before = (await store.LoadProjectAsync(project.Id))!.UpdatedAtUtc;

        await store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "manual");

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.True(saved!.UpdatedAtUtc >= before);
    }

    [Fact]
    public async Task S10_Outputs_metadata_は保持される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000);

        project.Outputs.ManualMarkdown = new GeneratedArtifact
        {
            Path = ManualArtifactTransaction.MarkdownRelativePath,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = project.Revision,
        };
        project.Outputs.TrainingVideo = new GeneratedArtifact
        {
            Path = VideoArtifactTransaction.CanonicalRelativePath,
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = project.Revision,
        };
        await store.SaveProjectAsync(project);
        var before = await store.LoadProjectAsync(project.Id);

        await store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "manual");

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(before!.Outputs.ManualMarkdown!.GeneratedAtUtc, saved!.Outputs.ManualMarkdown!.GeneratedAtUtc);
        Assert.Equal(before.Outputs.TrainingVideo!.GeneratedAtUtc, saved.Outputs.TrainingVideo!.GeneratedAtUtc);

        // SourceRevision は挿入前の Revision のまま → Stale になる。
        Assert.NotEqual(saved.Revision, saved.Outputs.ManualMarkdown.SourceRevision);
    }

    // =====================================================================
    // S11〜S14 — 失敗時は何も変更しない
    // =====================================================================

    [Fact]
    public async Task S11_Recording_が無ければ_何も変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await store.CreateProjectAsync("recording なし");
        project.Steps.Add(new TrainingStep { Id = Guid.NewGuid(), Order = 1, StartMs = 0, Action = StepActions.Click, Title = "s" });
        await store.SaveProjectAsync(project);
        var before = Snapshot((await store.LoadProjectAsync(project.Id))!);

        var result = await store.InsertManualStepAsync(project.Id, null, "manual");

        Assert.Equal(ManualStepInsertStatus.RecordingMissing, result.Status);
        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(before.Revision, saved!.Revision);
        Assert.Single(saved.Steps);
    }

    [Fact]
    public async Task S12_NoTimeSpace_なら_何も変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 1001);
        var before = await store.LoadProjectAsync(project.Id);

        var result = await store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "no space");

        Assert.Equal(ManualStepInsertStatus.NoTimeSpace, result.Status);
        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(before!.Revision, saved!.Revision);
        Assert.Equal([1000L, 1001L], saved.Steps.Select(s => s.StartMs));
    }

    [Fact]
    public async Task S13_AnchorNotFound_なら_何も変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 5000);
        var before = await store.LoadProjectAsync(project.Id);

        var result = await store.InsertManualStepAsync(project.Id, Guid.NewGuid(), "missing anchor");

        Assert.Equal(ManualStepInsertStatus.AnchorNotFound, result.Status);
        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(before!.Revision, saved!.Revision);
        Assert.Equal(2, saved.Steps.Count);
    }

    [Fact]
    public async Task S14_blank_Title_は_拒否され_何も変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000);
        var before = await store.LoadProjectAsync(project.Id);

        var result = await store.InsertManualStepAsync(project.Id, null, "   ");

        Assert.Equal(ManualStepInsertStatus.InvalidTitle, result.Status);
        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(before!.Revision, saved!.Revision);
        Assert.Single(saved.Steps);
    }

    [Fact]
    public async Task S15_保存失敗時は_project_json_が変わらない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateAsync(store, 1000, 5000);
        var jsonPath = Path.Combine(store.GetProjectDirectory(project.Id), ProjectStore.ProjectFileName);
        var bytesBefore = await File.ReadAllBytesAsync(jsonPath);

        Exception? failure;
        using (new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = await Record.ExceptionAsync(
                () => store.InsertManualStepAsync(project.Id, project.Steps[0].Id, "manual"));
        }

        // SaveProjectAsync の失敗は IOException / UnauthorizedAccessException / ProjectStoreException のいずれか。
        Assert.NotNull(failure);
        Assert.True(
            failure is IOException or UnauthorizedAccessException or ProjectStoreException,
            $"予期しない例外型: {failure.GetType().Name}");

        // 失敗しても project.json は byte 単位で無傷。
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(jsonPath));
    }

    [Fact]
    public async Task S15b_Project_が無ければ_ProjectStoreException()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        await Assert.ThrowsAsync<ProjectStoreException>(
            () => store.InsertManualStepAsync(Guid.NewGuid(), null, "manual"));
    }
}
