// UseWPF=true の project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.State;
using TrainingContent.Capture;
using TrainingContent.Core.Models;
using TrainingContent.EventCapture;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>
/// 担当A の <see cref="IRecordingEngine"/> と担当B の <see cref="OperationCaptureSession"/> を
/// App へ接続する Integration Layer。1 recording session の orchestration owner。
///
/// <para>
/// 責任: Engine / EventCapture の呼出順序、Recording session state、Project ID の固定、
/// RecordingOptions の構築、RecordingResult → RecordingInfo の mapping、ProjectStore への保存、
/// CurrentProjectContext の更新。
/// </para>
/// <para>
/// Canonical Timeline の 0ms は <see cref="IRecordingEngine.CaptureStarted"/>（実際の撮影開始瞬間）で
/// 揃える。StartAsync の戻りや StateChanged(Recording) では揃わない（WGC 初期化に ~2 秒かかる）。
/// 固定 ms の補正や <c>RebaseClockToNow()</c> は使わない。
/// </para>
/// <para>
/// 意図的にやらないこと: ScreenRecorderLib の直接操作、timeline の数値補正、Step 生成そのもの、AI、
/// EventCapture の再実装。finalization の判定（current session events の抽出 / StepBuilder /
/// candidate / validation / transaction / CurrentProject swap）は
/// <see cref="RecordingFinalizationPipeline"/> に置き、ここは Engine / EventCapture の呼出順序と
/// session state に専念する。EventCapture が壊れた recording は integrated recording として
/// project.json に確定しない。
/// </para>
/// </summary>
public sealed class RecordingCoordinator
{
    /// <summary>
    /// 録画停止後に project.json の保存へ失敗したときの message。
    /// Project directory が失われている場合など MP4 の存在を断定できない状況があるため、
    /// 「作成されました」とは言い切らず、確認を促す表現にする。rollback / recovery は D8 の範囲。
    /// </summary>
    private const string SaveFailedMessage =
        "録画停止後、プロジェクト情報の保存に失敗しました。録画ファイルの状態を確認してください。";

    /// <summary>EventCapture 側の失敗をユーザーへ伝える message。</summary>
    private const string EventCaptureFaultedUserMessage =
        "操作記録の取得に失敗しました。録画を停止して再試行してください。";

    /// <summary>Engine と EventCapture の論理時間差の許容値。超えたら Trace Warning を出す。</summary>
    private const double DurationToleranceMs = 500;

    /// <summary>撮影開始前（準備中）または EventCapture 故障中に操作されたときの message。</summary>
    private const string NotReadyMessage = "録画の準備が完了していません。";

    private readonly IRecordingEngine _engine;
    private readonly ProjectStore _projectStore;
    private readonly CurrentProjectContext _currentProject;
    private readonly RecordingFinalizationPipeline _finalizationPipeline;

    private Guid? _sessionProjectId;
    private RecordingOptions? _sessionOptions;

    /// <summary>
    /// 録画開始時点の <c>project.Revision</c>（candidate の base）。UI thread だけが読み書きする。
    /// </summary>
    private int _sessionBaseRevision;

    /// <summary>
    /// session 開始直前の events.jsonl byte length（= 今回 session が append した範囲の開始位置）。
    /// Engine callback thread（<see cref="OnCaptureStarted"/>）が書き、UI thread（<see cref="StopAsync"/>）が
    /// 読むため Volatile で扱う。未取得は <see cref="RecordingSessionEventsReader.NoOffset"/>。
    /// </summary>
    private long _sessionEventsStartOffset = RecordingSessionEventsReader.NoOffset;

    private bool _isCommandRunning;

    /// <summary>
    /// 正常 Stop の finalization（EventCapture 停止 → Dispose → 保存）が進行中か。
    ///
    /// <para>
    /// EventCapture の停止は background で実行されるため、Engine が先に Idle を通知しても
    /// まだ停止処理が動いている時間帯がある。その間に navigation / window close /
    /// 新規 recording が通らないよう、<see cref="IsSessionActive"/> へ含めて lock を維持する。
    /// <see cref="RecordingState"/> の mirror とは独立に、この停止処理の間だけ立つ。
    /// </para>
    /// </summary>
    private volatile bool _isStopFinalizing;

