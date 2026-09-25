// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Collections.Concurrent;
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// B2: <see cref="ProjectWorkspace.InsertManualStepAsync"/> の CurrentProject semantics（W1〜W4）と
/// manual Step の ReviewDraft 互換（§29）。
/// </summary>
public class ProjectWorkspaceManualStepTests
{
    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tc-app-tests", Guid.NewGuid().ToString("N"));

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

    private static async Task<(ProjectStore Store, TrainingProject Project)> SetupAsync(TempProjectsRoot temp)
    {
        var store = new ProjectStore(temp.Root);
        var project = await store.CreateProjectAsync("B2 workspace");
        project.Recording = new RecordingInfo
        {
            MediaPath = ProjectStore.RecordingMediaPath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 10000,
            HasSystemAudio = true,
            HasMicrophone = false,
        };
        project.Steps.Add(new TrainingStep
        {
            Id = Guid.NewGuid(),
            Order = 1,
            StartMs = 1000,
            Action = StepActions.Click,
            Title = "step 1",
        });
        project.Steps.Add(new TrainingStep
        {
            Id = Guid.NewGuid(),
            Order = 2,
            StartMs = 5000,
            Action = StepActions.Click,
            Title = "step 2",
        });
        await store.SaveProjectAsync(project);

        return (store, await store.LoadProjectAsync(project.Id) ?? throw new InvalidOperationException("前提が壊れています。"));
    }

    [Fact]
    public async Task W1_同一_Project_が_current_なら_CurrentProject_が更新される()
    {
        using var temp = new TempProjectsRoot();
        var (store, project) = await SetupAsync(temp);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        current.SetCurrent(project);
        var before = current.CurrentProject;

        var result = await workspace.InsertManualStepAsync(project.Id, project.Steps[0].Id, "MANUAL");

        Assert.True(result.Succeeded);
        Assert.NotSame(before, current.CurrentProject);
        Assert.Equal(project.Revision + 1, current.CurrentProject!.Revision);
        Assert.Equal(3, current.CurrentProject.Steps.Count);
    }

    [Fact]
    public async Task W2_別_Project_が_current_なら_disk_は更新されるが_CurrentProject_は変わらない()
    {
        using var temp = new TempProjectsRoot();
        var (store, project) = await SetupAsync(temp);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        var other = await store.CreateProjectAsync("別プロジェクト");
        current.SetCurrent(other);
        var before = current.CurrentProject;

        var result = await workspace.InsertManualStepAsync(project.Id, project.Steps[0].Id, "MANUAL");

        Assert.True(result.Succeeded);
        Assert.Same(before, current.CurrentProject);

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(3, saved!.Steps.Count);
    }

    [Fact]
    public async Task W3_Storage_failure_では_CurrentProject_が変わらない()
    {
        using var temp = new TempProjectsRoot();
        var (store, project) = await SetupAsync(temp);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        current.SetCurrent(project);
        var before = current.CurrentProject;

        // NoTimeSpace（余裕の無い位置）で failure させる。
        var tight = await store.LoadProjectAsync(project.Id);
        tight!.Steps[1].StartMs = tight.Steps[0].StartMs + 1;
        await store.SaveProjectAsync(tight);
        var currentAfterEdit = await store.LoadProjectAsync(project.Id);
        current.SetCurrent(currentAfterEdit!);

        var result = await workspace.InsertManualStepAsync(
            project.Id, currentAfterEdit!.Steps[0].Id, "no space");

        Assert.Equal(ManualStepInsertStatus.NoTimeSpace, result.Status);
        Assert.Same(currentAfterEdit, current.CurrentProject);
        _ = before;
    }

    /// <summary>単一 thread の UI context を模す（C の R18 / G14 と同型）。Post は owner thread が消化する。</summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PostCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            _queue.Enqueue((d, state));
        }

        public void PumpUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (_queue.TryDequeue(out var work))
                {
                    work.Callback(work.State);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            while (_queue.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }

    [Fact]
    public async Task W4_SetCurrent_は_caller_の_context_で行われる()
    {
        using var temp = new TempProjectsRoot();
        var (store, project) = await SetupAsync(temp);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        current.SetCurrent(project);

        var callerThread = Environment.CurrentManagedThreadId;
        var publishThread = -1;
        current.CurrentProjectChanged += (_, _) => publishThread = Environment.CurrentManagedThreadId;

        var context = new PumpingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        Task<ManualStepInsertResult> task;
        try
        {
            task = workspace.InsertManualStepAsync(project.Id, project.Steps[0].Id, "MANUAL");
            context.PumpUntil(task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var result = await task;

        Assert.True(result.Succeeded);
        Assert.True(
            Volatile.Read(ref context.PostCount) > 0,
            "Workspace が caller の context へ戻っていません（UI thread から外れています）。");
        Assert.Equal(callerThread, publishThread);
    }

    // =====================================================================
    // §29 — ReviewDraft 互換（manual Step を canonical から読める）
    // =====================================================================

    [Fact]
    public async Task W5_manual_Step_は_ReviewDraft_で読み込み_編集できる()
    {
        using var temp = new TempProjectsRoot();
        var (store, project) = await SetupAsync(temp);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        current.SetCurrent(project);

        var inserted = await workspace.InsertManualStepAsync(project.Id, project.Steps[0].Id, "manual step");
        var updated = current.CurrentProject!;

        var draft = ReviewDraft.Create(updated);
        var draftStep = draft.Steps.Single(s => s.StepId == inserted.StepId);

        // manual の read-only field は canonical のまま、編集可能 field は draft に載る。
        Assert.Equal(StepActions.Manual, draftStep.Action);
        Assert.Null(draftStep.Target);
        Assert.Null(draftStep.ScreenshotPath);
        Assert.Equal("manual step", draftStep.Title);

        draftStep.Title = "edited manual";
        draftStep.Description = "desc";
        Assert.True(draft.IsDirty);

        // reorder / delete も existing Step と同じ API で可能。
        Assert.True(draft.CanMoveUp(draft.IndexOf(inserted.StepId!.Value)));
        Assert.True(draft.RemoveAt(draft.IndexOf(inserted.StepId!.Value)));
        Assert.Equal(2, draft.Steps.Count);
    }

    [Fact]
    public async Task W6_追加直後の_draft_rebuild_で_new_Step_を_選択できる()
    {
        using var temp = new TempProjectsRoot();
        var (store, project) = await SetupAsync(temp);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        current.SetCurrent(project);

        var inserted = await workspace.InsertManualStepAsync(project.Id, project.Steps[0].Id, "selected after add");

        // View は RebuildAndRender(selectStepId: result.StepId) で IndexOf を使って選択する。
        var draft = ReviewDraft.Create(current.CurrentProject!);
        var index = draft.IndexOf(inserted.StepId!.Value);

        Assert.Equal(1, index);
        Assert.Equal("selected after add", draft.Steps[index].Title);
    }
}
