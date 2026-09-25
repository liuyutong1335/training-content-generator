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
/// E+F-B: <see cref="RecordingFinalizationPipeline"/> の finalization 判定。
///
/// <para>
/// 実録画・実 EventCapture（global hook）は行わない。Engine は <see cref="FakeRecordingEngine"/>、
/// events.jsonl は test が直接 seed し、<see cref="RecordingFinalizationTransaction"/> は本物を使う。
/// 「current session の範囲だけを StepBuilder へ渡す」ことを直接検証する。
/// </para>
/// </summary>
public class RecordingFinalizationPipelineTests
{
    private const byte OldFill = 0x11;
    private const byte NewFill = 0xEE;

    // =====================================================================
    // 共通インフラ
    // =====================================================================

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

    private sealed record PipelineContext(
        ProjectStore Store,
        TrainingProject Project,
        FakeRecordingEngine Engine,
        RecordingFinalizationTransaction Transaction,
        RecordingFinalizationPipeline Pipeline,
        string CanonicalPath,
        string EventsPath);

    private static async Task<PipelineContext> SetupAsync(TempProjectsRoot temp, bool withExistingCanonical = true)
    {
        var store = new ProjectStore(temp.Root);
        var project = await store.CreateProjectAsync("録画 finalize テスト");
        var engine = new FakeRecordingEngine();

        var canonical = store.GetRecordingOutputPath(project.Id);
        engine.CanonicalPath = canonical;

        if (withExistingCanonical)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            File.WriteAllBytes(canonical, Bytes(OldFill));
        }

        var transaction = new RecordingFinalizationTransaction(engine, store);

        // pipeline は CurrentProjectContext を持たない（publish は RecordingCoordinator が UI thread で行う）。
        var pipeline = new RecordingFinalizationPipeline(engine, store, transaction);