    // IRecordingEngine は State を公開していない（具象 Engine のみ）。境界は interface に保つため、
    // StateChanged から受け取った状態をここで保持する。Engine を動かすのは本 Coordinator だけなので
    // この mirror が唯一の状態源になる。
    // Engine callback thread が書き UI thread が読むため volatile にする。
    private volatile RecordingState _state = RecordingState.Idle;

    // ---- EventCapture integration ----
    // 1 recording session につき 1 instance。所有権の受け渡しは TakeOperationSession() に集約し、
    // 参照の公開は Volatile.Write、取り出しは Interlocked.Exchange で行う。
    private OperationCaptureSession? _operationSession;

    /// <summary><c>session.Start()</c> が成功したか。Engine callback thread から更新される。</summary>
    private volatile bool _captureReady;

    /// <summary>EventCapture 側で回復不能な失敗が起きたか。</summary>
    private volatile bool _eventCaptureFaulted;

    private string? _eventCaptureFaultMessage;

    public RecordingCoordinator(
        IRecordingEngine engine,
        ProjectStore projectStore,
        CurrentProjectContext currentProject,
        RecordingFinalizationTransaction finalizationTransaction)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(finalizationTransaction);

        _engine = engine;
        _projectStore = projectStore;
        _currentProject = currentProject;

        // finalization の判定は pipeline が持ち、transaction はその唯一の commit boundary。
        // pipeline は context-free（CurrentProject へは触れない）。publish は UI-affine なこの class が行う。
        _finalizationPipeline = new RecordingFinalizationPipeline(
            engine, projectStore, finalizationTransaction);

