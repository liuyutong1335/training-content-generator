// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// B1-A: <see cref="ProjectWorkspace.UpdateReviewedStepsAsync"/> の Current Project 整合のテスト。
///
/// <para>
/// service-level のみを対象とし、WPF View / Window / Dispatcher には触れない。
/// </para>
/// </summary>
public class ProjectWorkspaceReviewedStepsTests
{
    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; }

        public TempProjectsRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "tc-app-tests", Guid.NewGuid().ToString("N"));
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

    private static (ProjectStore Store, CurrentProjectContext Current, ProjectWorkspace Workspace)
        CreateWorkspace(TempProjectsRoot temp)
    {
        var store = new ProjectStore(temp.Root);
        var current = new CurrentProjectContext();
        return (store, current, new ProjectWorkspace(store, current));
    }

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
                Action = "click",
                Title = $"手順 {order}",
                Description = $"説明 {order}",
            });
        }

        await store.SaveProjectAsync(project);
        return project;
    }

    private static StepReviewUpdate UpdateOf(TrainingStep step) =>
        new(step.Id, step.Title, step.Description, step.Caution, step.ExpectedResult);

    private static List<StepReviewUpdate> UpdatesOf(IEnumerable<TrainingStep> steps) =>
        [.. steps.Select(UpdateOf)];

    // =====================================================================
    // A1. Current Project を更新 → 返された snapshot に差し替わる
    // =====================================================================
    [Fact]
    public async Task A1_CurrentProject_IsRefreshedToUpdatedSnapshot()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepsAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var revisionBefore = current.CurrentProject!.Revision;

        var updates = UpdatesOf(project.Steps);
        updates[0] = updates[0] with { Title = EditedTitle };

        var updated = await workspace.UpdateReviewedStepsAsync(project.Id, updates);

        Assert.Equal(revisionBefore + 1, updated.Revision);
        Assert.Same(updated, current.CurrentProject);
        Assert.Equal(EditedTitle, current.CurrentProject!.Steps.Single(s => s.Id == updates[0].StepId).Title);
    }

    // =====================================================================
    // A2. Storage failure → CurrentProject は変更しない
    // =====================================================================
    [Fact]
    public async Task A2_StorageFailure_LeavesCurrentProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepsAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var currentBefore = current.CurrentProject;

        // 未知 StepId → Storage が reject する。
        var updates = UpdatesOf(project.Steps);
        updates.Add(new StepReviewUpdate(Guid.NewGuid(), "新規", null, null, null));

        await Assert.ThrowsAsync<ArgumentException>(
            () => workspace.UpdateReviewedStepsAsync(project.Id, updates));

        Assert.Same(currentBefore, current.CurrentProject);
        Assert.Equal(project.Revision, current.CurrentProject!.Revision);
    }

    // =====================================================================
    // A3. 別 Project を更新 → disk は更新、CurrentProject は上書きしない
    // =====================================================================
    [Fact]
    public async Task A3_OtherProjectUpdate_DoesNotTouchCurrentProject()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var currentProject = await CreateProjectWithStepsAsync(store);
        var otherProject = await CreateProjectWithStepsAsync(store);

        await workspace.OpenProjectAsync(currentProject.Id);
        var currentBefore = current.CurrentProject;

        var updates = UpdatesOf(otherProject.Steps);
        updates[0] = updates[0] with { Title = EditedTitle };

        await workspace.UpdateReviewedStepsAsync(otherProject.Id, updates);

        // identity guard: 別 Project の更新で Current Project を上書きしない。
        Assert.Same(currentBefore, current.CurrentProject);

        // 対象 Project は disk 上で更新されている。
        var loaded = await store.LoadProjectAsync(otherProject.Id);
        Assert.Equal(EditedTitle, loaded!.Steps.Single(s => s.Id == updates[0].StepId).Title);
    }

    // =====================================================================
    // A4. no-op → CurrentProject の内容は論理的に同一 / Revision 不変
    // =====================================================================
    [Fact]
    public async Task A4_NoOp_LeavesCurrentProjectContentUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepsAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var revisionBefore = current.CurrentProject!.Revision;

        await workspace.UpdateReviewedStepsAsync(project.Id, UpdatesOf(project.Steps));

        Assert.Equal(revisionBefore, current.CurrentProject!.Revision);
        Assert.Equal(project.Steps.Count, current.CurrentProject.Steps.Count);
        Assert.Equal("手順 1", current.CurrentProject.Steps[0].Title);
    }
}
