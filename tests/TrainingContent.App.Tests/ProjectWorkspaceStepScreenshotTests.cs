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
/// 計画 §1.11 D 側 Task B: <see cref="ProjectWorkspace.UpdateStepScreenshotAsync"/> の
/// Current Project 整合のテスト。
///
/// <para>
/// service-level のみを対象とし、WPF View / Window / Dispatcher には触れない
/// （<see cref="ProjectWorkspace"/> / <see cref="CurrentProjectContext"/> は WPF 型に依存しない）。
/// </para>
/// <para>
/// 守るべき invariant: Storage の保存が成功した後だけ Current Project を差し替える /
/// 別 Project を更新しても Current Project を上書きしない / Storage 失敗時は Current Project を
/// 変更しない（stale reference を mutation しない）。
/// </para>
/// </summary>
public class ProjectWorkspaceStepScreenshotTests
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

    private const string OriginalPath = "screenshots/original/event-000001.png";

    private const string NewPath =
        "screenshots/edited/step-11111111111111111111111111111111-22222222222222222222222222222222.png";

    /// <summary>ProjectStore を real temp directory で使う。UI thread / Dispatcher は生成しない。</summary>
    private static (ProjectStore Store, CurrentProjectContext Current, ProjectWorkspace Workspace)
        CreateWorkspace(TempProjectsRoot temp)
    {
        var store = new ProjectStore(temp.Root);
        var current = new CurrentProjectContext();
        return (store, current, new ProjectWorkspace(store, current));
    }

    private static async Task<TrainingProject> CreateProjectWithStepAsync(
        ProjectStore store,
        string screenshotPath = OriginalPath)
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

    // =====================================================================
    // B1. Current Project を更新 → 新しい snapshot に差し替わる
    // =====================================================================
    [Fact]
    public async Task T_B_01_CurrentProject_IsRefreshedToUpdatedSnapshot()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var revisionBefore = current.CurrentProject!.Revision;

        var updated = await workspace.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath);

        Assert.Equal(NewPath, updated.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore + 1, updated.Revision);

        // Current Project が Storage の返した新しい snapshot そのものへ差し替わっている。
        Assert.Same(updated, current.CurrentProject);
        Assert.Equal(NewPath, current.CurrentProject!.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore + 1, current.CurrentProject.Revision);
    }

    // =====================================================================
    // B2. 別 Project を更新 → Current Project は変更しない
    // =====================================================================
    [Fact]
    public async Task T_B_02_OtherProjectUpdate_DoesNotTouchCurrentProject()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var currentProject = await CreateProjectWithStepAsync(store);
        var otherProject = await CreateProjectWithStepAsync(store);

        await workspace.OpenProjectAsync(currentProject.Id);
        var currentBefore = current.CurrentProject;
        var revisionBefore = currentBefore!.Revision;

        await workspace.UpdateStepScreenshotAsync(otherProject.Id, otherProject.Steps[0].Id, NewPath);

        // identity guard: 別 Project の更新で Current Project を上書きしない。
        Assert.Same(currentBefore, current.CurrentProject);
        Assert.Equal(revisionBefore, current.CurrentProject!.Revision);

        // 対象 Project は disk 上で更新されている。
        var loaded = await store.LoadProjectAsync(otherProject.Id);
        Assert.Equal(NewPath, loaded!.Steps[0].ScreenshotPath);
    }

    // =====================================================================
    // B3. Storage failure（未知の StepId）→ Current Project は変更しない
    // =====================================================================
    [Fact]
    public async Task T_B_03_UnknownStepId_LeavesCurrentProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var currentBefore = current.CurrentProject;

        await Assert.ThrowsAsync<ProjectStoreException>(
            () => workspace.UpdateStepScreenshotAsync(project.Id, Guid.NewGuid(), NewPath));

        Assert.Same(currentBefore, current.CurrentProject);
        Assert.Equal(OriginalPath, current.CurrentProject!.Steps[0].ScreenshotPath);
    }

    // =====================================================================
    // B3b. Storage failure（不正 path）→ Current Project は変更しない
    // =====================================================================
    [Fact]
    public async Task T_B_03b_InvalidPath_LeavesCurrentProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var currentBefore = current.CurrentProject;

        await Assert.ThrowsAsync<ArgumentException>(
            () => workspace.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, "../escaped.png"));

        Assert.Same(currentBefore, current.CurrentProject);
        Assert.Equal(OriginalPath, current.CurrentProject!.Steps[0].ScreenshotPath);
    }

    // =====================================================================
    // B4. 同じ ScreenshotPath（no-op）→ Current Project の内容は変わらない
    // =====================================================================
    [Fact]
    public async Task T_B_04_SamePath_IsNoOpAndLeavesCurrentProjectContentUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepAsync(store, NewPath);

        await workspace.OpenProjectAsync(project.Id);
        var revisionBefore = current.CurrentProject!.Revision;

        await workspace.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath);

        Assert.Equal(NewPath, current.CurrentProject!.Steps[0].ScreenshotPath);
        Assert.Equal(revisionBefore, current.CurrentProject.Revision);
    }

    // =====================================================================
    // B5. Storage save failure → Current Project は変更しない
    // =====================================================================
    [Fact]
    public async Task T_B_05_SaveFailure_LeavesCurrentProjectUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var (store, current, workspace) = CreateWorkspace(temp);
        var project = await CreateProjectWithStepAsync(store);

        await workspace.OpenProjectAsync(project.Id);
        var currentBefore = current.CurrentProject;
        var revisionBefore = currentBefore!.Revision;

        // 読みは許可しつつ置換だけを失敗させる（Storage A6 と同じ seam）。
        // Windows では File.Move(overwrite) が UnauthorizedAccessException を投げるため両方を受ける。
        var jsonPath = Path.Combine(store.GetProjectDirectory(project.Id), ProjectStore.ProjectFileName);
        Exception? failure;
        using (new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            failure = await Record.ExceptionAsync(
                () => workspace.UpdateStepScreenshotAsync(project.Id, project.Steps[0].Id, NewPath));
        }

        Assert.True(
            failure is IOException or UnauthorizedAccessException,
            $"保存失敗として IOException / UnauthorizedAccessException を期待したが {failure?.GetType().Name ?? "例外なし"} だった。");

        // ProjectStoreException は IOException 派生なので、上の assertion だけでは
        // 「Step lookup 等の早期失敗」と「実際の保存失敗」を区別できない。
        // ここで save path に到達したことを固定する。
        Assert.IsNotType<ProjectStoreException>(failure);

        Assert.Same(currentBefore, current.CurrentProject);
        Assert.Equal(revisionBefore, current.CurrentProject!.Revision);
        Assert.Equal(OriginalPath, current.CurrentProject.Steps[0].ScreenshotPath);
    }
}