        // Engine の StateChanged / CaptureStarted は UI thread から来る保証がない。ここでは中継するだけにして、
        // Dispatcher への marshal は UI 側（RecordingView / MainWindow）の責任にする。
        _engine.StateChanged += OnEngineStateChanged;
        _engine.CaptureStarted += OnCaptureStarted;
    }

    /// <summary>Engine / EventCapture の状態変化、command の開始/終了を UI へ通知する。</summary>
    public event EventHandler? ActivityChanged;

    /// <summary>Engine の現在状態（StateChanged から保持した mirror）。</summary>
    public RecordingState State => _state;

    /// <summary>Start/Pause/Resume/Stop のいずれかが実行中か（UI の二重操作防止）。</summary>
    public bool IsCommandRunning => _isCommandRunning;

    /// <summary>
    /// 実際の撮影が始まり EventCapture も開始できたか。false かつ Engine が Recording の間は
    /// UI 上「録画準備中」として扱う（Pause させると A/B の timeline が壊れるため）。
    /// </summary>
    public bool IsCaptureReady => _captureReady;

    /// <summary>EventCapture 側の失敗が起きているか。</summary>
    public bool HasEventCaptureFault => _eventCaptureFaulted;

    /// <summary>EventCapture 失敗時のユーザー向け message。</summary>
    public string? EventCaptureFaultMessage => _eventCaptureFaultMessage;

    /// <summary>
    /// 録画 session が進行中か。Navigation lock（§26）と Window close 拒否（§27）の判定に使う。
    ///
    /// <para>
    /// <see cref="_isStopFinalizing"/> を含めるのは、Engine が Idle へ遷移した後も
    /// EventCapture の停止と保存が background で続くため。Engine の State だけを見ると
    /// その窓で lock が外れてしまう。
    /// </para>
    /// </summary>
    public bool IsSessionActive =>
        _state is RecordingState.Recording or RecordingState.Paused or RecordingState.Stopping
        || _isStopFinalizing;

    /// <summary>session 開始時に固定した Project ID（録画中以外は null）。</summary>
    public Guid? SessionProjectId => _sessionProjectId;

    // ---------------------------------------------------------------------
    // Device enumeration（Engine へそのまま委譲する）
    // ---------------------------------------------------------------------

    public IReadOnlyList<DisplayDevice> GetDisplays() => _engine.GetDisplays();

    public IReadOnlyList<AudioDevice> GetMicrophones() => _engine.GetMicrophones();

    public IReadOnlyList<AudioDevice> GetSystemAudioDevices() => _engine.GetSystemAudioDevices();

    // ---------------------------------------------------------------------
    // Engine events
    // ---------------------------------------------------------------------

    private void OnEngineStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        _state = e.State;

        if (e.State == RecordingState.Failed)
        {
            // Engine が失敗したら EventCapture の hook / thread を残さず、session metadata も
            // 成功 session として残さない。Engine の recovery semantics は新設しない
            // （既存方針: アプリ再起動を促す）。
            CleanupOperationSessionBestEffort("engine failed");
        }

        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 「実際の撮影開始」の通知。ここで初めて EventCapture を開始し、Canonical 0ms を
    /// MP4 の 0 秒に一致させる（契約 §5.1）。Engine の callback thread から呼ばれるため
    /// UI element には触らない。
    /// </summary>
    private void OnCaptureStarted(object? sender, EventArgs e)
    {
        // field を何度も読まず local snapshot で判定する。Start() の実行中に
        // Engine Failed の cleanup が同じ session を破棄する可能性があるため。
        // ここは所有権を取らない（Pause / Stop が同じ session を使う）ので Read のみ。
        var session = Volatile.Read(ref _operationSession);

        if (session is null || _captureReady || _eventCaptureFaulted)
        {
            return;
        }

        try
        {
            // current session の events 範囲を固定する。EventTimelineWriter は Start 時に既存末尾へ
            // newline を補うことがあるため、必ず session.Start() の前に byte length を取る。
            var eventsPath = Path.Combine(session.ProjectDirectory, ProjectStore.EventsFileName);
            Volatile.Write(
                ref _sessionEventsStartOffset,
                RecordingSessionEventsReader.SnapshotStartOffset(eventsPath));

            session.Start();

            // Start() の間に ownership が cleanup 側へ移っていたら ready に戻さない
            // （Engine Failed は recovery 不可なので成功 session として残さない）。
            if (!ReferenceEquals(Volatile.Read(ref _operationSession), session) || _state == RecordingState.Failed)
            {
                Trace.TraceWarning(
                    "RecordingCoordinator: CaptureStarted の処理中に session の所有権が失われたため ready にしません。");
                return;
            }

            _captureReady = true; // 正常終了した後にのみ true
        }
        catch (Exception ex)
        {
            // events offset の取得または session.Start() の失敗。current session の Step build boundary が
            // 保証できないため integrated recording として続行しない（events.jsonl は truncate しない）。
            Trace.TraceError("RecordingCoordinator: 操作記録の開始に失敗しました — {0}", ex);
            MarkEventCaptureFaulted();
        }

        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------------------------------------------------------------
    // Start
    // ---------------------------------------------------------------------

    /// <summary>
    /// 録画を開始する。出力先と Project directory は ProjectStore が解決し、選択 device は
    /// Engine の返した instance をそのまま渡す（FriendlyName からの再構築はしない）。
    ///
    /// <para>
    /// <see cref="OperationCaptureSession.Start"/> はここでは呼ばない。StartAsync は実際の撮影開始
    /// より前に戻るため、<see cref="IRecordingEngine.CaptureStarted"/> を待って開始する。
    /// </para>
    /// </summary>
    public async Task<RecordingCommandResult> StartAsync(
        DisplayDevice? display,
        AudioDevice? systemAudioDevice,
        AudioDevice? microphoneDevice)
    {
        if (IsSessionActive)
        {
            return RecordingCommandResult.Failure("録画は既に開始されています。");
        }

        var project = _currentProject.CurrentProject;
        if (project is null)
        {
            return RecordingCommandResult.Failure("録画を開始するには、先にプロジェクトを作成または開いてください。");
        }

        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            var options = new RecordingOptions
            {
                OutputFilePath = _projectStore.GetRecordingOutputPath(project.Id),

                // MP4 の canonical 置換を Engine の StopAsync 内で行わせず、finalization transaction の
                // commit 点に委ねる（StepBuilder / project.json 保存と同じ logical transaction にする）。
                DeferredCommit = true,

                Display = display,
                SystemAudioDevice = systemAudioDevice,
                MicrophoneDevice = microphoneDevice,
            };

            _sessionProjectId = project.Id;
            _sessionOptions = options;
            _sessionBaseRevision = project.Revision;
            Volatile.Write(ref _sessionEventsStartOffset, RecordingSessionEventsReader.NoOffset); // CaptureStarted で確定する
            _captureReady = false;
            _eventCaptureFaulted = false;
            _eventCaptureFaultMessage = null;

            // Session は StartAsync より先に用意して publish する（CaptureStarted は StartAsync の後に届く）。
            // Volatile.Write で公開し、callback thread からの可視性を明確にする。
            Volatile.Write(
                ref _operationSession,
                new OperationCaptureSession(_projectStore.GetProjectDirectory(project.Id)));

            await _engine.StartAsync(options).ConfigureAwait(true);
            return RecordingCommandResult.Success();
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingCoordinator: 録画開始に失敗しました — {0}", ex);

            // 未 Start の session は hook / thread を持たないが、Dispose 責任はここで回収する
            // （ResetSessionState は session resource を黙って捨てない）。
            DisposeSessionQuietly(TakeOperationSession(), "start failed");
            ResetSessionState();
            return RecordingCommandResult.Failure("録画を開始できませんでした。");
        }
        finally
        {
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------------
    // Pause / Resume
    // ---------------------------------------------------------------------

    /// <summary>
    /// 一時停止。順序は実機 PASS 済みの integration-smoke に合わせて Engine → EventCapture。
    /// 撮影開始前（準備中）と EventCapture 故障中は許可しない。
    /// </summary>
    public async Task<RecordingCommandResult> PauseAsync()
    {
        if (!_captureReady || _eventCaptureFaulted)
        {
            return RecordingCommandResult.Failure(NotReadyMessage);
        }

        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await _engine.PauseAsync().ConfigureAwait(true);

            try
            {
                _operationSession?.Pause();
                return RecordingCommandResult.Success();
            }
            catch (Exception ex)
            {
                Trace.TraceError("RecordingCoordinator: 操作記録の一時停止に失敗しました — {0}", ex);
                MarkEventCaptureFaulted();
                return RecordingCommandResult.Failure(EventCaptureFaultedUserMessage);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingCoordinator: 一時停止に失敗しました — {0}", ex);
            return RecordingCommandResult.Failure("一時停止できませんでした。");
        }
        finally
        {
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>再開。順序は Engine → EventCapture（integration-smoke と同じ）。</summary>
    public async Task<RecordingCommandResult> ResumeAsync()
    {
        if (!_captureReady || _eventCaptureFaulted)
        {
            return RecordingCommandResult.Failure(NotReadyMessage);
        }

        _isCommandRunning = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            await _engine.ResumeAsync().ConfigureAwait(true);

            try
            {
                _operationSession?.Resume();
                return RecordingCommandResult.Success();
            }
            catch (Exception ex)
            {
                Trace.TraceError("RecordingCoordinator: 操作記録の再開に失敗しました — {0}", ex);
                MarkEventCaptureFaulted();
                return RecordingCommandResult.Failure(EventCaptureFaultedUserMessage);
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingCoordinator: 再開に失敗しました — {0}", ex);
            return RecordingCommandResult.Failure("再開できませんでした。");
        }
        finally
        {
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---------------------------------------------------------------------
    // Stop → RecordingInfo → project.json
    // ---------------------------------------------------------------------

    /// <summary>
    /// 録画を停止する。順序: Engine 停止（DeferredCommit = staging のまま）→ EventCapture 停止 → Dispose →
    /// 論理時間の比較 → <see cref="RecordingFinalizationPipeline"/> による finalization
    /// （current session events の抽出 → StepBuilder → candidate → validation → transaction →
    /// CurrentProject swap）。
    ///
    /// <para>
    /// Engine の MP4 確定と project.json 保存は finalization transaction の 1 logical transaction として
    /// 行われる（片方だけが確定した状態を作らない）。保存は session 固定の Project ID で project.json を
    /// 読み直してから行う（UI が保持している古い object をそのまま書き戻さない）。
    /// </para>
    /// <para>
    /// EventCapture が失敗した recording は integrated recording として確定しない
    /// （RecordingInfo を保存せず、Revision も進めない）。確定待ち録画（staging）は Abort するが、
    /// canonical と events.jsonl / screenshot は削除しない。
    /// </para>
    /// </summary>
    public async Task<RecordingStopOutcome> StopAsync()
    {
        // finalization 中は IsSessionActive が true のままなので、Stop の多重実行はここで弾く
        // （2 本目の Stop が Engine と session を二重に触らないようにする）。
        if (_isStopFinalizing || !IsSessionActive)
        {
            return RecordingStopOutcome.Failure(RecordingStopStatus.NotRecording, "録画中ではありません。");
        }

        _isCommandRunning = true;

        // Engine が Idle を通知しても finalization が終わるまで session lock を維持する。
        // MainWindow の navigation lock / close 拒否は IsSessionActive を見ているため、
        // 両 flag を立ててから ActivityChanged を通知する。
        _isStopFinalizing = true;
        ActivityChanged?.Invoke(this, EventArgs.Empty);

        try
        {
            RecordingResult result;
            try
            {
                result = await _engine.StopAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // Engine 側の失敗（Failed state / 予期しない例外）。独自 recovery は行わない。
                Trace.TraceError("RecordingCoordinator: 録画停止に失敗しました — {0}", ex);
                CleanupOperationSessionBestEffort("engine stop failed");

                return RecordingStopOutcome.Failure(
                    RecordingStopStatus.EngineFailed,
                    "録画エンジンでエラーが発生しました。アプリを再起動して再試行してください。");
            }

            // EventCapture の停止と Dispose は hook thread / worker thread の Join と queue drain を
            // 含み、明示的な Join だけで最大 20 秒規模になりうる。UI thread を block しないよう
            // background へ移す。fire-and-forget にせず必ず await する。
            // ownership の取得と Coordinator state の更新は UI thread 側に閉じる
            // （background から field を直接触らない）。
            var session = TakeOperationSession();
            var captureReady = _captureReady;
            var alreadyFaulted = _eventCaptureFaulted;

            var stopResult = await Task
                .Run(() => StopEventCaptureCore(session, captureReady, alreadyFaulted))
                .ConfigureAwait(true);

            // background が返した immutable な結果を、UI thread 側で公開する。
            if (stopResult.Faulted)
            {
                MarkEventCaptureFaulted(stopResult.FaultMessage);
            }

            var eventDurationMs = stopResult.DurationMs;

            // Engine と EventCapture の論理時間を比較する（統合の成立確認）。
            // 差が大きくても recording は捨てない（契約違反として即破棄はしない）。
            if (eventDurationMs is { } eventMs)
            {
                var engineMs = result.Duration.TotalMilliseconds;
                var differenceMs = Math.Abs(engineMs - eventMs);

                if (differenceMs > DurationToleranceMs)
                {
                    Trace.TraceWarning(
                        "RecordingCoordinator: Engine と EventCapture の論理時間差が {0:F0} ms です（Engine={1:F0} ms / Event={2} ms）。",
                        differenceMs,
                        engineMs,
                        eventMs);
                }
                else
                {
                    Trace.TraceInformation(
                        "RecordingCoordinator: Engine と EventCapture の論理時間差は {0:F0} ms です。",
                        differenceMs);
                }
            }

            var projectId = _sessionProjectId;
            var options = _sessionOptions;

            if (projectId is null || options is null)
            {
                // session 情報が失われている = finalization の入力が揃わない。確定待ち録画が残っていれば
                // 解消しておく（放置すると次回 StartAsync が拒否される）。
                Trace.TraceError("RecordingCoordinator: session 情報が失われているため保存できません。");
                _finalizationPipeline.AbortPendingBestEffort(result, "session metadata lost");
                return RecordingStopOutcome.Failure(
                    RecordingStopStatus.SaveFailed,
                    SaveFailedMessage,
                    result.FilePath);
            }

            // EventCapture fault の判定 / Revision gate / current session events の抽出 / StepBuilder /
            // candidate 構築 / validation / finalization transaction は pipeline が持つ（context-free）。
            var finalization = await _finalizationPipeline
                .FinalizeAsync(new RecordingFinalizationInput(
                    projectId.Value,
                    options,
                    result,
                    Volatile.Read(ref _sessionEventsStartOffset),
                    _sessionBaseRevision,
                    _eventCaptureFaulted,
                    _eventCaptureFaultMessage))
                .ConfigureAwait(true); // publish を UI thread で行うため、ここで UI context へ戻す

            // CurrentProject への publish は UI-affine なこの層で行う。pipeline 側（thread pool の
            // 可能性がある継続）から SetCurrent を呼ぶと cross-thread 例外でプロセスが落ちる。
            PublishFinalizedProject(_currentProject, projectId.Value, finalization);

            return finalization.Outcome;
        }
        finally
        {
            ResetSessionState();
            _isStopFinalizing = false;
            _isCommandRunning = false;
            ActivityChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// <see cref="StopEventCaptureCore"/> の結果。background thread から UI thread へ渡すため
    /// immutable にする（Coordinator の field は含めない）。
    /// </summary>
    /// <param name="DurationMs">EventCapture 側の論理 Duration。取得できなければ null。</param>
    /// <param name="Faulted">EventCapture fault として記録すべきか。</param>
    /// <param name="FaultMessage">fault のユーザー向け message。null なら既定 message を使う。</param>
    private sealed record EventCaptureStopResult(long? DurationMs, bool Faulted, string? FaultMessage);

    /// <summary>
    /// EventCapture session を停止して Dispose する。Dispose まで行い hook / thread を残さない。
    ///
    /// <para>
    /// hook thread / worker thread の Join と queue drain を含むため UI thread では実行しない。
    /// 呼出元（<see cref="StopAsync"/>）が Task.Run で background へ渡す。
    /// </para>
    /// <para>
    /// Coordinator の field は一切触らない。session の所有権取得（<see cref="TakeOperationSession"/>）は
    /// 呼出元の UI thread で済ませ、ここへは引数として渡す。失敗しても例外は投げず、
    /// fault として結果に載せて返す。
    /// </para>
    /// </summary>
    private static EventCaptureStopResult StopEventCaptureCore(
        OperationCaptureSession? session,
        bool captureReady,
        bool alreadyFaulted)
    {
        if (session is null)
        {
            // integrated recording では EventCapture session の不在 = 正常な recording ではない。
            // 内部不整合で session が消えていても成功扱いで保存しないよう fault として記録する。
            Trace.TraceError(
                "RecordingCoordinator: EventCapture session が存在しないため integrated recording として扱いません。");
            return new EventCaptureStopResult(null, true, null);
        }

        long? durationMs = null;
        var faulted = false;

        if (captureReady)
        {
            try
            {
                durationMs = session.Stop();
            }
            catch (Exception ex)
            {
                Trace.TraceError("RecordingCoordinator: 操作記録の停止に失敗しました — {0}", ex);
                faulted = true;
            }
        }
        else if (!alreadyFaulted)
        {
            // CaptureStarted が来ないまま停止された = 操作記録が 1 件も取れていない。
            Trace.TraceWarning("RecordingCoordinator: 操作記録が開始されないまま録画が停止されました。");
            faulted = true;
        }

        // 停止に失敗しても Dispose は必ず行う（hook / thread を残さない）。
        DisposeSessionQuietly(session, "stop");
        return new EventCaptureStopResult(durationMs, faulted, null);
    }

    /// <summary>
    /// session の所有権を原子的に取り出す（Dispose 責任を取得する）。
    /// 同一 instance を複数経路が Dispose owner として取得しないための唯一の取り出し口。
    /// </summary>
    private OperationCaptureSession? TakeOperationSession() =>
        Interlocked.Exchange(ref _operationSession, null);

    /// <summary>後始末の例外で呼出元の処理を壊さない（cleanup は best-effort）。</summary>
    private static void DisposeSessionQuietly(OperationCaptureSession? session, string reason)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            session.Dispose();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "RecordingCoordinator: 操作記録の後始末に失敗しました（{0}） — {1}",
                reason,
                ex);
        }
    }

    /// <summary>
    /// Engine が Failed になった / 停止に失敗したときに、active session の metadata を破棄し、
    /// EventCapture の hook と thread を残さない。
    ///
    /// <para>
    /// Engine Failed は recovery 不可（既存方針: アプリ再起動を促す）なので、session metadata も
    /// 「成功 session」として残さない。metadata は同期的にすべて落とし、
    /// <see cref="OperationCaptureSession.Dispose"/>（内部で最大 20 秒待つ Stop）だけを
    /// Engine の callback thread を塞がないよう別 thread で実行する。
    /// 後始末の例外で App を落とさない。
    /// </para>
    /// </summary>
    private void CleanupOperationSessionBestEffort(string reason)
    {
        // Dispose 責任をこの経路が取得する（atomic なので他経路と二重取得しない）。
        var session = TakeOperationSession();

        // metadata は同期で完全に落とす（UI / 後続 command から見て session が残らないように）。
        ResetSessionState();

        if (session is null)
        {
            return;
        }

        Task.Run(() => DisposeSessionQuietly(session, reason));
    }

    /// <summary>
    /// EventCapture fault を記録する。既に fault が成立している場合は message を上書きしない
    /// （最初に検出した原因をユーザーへ見せる）。
    /// </summary>
    /// <param name="message">fault のユーザー向け message。null なら既定 message を使う。</param>
    private void MarkEventCaptureFaulted(string? message = null)
    {
        _eventCaptureFaulted = true;
        _eventCaptureFaultMessage ??= message ?? EventCaptureFaultedUserMessage;
    }

    /// <summary>
    /// session の metadata をクリアする（冪等・多重呼出前提）。
    ///
    /// <para>
    /// <see cref="OperationCaptureSession"/> の resource はここでは触らない。
    /// 所有権は <see cref="TakeOperationSession"/> で取得した側（Start 失敗 / Stop / Engine Failed の
    /// 各経路）が Dispose する。resource を黙って捨てないことが、この分離の目的。
    /// </para>
    /// </summary>
    private void ResetSessionState()
    {
        _sessionProjectId = null;
        _sessionOptions = null;
        _sessionBaseRevision = 0;
        Volatile.Write(ref _sessionEventsStartOffset, RecordingSessionEventsReader.NoOffset);
        _captureReady = false;
        _eventCaptureFaulted = false;
        _eventCaptureFaultMessage = null;
    }

    // ---------------------------------------------------------------------
    // Mapping（純関数 — UI / IO に依存しない）
    // ---------------------------------------------------------------------

    /// <summary>
    /// finalization の結果を CurrentProject へ publish する（identity guard 付き）。
    ///
    /// <para>
    /// <b>必ず UI thread から呼ぶこと</b>: <see cref="CurrentProjectContext.SetCurrent"/> は
    /// CurrentProjectChanged を同期発火し、購読している View（HomeView / ReviewView 等）が
    /// WPF オブジェクトへ触る。非 UI thread から呼ぶと cross-thread InvalidOperationException で
    /// プロセスが落ちる（2026-09-25 の runtime smoke で検出した regression の再発防止。
    /// <see cref="RecordingFinalizationPipeline"/> は意図的に context-free にしてあるため、
    /// publish はこの UI-affine な層が唯一の境界になる）。
    /// </para>
    /// <para>
    /// 成功（<see cref="RecordingStopStatus.Saved"/> かつ committed Project あり）で、かつ録画対象が
    /// current のときだけ差し替える。別 Project が current なら巻き戻さない（既存 identity guard）。
    /// </para>
    /// </summary>
    public static void PublishFinalizedProject(
        CurrentProjectContext currentProject,
        Guid projectId,
        RecordingFinalizationPipelineResult finalization)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(finalization);

        if (finalization.Status != RecordingStopStatus.Saved || finalization.CommittedProject is null)
        {
            return;
        }

        if (!currentProject.IsCurrent(projectId))
        {
            return;
        }

        currentProject.SetCurrent(finalization.CommittedProject);
    }

    /// <summary>
    /// <see cref="RecordingResult"/> と録画時に渡した <see cref="RecordingOptions"/> から
    /// 契約 §7 の <see cref="RecordingInfo"/> を作る。
    ///
    /// <para>
    /// MediaPath は ProjectStore が定義する canonical な相対 path を使う（絶対パスは保存禁止）。
    /// HasSystemAudio / HasMicrophone と device metadata は Result に含まれないため、
    /// 「何を録音するよう Engine へ要求したか」= RecordingOptions から導出する。
    /// </para>
    /// </summary>
    public static RecordingInfo MapToRecordingInfo(RecordingResult result, RecordingOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(options);

        return new RecordingInfo
        {
            MediaPath = ProjectStore.RecordingMediaPath,
            StartedAtUtc = result.StartedAtUtc,
            DurationMs = (long)result.Duration.TotalMilliseconds,
            HasSystemAudio = options.SystemAudioDevice is not null,
            HasMicrophone = options.MicrophoneDevice is not null,
            DisplayId = options.Display?.DeviceId,
            DisplayName = options.Display?.DeviceName,
            SystemAudioDeviceId = options.SystemAudioDevice?.DeviceId,
            SystemAudioDeviceName = options.SystemAudioDevice?.DeviceName,
            MicrophoneDeviceId = options.MicrophoneDevice?.DeviceId,
            MicrophoneDeviceName = options.MicrophoneDevice?.DeviceName,
        };
    }
}

/// <summary>Start / Pause / Resume の結果。失敗しても例外は投げず、UI が message を表示できる形にする。</summary>
public sealed record RecordingCommandResult(bool Succeeded, string? ErrorMessage)
{
    public static RecordingCommandResult Success() => new(true, null);

    public static RecordingCommandResult Failure(string message) => new(false, message);
}

/// <summary>Stop の結果分類。</summary>
public enum RecordingStopStatus
{
    /// <summary>MP4 確定 + project.json 保存 + Current Project 更新まで完了。</summary>
    Saved,

    /// <summary>MP4 は確定したが project.json の保存に失敗（MP4 は削除しない）。</summary>
    SaveFailed,

    /// <summary>Engine 側で失敗（Failed state / 例外）。</summary>
    EngineFailed,

    /// <summary>
    /// EventCapture 側が失敗したため integrated recording として保存しなかった。
    /// 確定待ち録画（staging）は Abort し、canonical は変更しない。events.jsonl / screenshot は残す。
    /// </summary>
    EventCaptureFailed,

    /// <summary>
    /// current session の操作記録から Step を生成できなかった（events 読み出し失敗 / StepBuilder error）。
    /// </summary>
    StepBuildFailed,

    /// <summary>録画中に Project が更新されたため、古い base からの確定を行わなかった。</summary>
    SourceChanged,

    /// <summary>
    /// 確定処理に失敗した（candidate validation / transaction）。canonical と project.json は
    /// transaction 前の状態へ戻っている（またはそもそも変更されていない）。
    /// </summary>
    FinalizationFailed,

    /// <summary>
    /// 確定に失敗し、rollback も失敗したため復旧用 backup を保持している。
    /// ユーザーへは generic な message のみを出し、filesystem path は表示しない。
    /// </summary>
    RecoveryRequired,

    /// <summary>そもそも録画中ではなかった。</summary>
    NotRecording,
}

/// <summary>Stop の結果。UI はこの Status で表示 message を切り替える。</summary>
public sealed record RecordingStopOutcome(RecordingStopStatus Status, string? ErrorMessage, string? AbsoluteFilePath)
{
    public static RecordingStopOutcome Success(string absoluteFilePath) =>
        new(RecordingStopStatus.Saved, null, absoluteFilePath);

    public static RecordingStopOutcome Failure(RecordingStopStatus status, string message, string? absoluteFilePath = null) =>
        new(status, message, absoluteFilePath);
}
