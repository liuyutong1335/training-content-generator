// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using System.Text;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Capture;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// E+F-B: <see cref="RecordingCoordinator"/> の production 配線（DeferredCommit / session state / 委譲）。
///
/// <para>
/// <b>CaptureStarted は発火させない</b>。発火すると <c>OperationCaptureSession.Start()</c> が実行され、
/// 実機の global hook / UIA / screenshot が test process で動いてしまうため。したがってここで被覆するのは
/// 「Engine へ DeferredCommit を渡す」「preparation stop は finalization の対象外」「session state が
/// 次 recording へ漏れない」「pending が解消され次回 Start が可能」まで。finalization 本体は
/// <see cref="RecordingFinalizationPipelineTests"/> が被覆する。
/// </para>
/// </summary>
public class RecordingCoordinatorFinalizationWiringTests
{
    private const byte OldFill = 0x11;
    private const byte NewFill = 0xEE;

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

    private sealed record WiringContext(
        ProjectStore Store,
        TrainingProject Project,
        FakeRecordingEngine Engine,
        CurrentProjectContext Current,
        RecordingCoordinator Coordinator,
        string CanonicalPath,
        string EventsPath);

    private static async Task<WiringContext> SetupAsync(TempProjectsRoot temp, bool withExistingCanonical = true)
    {
        var store = new ProjectStore(temp.Root);
        var project = await store.CreateProjectAsync("録画配線テスト");
        var current = new CurrentProjectContext();
        var engine = new FakeRecordingEngine();

        var canonical = store.GetRecordingOutputPath(project.Id);
        engine.CanonicalPath = canonical;

        if (withExistingCanonical)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            File.WriteAllBytes(canonical, [OldFill, OldFill, OldFill]);
        }

        current.SetCurrent(project);

        var transaction = new RecordingFinalizationTransaction(engine, store);
        var coordinator = new RecordingCoordinator(engine, store, current, transaction);

