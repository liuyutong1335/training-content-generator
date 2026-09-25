// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.Capture;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// E+F-A: <see cref="RecordingFinalizationTransaction"/> の transaction boundary。
///
/// <para>
/// 対象は「DeferredCommit 状態の録画を canonical へ確定し、同じ logical transaction で candidate
/// project.json を保存する」境界のみ。実ユーザーの Projects directory は使わず、必ず temp 配下で行う。
/// 録画そのものは fake <see cref="IRecordingEngine"/> で置き換える（WPF / 実機は不要）。
/// </para>
/// </summary>
public class RecordingFinalizationTransactionTests
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
                // temp の後始末失敗でテストを落とさない
            }
        }
    }

    /// <summary>最小の fake engine。Commit の呼出回数と挙動だけを制御できればよい。</summary>
    private sealed class FakeRecordingEngine : IRecordingEngine
    {
        private readonly Func<RecordingResult> _onCommit;

        public FakeRecordingEngine(Func<RecordingResult> onCommit) => _onCommit = onCommit;

        public int CommitCallCount { get; private set; }

        public int AbortCallCount { get; private set; }

        public RecordingResult CommitPendingRecording()
        {
            CommitCallCount++;
            return _onCommit();
        }

        public void AbortPendingRecording() => AbortCallCount++;

#pragma warning disable CS0067 // test fake: この test では購読しない interface event
        public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

        public event EventHandler? CaptureStarted;
#pragma warning restore CS0067

        public IReadOnlyList<DisplayDevice> GetDisplays() => [];

        public IReadOnlyList<AudioDevice> GetMicrophones() => [];

        public IReadOnlyList<AudioDevice> GetSystemAudioDevices() => [];

        // 録画を開始しない test なので、これらは呼ばれたら失敗させる（fake の範囲を明示する）。
        public Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("この test では録画を開始しません。");

        public Task PauseAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResumeAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>1 test 分の前提（既存 canonical あり・staging あり・candidate 完成済み）。</summary>
    private sealed record Scenario(
        ProjectStore Store,
        TrainingProject Persisted,
        TrainingProject Candidate,
        string Canonical,
        string Staging,
        string JsonBefore);

    private static async Task<Scenario> SetupScenarioAsync(TempProjectsRoot temp)
    {
        var store = new ProjectStore(temp.Root);
        var persisted = await store.CreateProjectAsync("録画 finalize テスト");

        var canonical = store.GetRecordingOutputPath(persisted.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
        File.WriteAllBytes(canonical, Bytes(OldFill));

        var staging = WriteStagingFile(temp.Root, NewFill);
        var candidate = await BuildCandidateAsync(store, persisted.Id, persisted.Revision);

        return new Scenario(store, persisted, candidate, canonical, staging, ReadProjectJson(store, persisted.Id));
    }

    /// <summary>呼出側が完成させた candidate（Revision = base + 1・Recording 付き）を作る。</summary>
    private static async Task<TrainingProject> BuildCandidateAsync(
        ProjectStore store,
        Guid projectId,
        int baseRevision,
        long durationMs = 2000)
    {
        var persisted = await store.LoadProjectAsync(projectId)
            ?? throw new InvalidOperationException("test 前提が壊れています。");

        return new TrainingProject
        {
            SchemaVersion = persisted.SchemaVersion,
            Id = persisted.Id,
            Title = persisted.Title,
            Objective = persisted.Objective,
            TargetAudience = persisted.TargetAudience,
            Prerequisites = persisted.Prerequisites,
            CreatedAtUtc = persisted.CreatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            Revision = baseRevision + 1,
            Recording = new RecordingInfo
            {
                MediaPath = ProjectStore.RecordingMediaPath,
                StartedAtUtc = DateTimeOffset.UtcNow,
                DurationMs = durationMs,
            },
            Steps = persisted.Steps,
            Outputs = persisted.Outputs,
        };
    }

    private static byte[] Bytes(byte fill, int length = 4096)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>確定待ち staging として実ファイルを作る。</summary>
    private static string WriteStagingFile(string directory, byte fill = NewFill, int length = 4096)
    {
        var path = Path.Combine(directory, $"recording.staging-{Guid.NewGuid():N}.mp4");
        File.WriteAllBytes(path, Bytes(fill, length));
        return path;
    }

    private static RecordingResult PendingResult(
        string stagingPath,
        string canonicalPath,
        bool pendingCommit = true,
        string? pendingCommitPath = null) =>
        new()
        {
            FilePath = stagingPath,
            Duration = TimeSpan.FromSeconds(2),
            StartedAtUtc = DateTimeOffset.UtcNow,
            PendingCommit = pendingCommit,
            PendingCommitPath = pendingCommitPath ?? canonicalPath,
        };

    /// <summary>engine の Commit が成功し、canonical へ新しい録画を書く挙動。</summary>
    private static Func<RecordingResult> CommitWrites(string canonicalPath, byte fill, Action? afterWrite = null) =>
        () =>
        {
            File.WriteAllBytes(canonicalPath, Bytes(fill));
            afterWrite?.Invoke();
            return new RecordingResult
            {
                FilePath = canonicalPath,
                Duration = TimeSpan.FromSeconds(2),
                StartedAtUtc = DateTimeOffset.UtcNow,
            };
        };

    /// <summary>
    /// project.json を保持し、Save を必ず失敗させる。<see cref="FileShare.Read"/> にするのが要点
    /// （読みは許可しつつ <c>FileShare.Delete</c> を与えないため、Save 側の置換だけが失敗する）。
    /// </summary>
    private static FileStream LockProjectJson(ProjectStore store, Guid projectId) =>
        new(
            Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

    private static string ReadProjectJson(ProjectStore store, Guid projectId) =>
        File.ReadAllText(Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName));

    /// <summary>transaction workspace が片付いていること（残っているなら空であること）。</summary>
    private static void AssertWorkspaceCleaned(RecordingFinalizationTransaction transaction, Guid projectId)
    {
        var projectWorkspace = Path.Combine(transaction.TransactionsRoot, projectId.ToString("D"));
        Assert.False(
            Directory.Exists(projectWorkspace) && Directory.GetFileSystemEntries(projectWorkspace).Length > 0,
            $"transaction workspace が残っています: {projectWorkspace}");
    }

    private static async Task<RecordingFinalizationCommitResult> RunAsync(
        Scenario scenario,
        FakeRecordingEngine engine,
        RecordingResult pendingResult)
    {
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);
        return await transaction.CommitAsync(
            scenario.Persisted.Id, scenario.Candidate, scenario.Persisted.Revision, pendingResult);
    }

    // =====================================================================
    // T1 / T2 — 成功経路
    // =====================================================================

    [Fact]
    public async Task T_EFA_01_既存canonicalあり_成功で置換と保存とcleanupが行われる()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        var result = await transaction.CommitAsync(
            scenario.Persisted.Id,
            scenario.Candidate,
            scenario.Persisted.Revision,
            PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.Committed, result.Status);
        Assert.Equal(NewFill, File.ReadAllBytes(scenario.Canonical)[0]);

        var saved = await scenario.Store.LoadProjectAsync(scenario.Persisted.Id);
        Assert.NotNull(saved);
        Assert.Equal(scenario.Persisted.Revision + 1, saved!.Revision);
        Assert.Equal(ProjectStore.RecordingMediaPath, saved.Recording!.MediaPath);

        AssertWorkspaceCleaned(transaction, scenario.Persisted.Id);
    }

    [Fact]
    public async Task T_EFA_02_既存canonicalなし_成功でcanonicalが新規作成される()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        File.Delete(scenario.Canonical); // 既存 canonical なしの状態にする

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        var result = await transaction.CommitAsync(
            scenario.Persisted.Id,
            scenario.Candidate,
            scenario.Persisted.Revision,
            PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.Committed, result.Status);
        Assert.True(File.Exists(scenario.Canonical));
        Assert.Equal(NewFill, File.ReadAllBytes(scenario.Canonical)[0]);

        var saved = await scenario.Store.LoadProjectAsync(scenario.Persisted.Id);
        Assert.Equal(scenario.Persisted.Revision + 1, saved!.Revision);
        AssertWorkspaceCleaned(transaction, scenario.Persisted.Id);
    }

    // =====================================================================
    // T3 — Revision gate（SourceChanged / ProjectNotFound）
    // =====================================================================

    [Fact]
    public async Task T_EFA_03_persisted_Revision_が期待値と違えば_SourceChanged_で何も変更しない()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);

        // 録画中に別の更新が入った状況を作る（persisted の Revision だけが期待 base から進む）。
        // candidate は録画開始時の base から作られているため、保存すると古い内容で上書きしてしまう。
        var external = await scenario.Store.LoadProjectAsync(scenario.Persisted.Id);
        external!.Revision += 1;
        external.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await scenario.Store.SaveProjectAsync(external);
        var jsonAfterExternalUpdate = ReadProjectJson(scenario.Store, scenario.Persisted.Id);

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        var result = await transaction.CommitAsync(
            scenario.Persisted.Id,
            scenario.Candidate,          // Revision = base + 1（preflight は通る）
            scenario.Persisted.Revision, // 期待 base = 録画開始時の値（persisted は既に base + 1）
            PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.SourceChanged, result.Status);
        Assert.Equal(0, engine.CommitCallCount);
        Assert.Equal(OldFill, File.ReadAllBytes(scenario.Canonical)[0]);
        Assert.Equal(jsonAfterExternalUpdate, ReadProjectJson(scenario.Store, scenario.Persisted.Id));
    }

    [Fact]
    public async Task T_EFA_03b_persisted_Project_が無ければ_ProjectNotFound()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        await scenario.Store.DeleteProjectAsync(scenario.Persisted.Id);

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.ProjectNotFound, result.Status);
        Assert.Equal(0, engine.CommitCallCount);
    }

    // =====================================================================
    // T4〜T11 — preflight reject（engine Commit を呼ばない / 何も変更しない）
    // =====================================================================

    [Fact]
    public async Task T_EFA_04_PendingCommit_false_は拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(
            scenario, engine, PendingResult(scenario.Staging, scenario.Canonical, pendingCommit: false));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_05_PendingCommitPath_が_canonical_と違えば拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(
            scenario,
            engine,
            PendingResult(scenario.Staging, scenario.Canonical, pendingCommitPath: scenario.Canonical + ".other"));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_06_staging_が存在しなければ拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(
            scenario, engine, PendingResult(scenario.Staging + ".missing", scenario.Canonical));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_07_staging_が0バイトなら拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));
        var emptyStaging = WriteStagingFile(temp.Root, NewFill, length: 0);

        var result = await RunAsync(scenario, engine, PendingResult(emptyStaging, scenario.Canonical));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_08_candidate_Id_が_projectId_と違えば拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        scenario.Candidate.Id = Guid.NewGuid();

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_09_candidate_Revision_が_base_plus_1_でなければ拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        scenario.Candidate.Revision += 1;

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_10_candidate_Recording_が_null_なら拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        scenario.Candidate.Recording = null;

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        AssertPreflightRejected(result, engine, scenario);
    }

    [Fact]
    public async Task T_EFA_11_candidate_Recording_MediaPath_が契約と違えば拒否する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        scenario.Candidate.Recording!.MediaPath = "raw/other.mp4";

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        AssertPreflightRejected(result, engine, scenario);
    }

    /// <summary>T4〜T11 の共通 assertion: reject され、engine も canonical も project.json も動いていない。</summary>
    private static void AssertPreflightRejected(
        RecordingFinalizationCommitResult result,
        FakeRecordingEngine engine,
        Scenario scenario)
    {
        Assert.Equal(RecordingFinalizationCommitStatus.InvalidPendingRecording, result.Status);
        Assert.Equal(0, engine.CommitCallCount);
        Assert.Equal(OldFill, File.ReadAllBytes(scenario.Canonical)[0]);
        Assert.Equal(scenario.JsonBefore, ReadProjectJson(scenario.Store, scenario.Persisted.Id));
    }

    // =====================================================================
    // T12 — engine Commit 失敗
    // =====================================================================

    [Fact]
    public async Task T_EFA_12_engine_commit_が失敗しても旧canonicalと旧project_jsonが残る()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);

        var engine = new FakeRecordingEngine(() => throw new InvalidOperationException("Move 失敗を模擬"));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        var result = await transaction.CommitAsync(
            scenario.Persisted.Id,
            scenario.Candidate,
            scenario.Persisted.Revision,
            PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.Failed, result.Status);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(1, engine.CommitCallCount);

        // transaction は AbortPendingRecording を自動では呼ばない（staging が新録画の唯一のコピーになり得るため）
        Assert.Equal(0, engine.AbortCallCount);
        Assert.True(File.Exists(scenario.Staging), "staging は呼出側の判断まで残す必要がある");

        Assert.Equal(OldFill, File.ReadAllBytes(scenario.Canonical)[0]);
        Assert.Equal(scenario.JsonBefore, ReadProjectJson(scenario.Store, scenario.Persisted.Id));
        AssertWorkspaceCleaned(transaction, scenario.Persisted.Id);
    }

    // =====================================================================
    // T13 / T14 — project.json 保存失敗時の canonical rollback
    // =====================================================================

    [Fact]
    public async Task T_EFA_13_project_save失敗_既存canonicalあり_旧canonicalへ戻る()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        RecordingFinalizationCommitResult result;
        using (LockProjectJson(scenario.Store, scenario.Persisted.Id))
        {
            result = await transaction.CommitAsync(
                scenario.Persisted.Id,
                scenario.Candidate,
                scenario.Persisted.Revision,
                PendingResult(scenario.Staging, scenario.Canonical));
        }

        Assert.Equal(RecordingFinalizationCommitStatus.Failed, result.Status);
        Assert.False(result.RecoveryRequired);
        Assert.Equal(1, engine.CommitCallCount);

        // commit 後の失敗なので canonical は新録画へ置換済み → rollback で旧録画へ戻る
        Assert.Equal(OldFill, File.ReadAllBytes(scenario.Canonical)[0]);
        Assert.Equal(scenario.JsonBefore, ReadProjectJson(scenario.Store, scenario.Persisted.Id));
        AssertWorkspaceCleaned(transaction, scenario.Persisted.Id);
    }

    [Fact]
    public async Task T_EFA_14_project_save失敗_既存canonicalなし_新canonicalが削除される()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        File.Delete(scenario.Canonical); // 既存 canonical なしの状態にする

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        RecordingFinalizationCommitResult result;
        using (LockProjectJson(scenario.Store, scenario.Persisted.Id))
        {
            result = await transaction.CommitAsync(
                scenario.Persisted.Id,
                scenario.Candidate,
                scenario.Persisted.Revision,
                PendingResult(scenario.Staging, scenario.Canonical));
        }

        Assert.Equal(RecordingFinalizationCommitStatus.Failed, result.Status);
        Assert.False(result.RecoveryRequired);

        // 旧 canonical が無かったので、置換済みの新 canonical を取り除く
        Assert.False(File.Exists(scenario.Canonical));
        Assert.Equal(scenario.JsonBefore, ReadProjectJson(scenario.Store, scenario.Persisted.Id));
        AssertWorkspaceCleaned(transaction, scenario.Persisted.Id);
    }

    // =====================================================================
    // T15 / T16 — 成功経路の保存と result
    // =====================================================================

    /// <summary>
    /// 成功経路が candidate をそのまま保存すること（= 単一の logical save）を確認する。
    ///
    /// <para>
    /// 「SaveProjectAsync の呼出回数」は <see cref="ProjectStore"/> が concrete dependency のため
    /// 実行時には観測できない（観測のためだけに interface / abstraction を追加しない方針）。
    /// そのため本 test は「保存された内容が candidate と完全に一致し、中間状態が残っていないこと」
    /// ＋「transaction 内の SaveProjectAsync 呼出が 1 call site であること」（コード上の性質）で代替する。
    /// </para>
    /// </summary>
    [Fact]
    public async Task T_EFA_15_成功経路は_candidate_を_exactly_once_で保存する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        scenario.Candidate.Recording!.DurationMs = 4321;

        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.Committed, result.Status);

        var saved = await scenario.Store.LoadProjectAsync(scenario.Persisted.Id);
        Assert.NotNull(saved);
        Assert.Equal(scenario.Candidate.Revision, saved!.Revision);
        Assert.Equal(scenario.Candidate.UpdatedAtUtc, saved.UpdatedAtUtc);
        Assert.Equal(4321, saved.Recording!.DurationMs);
        Assert.Equal(scenario.Candidate.Recording.MediaPath, saved.Recording.MediaPath);
    }

    [Fact]
    public async Task T_EFA_16_成功resultに_Project_と_canonicalPath_が入る()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill));

        var result = await RunAsync(scenario, engine, PendingResult(scenario.Staging, scenario.Canonical));

        Assert.Equal(RecordingFinalizationCommitStatus.Committed, result.Status);
        Assert.NotNull(result.Project);
        Assert.Equal(scenario.Candidate.Revision, result.Project!.Revision);
        Assert.Equal(scenario.Canonical, result.CanonicalPath);
        Assert.False(result.RecoveryRequired);
        Assert.Null(result.RecoveryDirectory);
    }

    // =====================================================================
    // T17 — rollback 失敗（recovery backup の保護）
    // =====================================================================

    [Fact]
    public async Task T_EFA_17_rollback失敗時は_RecoveryRequired_で_backup_を保持する()
    {
        using var temp = new TempProjectsRoot();
        var scenario = await SetupScenarioAsync(temp);

        // commit 成功直後に canonical を read 共有で開き、rollback の上書き（File.Copy overwrite）を
        // 失敗させる。project.json 側も lock して Save を失敗させる。
        FileStream? canonicalLock = null;
        var engine = new FakeRecordingEngine(CommitWrites(scenario.Canonical, NewFill, () =>
            canonicalLock = new FileStream(scenario.Canonical, FileMode.Open, FileAccess.Read, FileShare.Read)));
        var transaction = new RecordingFinalizationTransaction(engine, scenario.Store);

        RecordingFinalizationCommitResult result;
        try
        {
            using (LockProjectJson(scenario.Store, scenario.Persisted.Id))
            {
                result = await transaction.CommitAsync(
                    scenario.Persisted.Id,
                    scenario.Candidate,
                    scenario.Persisted.Revision,
                    PendingResult(scenario.Staging, scenario.Canonical));
            }
        }
        finally
        {
            canonicalLock?.Dispose();
        }

        Assert.Equal(RecordingFinalizationCommitStatus.Failed, result.Status);
        Assert.True(result.RecoveryRequired);
        Assert.NotNull(result.RecoveryDirectory);

        // backup が旧 canonical の唯一の複製になり得るため、workspace を残す
        var backup = Path.Combine(result.RecoveryDirectory!, "backup", "recording.mp4");
        Assert.True(File.Exists(backup), $"recovery backup が残っていません: {backup}");
        Assert.Equal(OldFill, File.ReadAllBytes(backup)[0]);

        // test 自身の後始末（保持を確認した後なので消してよい）
        try
        {
            Directory.Delete(result.RecoveryDirectory!, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 後始末失敗でテストを落とさない
        }
    }
}