        return new PipelineContext(
            store,
            project,
            engine,
            transaction,
            pipeline,
            canonical,
            Path.Combine(store.GetProjectDirectory(project.Id), ProjectStore.EventsFileName));
    }

    private static byte[] Bytes(byte fill, int length = 2048)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    private static string WriteStagingFile(TempProjectsRoot temp, byte fill = NewFill)
    {
        var path = Path.Combine(temp.Root, $"recording.staging-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, Bytes(fill));
        return path;
    }

    private static RecordingOptions Options(string canonicalPath) =>
        new() { OutputFilePath = canonicalPath, DeferredCommit = true };

    private static RecordingResult PendingResult(string stagingPath, string canonicalPath, long durationMs = 5000) =>
        new()
        {
            FilePath = stagingPath,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            StartedAtUtc = DateTimeOffset.UtcNow,
            PendingCommit = true,
            PendingCommitPath = canonicalPath,
        };

    /// <summary>events.jsonl の 1 行（契約 §8.1）。</summary>
    private static string Line(Guid id, int seq, long timestampMs, string type, string payload) =>
        $$"""{"schemaVersion":1,"id":"{{id:D}}","seq":{{seq}},"timestampMs":{{timestampMs}},"type":"{{type}}","payload":{{payload}}}""";

    /// <summary>previous session（StepBuilder へ渡ってはいけない行を含む）。</summary>
    private static (string Jsonl, Guid MouseClickId) BuildPreviousSession()
    {
        var mouseClickId = Guid.NewGuid();
        var sb = new StringBuilder();
        sb.Append(Line(Guid.NewGuid(), 1, 0, "recording.started", "{}")).Append('\n');
        sb.Append(Line(mouseClickId, 2, 500, "mouse.click",
            """{"x":10,"y":20,"button":"left","clickCount":1}""")).Append('\n');
        sb.Append(Line(Guid.NewGuid(), 3, 900, "recording.stopped", "{}")).Append('\n');
        return (sb.ToString(), mouseClickId);
    }

    /// <summary>
    /// current session（今回の録画で append された範囲）。
    /// <paramref name="extraLine"/> は <c>recording.started</c> の直後に挿入する
    /// （seq は呼出側が 5 を渡す前提。以降の seq は自動で繰り上がる）。
    /// </summary>
    private static (string Jsonl, Guid ShortcutId) BuildCurrentSession(string extraLine = "")
    {
        var shortcutId = Guid.NewGuid();
        var seq = 4;

        var sb = new StringBuilder();
        sb.Append(Line(Guid.NewGuid(), seq++, 0, "recording.started", "{}")).Append('\n');

        if (extraLine.Length > 0)
        {
            sb.Append(extraLine).Append('\n');
            seq++; // 呼出側が渡した行ぶん繰り上げる
        }

        sb.Append(Line(shortcutId, seq++, 1000, "keyboard.shortcut", """{"shortcut":"Ctrl+C"}""")).Append('\n');
        sb.Append(Line(Guid.NewGuid(), seq++, 1500, "recording.stopped", "{}")).Append('\n');
        return (sb.ToString(), shortcutId);
    }

    private static RecordingFinalizationInput Input(
        PipelineContext ctx,
        string stagingPath,
        long offset,
        long durationMs = 5000,
        bool eventCaptureFaulted = false,
        int? baseRevision = null) =>
        new(
            ctx.Project.Id,
            Options(ctx.CanonicalPath),
            PendingResult(stagingPath, ctx.CanonicalPath, durationMs),
            offset,
            baseRevision ?? ctx.Project.Revision,
            eventCaptureFaulted,
            eventCaptureFaulted ? "操作記録の取得に失敗しました。" : null);

    /// <summary>previous + current session を持つ events.jsonl を作り、offset を返す。</summary>
    private static (long Offset, Guid MouseClickId, Guid ShortcutId) SeedSessions(
        PipelineContext ctx,
        string currentExtraLine = "")
    {
        var (previous, mouseClickId) = BuildPreviousSession();
        File.WriteAllText(ctx.EventsPath, previous);

        var offset = new FileInfo(ctx.EventsPath).Length;

        var (current, shortcutId) = BuildCurrentSession(currentExtraLine);
        File.AppendAllText(ctx.EventsPath, current);

        return (offset, mouseClickId, shortcutId);
    }

    private static string ReadProjectJson(ProjectStore store, Guid projectId) =>
        File.ReadAllText(Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName));

    private static FileStream LockProjectJson(ProjectStore store, Guid projectId) =>
        new(
            Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

    // =====================================================================
    // F3 / F4 / F5 — 成功経路と re-record isolation
    // =====================================================================

    [Fact]
    public async Task F03_成功で_pending_recording_が確定し_current_session_の_Step_が保存される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);

        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.Saved, outcome.Status);
        Assert.Equal(1, ctx.Engine.CommitCallCount);
        Assert.Equal(NewFill, File.ReadAllBytes(ctx.CanonicalPath)[0]);

        // pipeline は committed Project を返す（CurrentProject への反映は呼出側 = UI thread の責務）。
        Assert.NotNull(outcome.CommittedProject);
        Assert.Equal(2, outcome.CommittedProject!.Revision);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.NotNull(saved);
        Assert.Single(saved!.Steps);
    }

    [Fact]
    public async Task F04_candidate_は_RecordingInfo_Steps_Revision_UpdatedAtUtc_を更新し_Outputs_を保持する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        // Outputs metadata を積んでおく（finalization が消してはいけない）。
        var before = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        before!.Outputs.TrainingVideo = new GeneratedArtifact
        {
            Path = "output/training_video.mp4",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = before.Revision,
        };
        await ctx.Store.SaveProjectAsync(before);
        var revisionBefore = before.Revision;
        var updatedBefore = before.UpdatedAtUtc;

        var (offset, _, shortcutId) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.Saved, outcome.Status);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.NotNull(saved);
        Assert.Equal(revisionBefore + 1, saved!.Revision);
        Assert.True(saved.UpdatedAtUtc > updatedBefore, "UpdatedAtUtc が更新されていません。");

        Assert.NotNull(saved.Recording);
        Assert.Equal(ProjectStore.RecordingMediaPath, saved.Recording!.MediaPath);
        Assert.Equal(5000L, saved.Recording.DurationMs);
        Assert.False(saved.Recording.HasSystemAudio);

        var step = Assert.Single(saved.Steps);
        Assert.Equal(StepActions.Shortcut, step.Action);
        Assert.Equal(1, step.Order);
        Assert.Equal(1000L, step.StartMs);
        Assert.Contains(shortcutId, step.SourceEventIds);

        // Outputs は保持される（Revision +1 で stale になるだけ）。
        Assert.NotNull(saved.Outputs.TrainingVideo);
        Assert.Equal(revisionBefore, saved.Outputs.TrainingVideo!.SourceRevision);
    }

    [Fact]
    public async Task F05_re_record_で_old_session_の_Event_を_Step_に混入させない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, mouseClickId, shortcutId) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));

        var eventsBefore = File.ReadAllText(ctx.EventsPath);

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.Saved, outcome.Status);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        var step = Assert.Single(saved!.Steps);

        // current session の Step だけが入る。
        Assert.Contains(shortcutId, step.SourceEventIds);
        Assert.DoesNotContain(mouseClickId, step.SourceEventIds);
        Assert.DoesNotContain(saved.Steps, s => s.Action == StepActions.Click);

        // events.jsonl 自体は previous + current の両方を保持したまま（削除しない）。
        Assert.Equal(eventsBefore, File.ReadAllText(ctx.EventsPath));
    }

    // =====================================================================
    // F6 / F7 — StepBuilder warnings / errors
    // =====================================================================

    [Fact]
    public async Task F06_StepBuilder_の_warning_は保存を止めない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        // sensitive な textEntry は warning になり Step を生成しない（payload 契約 §11.1）。
        // なお sensitive では keyCount（文字数）も保存禁止のため payload に含めない。
        var sensitiveLine = Line(Guid.NewGuid(), 5, 700, "keyboard.textEntry", """{"isSensitive":true}""");
        var (offset, _, shortcutId) = SeedSessions(ctx, sensitiveLine);
        var staging = WriteStagingFile(temp);
        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.Saved, outcome.Status);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        var step = Assert.Single(saved!.Steps);
        Assert.Contains(shortcutId, step.SourceEventIds);
    }

    [Fact]
    public async Task F07_StepBuilder_の_error_では_保存せず_pending_を_Abort_する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        var jsonBefore = ReadProjectJson(ctx.Store, ctx.Project.Id);
        var canonicalBefore = File.ReadAllBytes(ctx.CanonicalPath);

        // seq を重複させて StepBuilder を error にする（seq 5 が 2 回）。
        var dup = Line(Guid.NewGuid(), 5, 800, "mouse.click", """{"x":1,"y":1}""");
        File.AppendAllText(ctx.EventsPath, dup + "\n");

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.StepBuildFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(0, ctx.Engine.CommitCallCount);

        // canonical / project.json は変更されない（partial Steps も保存しない）。
        Assert.Equal(canonicalBefore, File.ReadAllBytes(ctx.CanonicalPath));
        Assert.Equal(jsonBefore, ReadProjectJson(ctx.Store, ctx.Project.Id));
    }

    // =====================================================================
    // F8〜F11 — 各 failure で Abort し、canonical / project.json を変更しない
    // =====================================================================

    [Fact]
    public async Task F08_EventCapture_fault_では_transaction_を実行せず_pending_を_Abort_する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        var jsonBefore = ReadProjectJson(ctx.Store, ctx.Project.Id);
        var canonicalBefore = File.ReadAllBytes(ctx.CanonicalPath);

        var outcome = await ctx.Pipeline.FinalizeAsync(
            Input(ctx, staging, offset, eventCaptureFaulted: true));

        Assert.Equal(RecordingStopStatus.EventCaptureFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.Equal(canonicalBefore, File.ReadAllBytes(ctx.CanonicalPath));
        Assert.Equal(jsonBefore, ReadProjectJson(ctx.Store, ctx.Project.Id));
    }

    [Fact]
    public async Task F09_SourceChanged_では_古い_base_で確定せず_pending_を_Abort_する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        // 録画中に別の更新が入った状況（persisted の Revision が base から進む）。
        var external = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        external!.Revision += 1;
        external.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await ctx.Store.SaveProjectAsync(external);

        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        var jsonAfterExternalUpdate = ReadProjectJson(ctx.Store, ctx.Project.Id);
        var canonicalBefore = File.ReadAllBytes(ctx.CanonicalPath);

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.SourceChanged, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.Equal(canonicalBefore, File.ReadAllBytes(ctx.CanonicalPath));
        Assert.Equal(jsonAfterExternalUpdate, ReadProjectJson(ctx.Store, ctx.Project.Id));
    }

    [Fact]
    public async Task F10_Project_が無ければ_pending_を_Abort_する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);

        await ctx.Store.DeleteProjectAsync(ctx.Project.Id);

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.FinalizationFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.False(File.Exists(ctx.CanonicalPath));
    }

    [Fact]
    public async Task F11_candidate_validation_failure_では_transaction_を呼ばない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        // Recording Duration を短くして「Step.StartMs > Recording.DurationMs」を作る（契約 §29 違反）。
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        var jsonBefore = ReadProjectJson(ctx.Store, ctx.Project.Id);
        var canonicalBefore = File.ReadAllBytes(ctx.CanonicalPath);

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset, durationMs: 500));

        Assert.Equal(RecordingStopStatus.FinalizationFailed, outcome.Status);
        Assert.Equal(0, ctx.Engine.CommitCallCount); // transaction の commit まで到達しない
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(canonicalBefore, File.ReadAllBytes(ctx.CanonicalPath));
        Assert.Equal(jsonBefore, ReadProjectJson(ctx.Store, ctx.Project.Id));
    }

    // =====================================================================
    // CurrentProject の publish は pipeline の責務ではない
    // =====================================================================
    // 旧 F12 / F13（pipeline が CurrentProject を差し替える）は、2026-09-25 の threading regression fix で
    // 責務を RecordingCoordinator.PublishFinalizedProject へ移したため、RecordingCoordinatorFinalizationWiringTests
    // 側の T2 / T3 で検証する。pipeline は committed Project を返すだけ（CurrentProjectContext に依存しない）。

    // =====================================================================
    // F14 / F15 — transaction failure / recovery
    // =====================================================================

    [Fact]
    public async Task F14_transaction_が_save_failure_で_rollback_した場合は_FinalizationFailed_で_committed_なし()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));

        RecordingFinalizationPipelineResult outcome;
        using (LockProjectJson(ctx.Store, ctx.Project.Id))
        {
            outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));
        }

        Assert.Equal(RecordingStopStatus.FinalizationFailed, outcome.Status);
        Assert.Null(outcome.CommittedProject); // 呼出側は publish しない

        // canonical は transaction 前（旧録画）へ戻る。commit 済みなので Abort は「解消済み」になる。
        Assert.Equal(OldFill, File.ReadAllBytes(ctx.CanonicalPath)[0]);
    }

    [Fact]
    public async Task F15_RecoveryRequired_では_自動_Abort_せず_committed_も返さない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);

        // commit 成功直後に canonical を lock し、rollback の復元を失敗させる。
        FileStream? canonicalLock = null;
        ctx.Engine.OnCommit = () =>
        {
            File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));
            canonicalLock = new FileStream(ctx.CanonicalPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        };

        RecordingFinalizationPipelineResult outcome;
        try
        {
            using (LockProjectJson(ctx.Store, ctx.Project.Id))
            {
                outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));
            }
        }
        finally
        {
            canonicalLock?.Dispose();
        }

        Assert.Equal(RecordingStopStatus.RecoveryRequired, outcome.Status);
        Assert.Equal(0, ctx.Engine.AbortCallCount); // recovery artifact の状態を変えない
        Assert.Null(outcome.CommittedProject);
    }

    // =====================================================================
    // §10 — current-session events の読み出し条件
    // =====================================================================

    [Fact]
    public async Task F07b_offset_未取得なら_StepBuildFailed_で_Abort_する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (_, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);

        var outcome = await ctx.Pipeline.FinalizeAsync(
            Input(ctx, staging, RecordingSessionEventsReader.NoOffset));

        Assert.Equal(RecordingStopStatus.StepBuildFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.Equal(OldFill, File.ReadAllBytes(ctx.CanonicalPath)[0]);
    }

    [Fact]
    public async Task F07c_events_jsonl_が無ければ_StepBuildFailed()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var staging = WriteStagingFile(temp);

        // CreateProjectAsync が空の events.jsonl を作るため、明示的に消して「無い」状態にする。
        File.Delete(ctx.EventsPath);
        Assert.False(File.Exists(ctx.EventsPath));

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset: 0));

        Assert.Equal(RecordingStopStatus.StepBuildFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
    }

    [Fact]
    public async Task F07d_offset_が現在長を超えていれば_StepBuildFailed()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset + 10_000));

        Assert.Equal(RecordingStopStatus.StepBuildFailed, outcome.Status);
        Assert.Equal(1, ctx.Engine.AbortCallCount);
    }

    // =====================================================================
    // F18 — success 後に engine pending が解消し、次の StartAsync が拒否されない
    // =====================================================================

    [Fact]
    public async Task F18_確定成功で_engine_pending_が解消し_次の_StartAsync_が拒否されない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);

        ctx.Engine.OnCommit = () => File.WriteAllBytes(ctx.CanonicalPath, Bytes(NewFill));
        ctx.Engine.StopResultFactory = () => PendingResult(staging, ctx.CanonicalPath);

        // 実 engine と同じ手順で Stop させる（確定待ちが立つ）。
        await ctx.Engine.StopAsync();
        Assert.True(ctx.Engine.HasPending);

        var outcome = await ctx.Pipeline.FinalizeAsync(Input(ctx, staging, offset));

        Assert.Equal(RecordingStopStatus.Saved, outcome.Status);
        Assert.False(ctx.Engine.HasPending, "確定待ちが残っています。");
        Assert.Equal(0, ctx.Engine.AbortCallCount);

        // pending が残っていれば fake の StartAsync が throw する。拒否されないことを固定する。
        await ctx.Engine.StartAsync(Options(ctx.CanonicalPath));
    }

    // =====================================================================
    // preparation stop（PendingCommit = false）は finalization の対象外
    // =====================================================================

    [Fact]
    public async Task F16b_PendingCommit_false_では_StepBuilder_も_transaction_も走らせない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var (offset, _, _) = SeedSessions(ctx);
        var staging = WriteStagingFile(temp);
        var jsonBefore = ReadProjectJson(ctx.Store, ctx.Project.Id);

        var notPending = new RecordingResult
        {
            FilePath = ctx.CanonicalPath,
            Duration = TimeSpan.Zero,
            StartedAtUtc = DateTimeOffset.UtcNow,
            PendingCommit = false,
            PendingCommitPath = ctx.CanonicalPath,
        };

        var outcome = await ctx.Pipeline.FinalizeAsync(new RecordingFinalizationInput(
            ctx.Project.Id, Options(ctx.CanonicalPath), notPending, offset, ctx.Project.Revision));

        Assert.Equal(RecordingStopStatus.FinalizationFailed, outcome.Status);
        Assert.Equal(0, ctx.Engine.AbortCallCount); // pending が無いので Abort も不要
        Assert.Equal(0, ctx.Engine.CommitCallCount);
        Assert.Equal(jsonBefore, ReadProjectJson(ctx.Store, ctx.Project.Id));
    }
}