        return new WiringContext(
            store,
            project,
            engine,
            current,
            coordinator,
            canonical,
            Path.Combine(store.GetProjectDirectory(project.Id), ProjectStore.EventsFileName));
    }

    private static string Line(Guid id, int seq, long timestampMs, string type, string payload) =>
        $$"""{"schemaVersion":1,"id":"{{id:D}}","seq":{{seq}},"timestampMs":{{timestampMs}},"type":"{{type}}","payload":{{payload}}}""";

    /// <summary>current session の events.jsonl（offset は <see cref="SeedCurrentSession"/> が返す）。</summary>
    private static long SeedCurrentSession(WiringContext ctx)
    {
        var sb = new StringBuilder();
        sb.Append(Line(Guid.NewGuid(), 1, 0, "recording.started", "{}")).Append('\n');
        sb.Append(Line(Guid.NewGuid(), 2, 1000, "keyboard.shortcut", """{"shortcut":"Ctrl+S"}""")).Append('\n');
        sb.Append(Line(Guid.NewGuid(), 3, 1500, "recording.stopped", "{}")).Append('\n');

        File.WriteAllText(ctx.EventsPath, sb.ToString());
        return new FileInfo(ctx.EventsPath).Length;
    }

    private static string ReadProjectJson(ProjectStore store, Guid projectId) =>
        File.ReadAllText(Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName));

    private static string WriteStagingFile(TempProjectsRoot temp)
    {
        var path = Path.Combine(temp.Root, $"recording.staging-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, [NewFill, NewFill, NewFill]);
        return path;
    }

    // =====================================================================
    // F1 — DeferredCommit を Engine へ渡す
    // =====================================================================

    [Fact]
    public async Task F01_StartAsync_は_DeferredCommit_true_と_canonical_path_を_Engine_へ渡す()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Engine.StopResultFactory = () => PreparationStopResult(ctx);

        var start = await ctx.Coordinator.StartAsync(display: null, systemAudioDevice: null, microphoneDevice: null);

        Assert.True(start.Succeeded, start.ErrorMessage);
        var options = Assert.Single(ctx.Engine.StartedOptions);
        Assert.True(options.DeferredCommit, "DeferredCommit = true が Engine へ渡されていません。");
        Assert.Equal(ctx.CanonicalPath, options.OutputFilePath);

        // 後始末（preparation stop として停止させる）。
        await ctx.Coordinator.StopAsync();
    }

    // =====================================================================
    // F16 / F17 — preparation stop と session state の reset
    // =====================================================================

    [Fact]
    public async Task F16_preparation_stop_では_StepBuilder_も_transaction_も走らせない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        SeedCurrentSession(ctx);

        // preparation stop（CaptureStarted 前）の Engine 結果: pending は成立しない。
        ctx.Engine.StopResultFactory = () => PreparationStopResult(ctx);
        var jsonBefore = ReadProjectJson(ctx.Store, ctx.Project.Id);

        var start = await ctx.Coordinator.StartAsync(null, null, null);
        Assert.True(start.Succeeded, start.ErrorMessage);

        var outcome = await ctx.Coordinator.StopAsync();

        // 操作記録が始まっていない recording は integrated recording として確定しない。
        Assert.Equal(RecordingStopStatus.EventCaptureFailed, outcome.Status);
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.Equal(0, ctx.Engine.AbortCallCount); // pending が無いので Abort も不要
        Assert.Equal(jsonBefore, ReadProjectJson(ctx.Store, ctx.Project.Id));
        Assert.Equal(OldFill, File.ReadAllBytes(ctx.CanonicalPath)[0]);
    }

    [Fact]
    public async Task F17_stop_後の_session_state_が_次_recording_へ漏れない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Engine.StopResultFactory = () => PreparationStopResult(ctx);

        await ctx.Coordinator.StartAsync(null, null, null);
        await ctx.Coordinator.StopAsync();

        // session metadata / fault flag が reset されている。
        Assert.Null(ctx.Coordinator.SessionProjectId);
        Assert.False(ctx.Coordinator.HasEventCaptureFault);
        Assert.False(ctx.Coordinator.IsSessionActive);

        // 次の recording をそのまま開始できる（stale state で block されない）。
        var second = await ctx.Coordinator.StartAsync(null, null, null);
        Assert.True(second.Succeeded, second.ErrorMessage);
        Assert.Equal(ctx.Project.Id, ctx.Coordinator.SessionProjectId);

        await ctx.Coordinator.StopAsync();
    }

    // =====================================================================
    // F18b — EventCapture fault 経路でも pending を残さない（次回 StartAsync が拒否されない）
    // =====================================================================

    [Fact]
    public async Task F18b_EventCapture_fault_でも_pending_を残さず次の_StartAsync_が拒否されない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var staging = WriteStagingFile(temp);
        SeedCurrentSession(ctx);

        ctx.Engine.StopResultFactory = () => new RecordingResult
        {
            FilePath = staging,
            Duration = TimeSpan.FromSeconds(5),
            StartedAtUtc = DateTimeOffset.UtcNow,
            PendingCommit = true,
            PendingCommitPath = ctx.CanonicalPath,
        };
        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, [NewFill, NewFill, NewFill]);

        // CaptureStarted を発火させない = 操作記録が 1 件も取れていない recording。
        await ctx.Coordinator.StartAsync(null, null, null);

        var outcome = await ctx.Coordinator.StopAsync();

        // integrated recording として確定しない（canonical は変更されない）が、確定待ちは残さない。
        Assert.Equal(RecordingStopStatus.EventCaptureFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.False(ctx.Engine.HasPending, "確定待ちが残っています（次回 StartAsync が拒否される）。");
        Assert.Equal(OldFill, File.ReadAllBytes(ctx.CanonicalPath)[0]);

        // 次の recording を開始できる。
        ctx.Engine.StopResultFactory = () => PreparationStopResult(ctx);
        var next = await ctx.Coordinator.StartAsync(null, null, null);
        Assert.True(next.Succeeded, next.ErrorMessage);

        await ctx.Coordinator.StopAsync();
    }

    // =====================================================================
    // T2〜T5 — CurrentProject の publish（threading regression fix 後の Coordinator 責務）
    // =====================================================================
    // 2026-09-25 の runtime smoke で、context-free な pipeline が thread pool の継続から
    // CurrentProjectContext.SetCurrent を呼び、同期 CurrentProjectChanged → View の WPF 操作で
    // cross-thread 例外（プロセス終了）になる regression を検出した。修正で publish は UI-affine な
    // Coordinator（PublishFinalizedProject）へ移した。ここでは identity guard と分岐を検証する。
    //
    // 「UI thread から呼ぶこと」自体は WPF Dispatcher を test へ持ち込まない方針のため自動 test では
    // 被覆せず、runtime smoke と StopAsync の ConfigureAwait(true)（UI context へ戻す境界）で担保する。

    private static TrainingProject MakeProject(string title, int revision) =>
        new() { Id = Guid.NewGuid(), Title = title, Revision = revision };

    [Fact]
    public void T2_success_かつ同一_Project_なら_CurrentProject_が_committed_へ更新される()
    {
        var current = new CurrentProjectContext();
        var original = MakeProject("対象", 1);
        current.SetCurrent(original);

        var committed = MakeProject("対象", 2);
        committed.Id = original.Id; // 同じ Project の更新後 snapshot

        var result = new RecordingFinalizationPipelineResult(
            RecordingStopOutcome.Success("C:\\nonexistent\\recording.mp4"), committed);

        RecordingCoordinator.PublishFinalizedProject(current, original.Id, result);

        Assert.Same(committed, current.CurrentProject);
        Assert.Equal(2, current.CurrentProject!.Revision);
    }

    [Fact]
    public void T3_success_でも別_Project_が_current_なら巻き戻さない()
    {
        var current = new CurrentProjectContext();
        var other = MakeProject("別", 5);
        current.SetCurrent(other);

        var target = MakeProject("対象", 1);
        var committed = MakeProject("対象", 2);
        committed.Id = target.Id;

        var result = new RecordingFinalizationPipelineResult(
            RecordingStopOutcome.Success("C:\\nonexistent\\recording.mp4"), committed);

        RecordingCoordinator.PublishFinalizedProject(current, target.Id, result);

        Assert.Same(other, current.CurrentProject);
    }

    [Fact]
    public void T4_failure_では_committed_が_null_なので_CurrentProject_は不変()
    {
        var current = new CurrentProjectContext();
        var original = MakeProject("対象", 1);
        current.SetCurrent(original);

        var result = new RecordingFinalizationPipelineResult(
            RecordingStopOutcome.Failure(RecordingStopStatus.FinalizationFailed, "確定に失敗しました。"), null);

        RecordingCoordinator.PublishFinalizedProject(current, original.Id, result);

        Assert.Same(original, current.CurrentProject);
    }

    [Fact]
    public void T5_RecoveryRequired_では_committed_が_あっても_publish_しない()
    {
        var current = new CurrentProjectContext();
        var original = MakeProject("対象", 1);
        current.SetCurrent(original);

        // 防御的 guard の検証: failure status では committed が入っていても publish しない。
        var result = new RecordingFinalizationPipelineResult(
            RecordingStopOutcome.Failure(RecordingStopStatus.RecoveryRequired, "復旧用データを保持しています。"),
            MakeProject("対象", 2));

        RecordingCoordinator.PublishFinalizedProject(current, original.Id, result);

        Assert.Same(original, current.CurrentProject);
    }

    /// <summary>preparation stop（pending 無し・Duration 0）の Engine 結果。</summary>
    private static RecordingResult PreparationStopResult(WiringContext ctx) =>
        new()
        {
            FilePath = ctx.CanonicalPath,
            Duration = TimeSpan.Zero,
            StartedAtUtc = DateTimeOffset.UtcNow,
            PendingCommit = false,
            PendingCommitPath = ctx.CanonicalPath,
        };
}
