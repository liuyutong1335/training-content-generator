using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using TrainingContent.EventCapture.Hooks;
using TrainingContent.EventCapture.Screenshot;
using TrainingContent.EventCapture.UiAutomation;
using TrainingContent.EventCapture.WindowInfo;

namespace TrainingContent.EventCapture;

/// <summary>
/// Operation Capture の制御オプション。
/// </summary>
public sealed class OperationCaptureOptions
{
    /// <summary>
    /// この述部が true を返すウィンドウ（自プロセスの Record UI など）での
    /// ユーザー操作を記録から除外する。未指定なら自プロセスの PID で判定する。
    /// 注意: コンソールアプリを Windows Terminal 上で動かすと、ウィンドウの PID が
    /// Terminal 側になるため自プロセス判定は機能しない（WPF の Record UI なら機能する）。
    /// </summary>
    public Func<WindowInfo.WindowInfo, bool>? WindowFilter { get; init; }
}

/// <summary>
/// 1 回の録画セッションでユーザー操作を取得し、Phase 0 契約 v1.0 準拠の
/// TimelineEvent（events.jsonl）を TrainingProject ディレクトリへ書き出す（契約 §17 / §20）。
/// 担当 D（WPF / Integration）はこのクラス経由で録画を制御する。
///
/// スレッド構成:
///   フックスレッド  : GlobalMouse/KeyboardHook を設置し Application.Run() でメッセージループを維持
///                     （LL Hook はメッセージループがないとコールバックが呼ばれない）
///   ワーカースレッド: UIA・スクリーンショット等の重い処理（フックスレッドをブロックしない）
/// </summary>
public sealed class OperationCaptureSession : IDisposable
{
    private readonly OperationCaptureOptions _options;
    private readonly MasterClock _clock = new();
    private readonly BlockingCollection<RawItem> _queue = new();
    private readonly object _stateSync = new();
    private readonly object _textSync = new();
    private readonly TextEntryAggregator _textAggregator = new();
    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    private EventTimelineWriter? _writer;
    private UiAutomationService? _uiAutomation;
    private GlobalMouseHook? _mouseHook;
    private GlobalKeyboardHook? _keyboardHook;
    private Thread? _hookThread;
    private Thread? _worker;
    private Exception? _hookInitError;

    /// <summary>書き出し前の Event の保留バッファ（worker スレッド専用）。
    /// ダブルクリック判定待ちの保留左クリックより物理時刻が後の Event を先に書くと
    /// seq（発生順）が物理発生順にならないため（契約 §5.3）、保留クリックの確定を
    /// 挟み込んでから物理時刻順に書き出す。</summary>
    private readonly List<RawItem> _reorderBuffer = new();

    /// <summary>Pause 時点の Canonical 時刻（境界フィルタ用）。未設定は -1。
    /// アプリスレッドが書き / ワーカーが読むため Volatile でアクセスする
    /// （long? のままだと非アトミック読み書きになる。MasterClock の監査指摘と同型）。</summary>
    private long _pauseBoundaryMs = -1;
    private (UiElementInfo? Element, string? ProcessName, string? WindowTitle) _textTarget;
    private string? _lastTextWindowKey;
    private bool _started;

    /// <summary>Start の初期化（フック設置 + recording.started 書き出し）が完了したか
    /// （監査 MIN-6: 実行中も true になる IsRecording を補正する）。</summary>
    private volatile bool _startCompleted;
    private bool _stopped;
    private bool _startFailed;

    /// <summary>Start 実行中の Dispose が完了を待つためのシグナル（監査 RC-6:
    /// 初期化中に Dispose するとフック設置との競合で started / stopped 逆順の
    /// ファイルやフック残留を残し得た）。</summary>
    private readonly ManualResetEventSlim _startDone = new();

    /// <summary>Stop の最終 flush 後に worker 側のユーザー Event 追加を禁止するフラグ
    /// （recording.stopped より後への mouse.* / keyboard.* 書き込みを防ぐ）。
    /// textEntry に限らず特殊キー / ショートカット / マウスも対象（監査指摘:
    /// worker の Join(15000) タイムアウト後も finalization が進むため、stopped
    /// より後への append をフラグで構造的に阻否する）。_textSync で守る。</summary>
    private bool _userEventsFinal;

    /// <summary>テスト用: Stop 時の worker Join 待ち時間（タイムアウト後の
    /// finalization 継続を短時間で再現するため）。実運用は 15 秒。</summary>
    internal long WorkerJoinTimeoutForTest { get; set; } = 15000;

    /// <summary>キー受領から処理までの遅延がこれを超えたら Password 判定を信頼せず
    /// sensitive に倒す（fail-closed。処理時点のフォーカスは入力時点と異なりうる）。</summary>
    internal static long PasswordJudgementLagLimitMs { get; } = 500;

    public string ProjectDirectory { get; }

    /// <summary>
    /// 録画処理中か（Start が正常に完了して以降〜Stop まで）。
    /// Capture preparation 中の Cancel 判定など、呼び出し側の分岐用:
    /// false なら Stop は呼ばず Dispose する（Stop は InvalidOperationException を投げる）。
    /// Start 失敗セッション・停止済みセッションでは false。
    /// Start 実行中（初期化の間）も false（監査 MIN-6。書き込み直後の一瞬の遅れはあり得る）。
    /// </summary>
    public bool IsRecording => _startCompleted && !_startFailed && !_stopped;

    /// <summary>書き出した Event の総数（recording.* ライフサイクルを含む）。</summary>
    public long EventCount => _writer?.Count ?? 0;

    public OperationCaptureSession(string projectDirectory, OperationCaptureOptions? options = null)
    {
        ProjectDirectory = projectDirectory;
        _options = options ?? new OperationCaptureOptions();
    }

    /// <summary>録画を開始する（フック設置 + recording.started の書き出し）。</summary>
    /// <remarks>
    /// Start が途中で失敗した場合（例: events.jsonl が他プロセスに掴まれていて
    /// recording.started の書き出しに失敗）でも、フック / ワーカースレッドを残留させない
    /// （D5-B 統合テスト指摘 §1）。失敗したセッションは再利用できないため、
    /// 呼び出し側は新しい OperationCaptureSession を作り直すこと。
    /// </remarks>
    public void Start()
    {
        lock (_stateSync)
        {
            if (_started)
            {
                throw _startFailed
                    ? new InvalidOperationException(
                        "開始に失敗したセッションは再利用できません。新しい OperationCaptureSession を作成してください。")
                    : new InvalidOperationException("このセッションは既に開始されています。");
            }

            _started = true;
        }

        try
        {
            Directory.CreateDirectory(ProjectDirectory);
            _writer = new EventTimelineWriter(Path.Combine(ProjectDirectory, "events.jsonl"));
            _uiAutomation = new UiAutomationService();

            if (InstallHooksForTest)
            {
                _mouseHook = new GlobalMouseHook();
                _keyboardHook = new GlobalKeyboardHook();
                _mouseHook.ClickCaptured += OnMouseClick;
                _keyboardHook.KeyboardInputCaptured += OnKeyboardInput;

                var hookThreadReady = new ManualResetEventSlim();
                _hookThread = new Thread(HookThreadProc) { IsBackground = true };
                _hookThread.SetApartmentState(ApartmentState.STA);
                _hookThread.Start(hookThreadReady);
                hookThreadReady.Wait();

                if (_hookInitError is not null)
                {
                    throw new InvalidOperationException("フックの初期化に失敗しました。", _hookInitError);
                }
            }

            _worker = new Thread(ProcessQueue) { IsBackground = true };
            _worker.Start();

            _clock.Start();
            _writer.Append("recording.started", _clock.NowMs(), new { });

            // recording.started の書き出しまで成功して初めて「録画処理中」になる
            // （IsRecording の監査 MIN-6 補正。volatile で他スレッドからも可視化）。
            _startCompleted = true;
        }
        catch
        {
            // 部分的にでも開始してしまっていたら best-effort で後片付けする
            // （フック解除 / フックスレッド終了 / ワーカー終了）。
            _startFailed = true;
            try
            {
                TeardownHooks();
                _queue.CompleteAdding();
                _worker?.Join(15000);
            }
            catch
            {
                // 後片付けの失敗は元の例外を壊さないよう握り潰す。
            }

            // 失敗したセッションから recording.stopped 等を書かせない
            // （Dispose → Stop 経路でライフサイクルイベントを追加書き込みしない）。
            _writer = null;
            throw;
        }
        finally
        {
            // Start 実行中に Dispose されたスレッドを解放する（監査 RC-6）。
            _startDone.Set();
        }
    }

    /// <summary>録画を一時停止する。Pause 中の実時間は Canonical Timeline に含まれない（契約 §5.2）。</summary>
    public void Pause()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();

            // boundary の設定と recording.paused の書き出しを worker のユーザー Event
            // 書き出し（AppendUserEvent / FlushTextBuffer）と同じロックで直列化する。
            // これにより paused 行の後へ「物理的には Pause より前」のユーザー Event が
            // 割り込めない（処理中だった Event は Append 直前の境界再判定で破棄される）。
            lock (_textSync)
            {
                FlushTextBuffer();
                _clock.Pause();
                Volatile.Write(ref _pauseBoundaryMs, _clock.NowMs());
                _writer!.Append("recording.paused", _clock.NowMs(), new { });
            }
        }
    }

    /// <summary>録画を再開する。</summary>
    public void Resume()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();

            // recording.resumed も paused と同じく _textSync 内で書く
            // （Resume 呼び出しより物理時刻が後のユーザー Event が resumed 行より
            //   先に書かれる入り込みを防ぐ。boundary は Resume 後も減算値として使う
            //   ので、ここではリセットしない）。
            lock (_textSync)
            {
                FlushTextBuffer();
                _clock.Resume();
                _writer!.Append("recording.resumed", _clock.NowMs(), new { });
            }
        }
    }

    /// <summary>
    /// Canonical Timeline の原点を現在時刻に張り直す（MasterClock.RebaseOriginToNow 参照）。
    /// 用途: 担当 A の IRecordingEngine が「実際の撮影開始」を通知してきた瞬間に呼び、
    /// Event 側の 0ms を MP4 の 0 秒に一致させる（docs/integration-notes.md §1 / duty-a-progress §3-8）。
    /// 録画開始直後（Pause が起きる前）に呼ぶことを想定。
    /// </summary>
    public void RebaseClockToNow()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();
            FlushTextBuffer();
            _clock.RebaseOriginToNow();
        }
    }

    /// <summary>録画を停止し、論理 DurationMs を返す。</summary>
    /// <remarks>
    /// 停止手順（D5-B 統合テスト指摘 §2 の修正）:
    ///   1. フックを先に止める（stop 以降のユーザー入力を取り込まない）
    ///   2. 終端時刻 durationMs を確定させる
    ///   3. queue の入力を閉じ、stop 時点で受領済みのイベントをワーカーが書き切るのを待つ
    ///   4. 残った textEntry バーストを締め切る
    ///   5. 最後に recording.stopped を書く（必ず最終行になる）
    /// どこかの書き込みが失敗しても（events.jsonl 掴まれ等）リソース解放は finally で
    /// 必ず実行する。後片付けを書き込み成否に依存させない（同指摘 §1）。
    /// 書き込み系の例外は握り潰さず呼び出し元へ伝播させる: recording.stopped を書けなかった
    /// recording は終端イベントを欠くため、integrated recording として確定させるべきではない
    /// （Coordinator 側は Stop 失敗を fault として扱う）。
    /// </remarks>
    public long Stop()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();
            _stopped = true;

            try
            {
                TeardownHooks();

                // 終端時刻を先に確定させる（以降の drain 遅延や書き込み失敗に依存しない）。
                var durationMs = _clock.NowMs();

                _queue.CompleteAdding();
                _worker?.Join((int)WorkerJoinTimeoutForTest); // 終端イベントの取りこぼし防止（UIA・撮影は 1 Event 百ms 級）
                // Join がタイムアウトしても finalization は進める（指摘 §2）。
                // タイムアウトした場合、worker は後続のユーザー Event をまだ処理しうるが、
                // FlushTextBuffer(final:) が設定する _userEventsFinal フラグ以降の
                // worker 側 append は全て破棄されるため、stopped が最終行であることが保たれる。

                // textEntry バーストはワーカーが queue を処理し終わった後に締め切る。
                // drain 前に flush すると queue 残存イベントより古い textEntry が先に
                // 書かれ、ファイル行順の timestampMs 非減少が崩れるため。
                // （ワーカーの各処理パスは書き込み直前に flush 済みなので、ここで残るのは
                //   最後の Text キーで始まった未締め切りバーストのみ）
                // final: 以降の worker 側ユーザー Event 追加を禁止する（stopped が最終行であることの保証）。
                FlushTextBuffer(final: true);

                _writer?.Append("recording.stopped", durationMs, new { });

                return durationMs;
            }
            finally
            {
                // 例外が出てもフック解除だけは保証する（書き込み失敗でフック / スレッドを残留させない）。
                TeardownHooks();
            }
        }
    }

    public void Dispose()
    {
        lock (_stateSync)
        {
            try
            {
                // Start 実行中に呼ばれた場合は初期化の完了を待つ（監査 RC-6）。
                // 待たずに後片付けると、並行する Start のフック設置と競合して
                // フック残留 / started・stopped 逆順のファイルを残し得る。
                // （同一スレッドからは呼べない呼び出し順なのでデッドロックにはならない。
                //   Start がハングした場合もタイムアウトで諦めて finally の後片付けへ）
                if (_started && !_startDone.IsSet)
                {
                    _startDone.Wait(15000);
                }

                if (_startCompleted && !_startFailed && !_stopped)
                {
                    Stop();
                }
            }
            catch
            {
                // Dispose では停止失敗を握り潰す。
            }
            finally
            {
                // Stop が何らかの理由で完了しなかった場合でも、スレッド / フックの
                // 残留だけは防ぐ（TeardownHooks / CompleteAdding は冪等）。
                try { TeardownHooks(); } catch { }

                try { _queue.CompleteAdding(); } catch { }

                try { _worker?.Join(1000); } catch { }
            }
        }
    }

    private void HookThreadProc(object? obj)
    {
        var ready = (ManualResetEventSlim)obj!;
        try
        {
            _mouseHook!.Start();
            _keyboardHook!.Start();
            ready.Set();
            System.Windows.Forms.Application.Run(); // メッセージループでフックを維持する
        }
        catch (Exception ex)
        {
            _hookInitError = ex;
            ready.Set();
        }
        finally
        {
            // 初期化が途中で失敗した場合（例: mouse だけ設置成功して keyboard で失敗）も、
            // 設置済みのグローバル フックを必ず解除する。
            try { _keyboardHook?.Stop(); } catch { }

            try { _mouseHook?.Stop(); } catch { }
        }
    }

    /// <summary>フックを解除し、フックスレッドのメッセージループを終了させる（冪等）。</summary>
    private void TeardownHooks()
    {
        if (_hookThread is { IsAlive: true })
        {
            System.Windows.Forms.Application.Exit(); // フックスレッドのメッセージループを抜ける
            _hookThread.Join(5000);
        }

        // フックスレッドがスタックして Join がタイムアウトした場合の保険。
        // HookThreadProc の finally でも解除されるため、通常はここでは何もしない。
        try { _keyboardHook?.Stop(); } catch { }

        try { _mouseHook?.Stop(); } catch { }
    }

    private void ThrowIfNotRunning()
    {
        // _startCompleted も含める（監査 MIN-6 / RC-6: Start 初期化中の Stop は
        // フック設置と競合するため「録画中ではない」として拒否する。目安は IsRecording）。
        if (_startFailed || !_started || !_startCompleted || _stopped)
        {
            throw new InvalidOperationException("セッションは録画中ではありません。");
        }
    }

    private void Enqueue(RawItem item)
    {
        // IsAddingCompleted チェックと Add の間で Stop / Dispose 側の CompleteAdding が
        // 入ると InvalidOperationException（Dispose 後なら ObjectDisposedException）が
        // フックスレッドのコールバック内で飛ぶ。LL Hook コールバック内の未処理例外は
        // プロセス墜落になり得るため、「停止瞬間の入力破棄」として握り潰す。
        try
        {
            if (!_queue.IsAddingCompleted)
            {
                _queue.Add(item);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
        }
    }

    private void OnMouseClick(object? sender, ClickCapturedEventArgs e)
    {
        // 物理クリック時刻（QPC）をそのまま運ぶ。ダブルクリック判定待ちで
        // 保留解除されるまで最大 ~900ms 遅れるため、受領時刻で採番すると
        // Canonical Timeline に系統誤差が乗る（→ ProcessItem で Canonical 化）。
        Enqueue(new RawItem(e.CapturedQpcTimestamp, RawKind.Mouse, X: e.X, Y: e.Y, ClickType: e.ActionType));
    }

    private void OnKeyboardInput(object? sender, KeyboardInputEventArgs e)
    {
        Enqueue(new RawItem(Stopwatch.GetTimestamp(), RawKind.Key, KeyKind: e.Kind, KeyName: e.KeyName, ShortcutName: e.ShortcutName));
    }

    private void ProcessQueue()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            // ダブルクリック判定待ちの保留左クリックより物理時刻が後の Event を
            // 先に書くと、seq（発生順）と timestampMs（時間位置）が逆転する
            // （保留クリックは最大約 900ms 後に確定するため。契約 §5.3 /
            //   D の E+F-B production runtime smoke 指摘）。
            // 一度保留バッファへ貯め、保留クリックの確定を挟み込んでから
            // 物理時刻（QPC）順に書き出す。
            _reorderBuffer.Add(item);
            _reorderBuffer.Sort(static (a, b) => a.QpcTimestamp.CompareTo(b.QpcTimestamp));
            TryReleaseReorder();
        }

        // 入力の締め切り後は保留クリックの確定を待てないため、残りを全て書き切る
        // （recording.stopped より前に書かれるよう、Stop は Join してから
        //   FlushTextBuffer(final) / stopped 書き出しに進む）。
        TryReleaseReorder(final: true);
    }

    /// <summary>
    /// 保留バッファから書き出せる Event を物理時刻順に書き出す（worker スレッド専用）。
    /// 先頭（最古）の Event より物理時刻が早い保留左クリックがフック側に残っている間は、
    /// その確定を待つために書き出しを中断する。保留クリックは必ずタイマーもしくは
    /// 後続クリック / Stop 時の flush で確定し queue へ届くため、待ちが無期限になることはない。
    /// </summary>
    /// <param name="final">true なら保留クリックの確定を待たずに全て書き切る（queue 締め切り後）。</param>
    private void TryReleaseReorder(bool final = false)
    {
        while (_reorderBuffer.Count > 0)
        {
            var candidate = _reorderBuffer[0];
            var pendingQpc = final ? null : GetPendingLeftClickQpc();
            if (pendingQpc is { } qpc && qpc < candidate.QpcTimestamp)
            {
                // 保留クリックの方が物理的に先 → 確定を待つ（次の Event 到達時に再判定）。
                return;
            }

            _reorderBuffer.RemoveAt(0);
            try
            {
                ProcessItem(candidate);
            }
            catch
            {
                // 1 Event の処理失敗で録画全体を止めない（従来どおり）。
            }
        }
    }

    /// <summary>保留中の左クリックの物理クリック時刻（QPC）。保留がなければ null。</summary>
    private long? GetPendingLeftClickQpc()
    {
        return PendingLeftClickQpcSourceForTest is { } source
            ? source()
            : _mouseHook?.PendingLeftClickQpc;
    }

    private void ProcessItem(RawItem item)
    {
        // Raw Event の QPC タイムスタンプ（物理発生時刻）を Canonical ms に変換する。
        // Pause 中に発生した Event は区間開始時刻の凍結値に変換されるため、
        // 下の境界フィルタ（<=）で確実に弾かれる。
        var timestampMs = _clock.ToCanonicalMs(item.QpcTimestamp);

        // Pause 中のユーザー操作は Canonical Timeline の外側のため破棄する。
        // さらに Pause より前に発生していながら処理が追いつかなかった
        // 後追いイベント（例: P 押下自体のキーダウン）も破棄する。
        // 境界そのもの（==）は Pause 中に発生した Event の凍結 timestampMs なので破棄対象に含める。
        if (IsOutsideCanonicalTimeline(timestampMs))
        {
            return;
        }

        if (item.Kind == RawKind.Mouse)
        {
            ProcessMouse(item, timestampMs);
        }
        else if (item.Kind == RawKind.Key)
        {
            ProcessKey(item, timestampMs);
        }
    }

    private void ProcessMouse(RawItem item, long timestampMs)
    {
        FlushTextBuffer();

        var window = WindowInfoService.FromPoint(item.X, item.Y);
        if (IsFiltered(window))
        {
            return;
        }

        var uiInfo = _uiAutomation!.GetElementAt(item.X, item.Y);
        var screenshotPath = ScreenshotCapture.CaptureEventScreenshot(ProjectDirectory, item.X, item.Y);
        var eventType = item.ClickType switch
        {
            MouseClickKind.Click => "mouse.click",
            MouseClickKind.DoubleClick => "mouse.doubleClick",
            MouseClickKind.RightClick => "mouse.rightClick",
            _ => "mouse.click"
        };

        AppendUserEvent(eventType, timestampMs, new MousePayload(
            item.X,
            item.Y,
            item.ClickType == MouseClickKind.RightClick ? "right" : "left",
            item.ClickType == MouseClickKind.DoubleClick ? 2 : 1,
            window.ProcessName,
            window.WindowTitle,
            ToUiElementPayload(uiInfo),
            screenshotPath));
    }

    private void ProcessKey(RawItem item, long timestampMs)
    {
        switch (item.KeyKind)
        {
            case KeyboardInputKind.Text:
            {
                var window = WindowInfoService.FromForegroundWindow();
                if (IsFiltered(window))
                {
                    return;
                }

                // UIA 呼び出しは _textSync の外で行う（UA-1 対策）:
                // UIA は無応答になりうるため、_textSync を保持したまま呼ぶと
                // Pause / Stop の flush が同じロックで無期限にブロックする。
                // ロック保持範囲は aggregator への短い追加のみにする。
                var focused = _uiAutomation!.GetFocusedElement();

                // Password 判定は毎キー行う（バースト途中でパスワード欄へ
                // 移った場合も keyCount 漏れがないように。契約 §11.1）。
                // target はバースト先頭のフォーカス要素を使う。
                // 判定は処理時点のフォーカスで行うため、(1) UIA が失敗した場合、
                // (2) 受領からの処理遅延が閾値を超えた場合（入力時のフォーカスを
                //     もはや参照できない）は fail-closed で sensitive に倒す
                // （keyCount = null にする誤りは契約 §11.1 違反になるため）。
                var judgementLagged = _clock.ElapsedSinceMs(item.QpcTimestamp) > PasswordJudgementLagLimitMs;
                var isPassword = focused.Quality == UiAutomationQuality.UiAutomationFailed
                    || focused.IsPassword
                    || judgementLagged;

                lock (_textSync)
                {
                    if (_userEventsFinal)
                    {
                        // Stop の最終 flush 後に到達したキー。バーストを再開すると
                        // recording.stopped より後へ書かれるため破棄する。
                        return;
                    }

                    var windowKey = $"{window.ProcessId}:{window.WindowTitle}";
                    if (_textAggregator.IsEmpty || _lastTextWindowKey != windowKey)
                    {
                        _textTarget = (focused, window.ProcessName, window.WindowTitle);
                        _lastTextWindowKey = windowKey;
                    }

                    var flushed = _textAggregator.Add(
                        timestampMs,
                        isPassword,
                        ToTargetPayload(_textTarget.Element),
                        window.ProcessName,
                        window.WindowTitle);
                    if (flushed is not null)
                    {
                        _writer!.Append("keyboard.textEntry", flushed.StartTimestampMs, flushed.Payload);
                    }
                }

                break;
            }

            case KeyboardInputKind.SpecialKey:
                FlushTextBuffer();
                AppendUserEvent("keyboard.specialKey", timestampMs, new SpecialKeyPayload(item.KeyName ?? "Unknown"));
                break;

            case KeyboardInputKind.Shortcut:
                FlushTextBuffer();
                AppendUserEvent("keyboard.shortcut", timestampMs, new ShortcutPayload(item.ShortcutName ?? "Unknown"));
                break;
        }
    }

    /// <summary>保持中の textEntry バーストを締め切って events.jsonl に出力する。</summary>
    /// <param name="final">Stop の締め切りの場合 true。以降のユーザー Event 追加を禁止する。</param>
    private void FlushTextBuffer(bool final = false)
    {
        if (_writer is null)
        {
            return;
        }

        lock (_textSync)
        {
            var flushed = _textAggregator.Flush();
            if (flushed is not null)
            {
                _writer.Append("keyboard.textEntry", flushed.StartTimestampMs, flushed.Payload);
            }

            _lastTextWindowKey = null;
            if (final)
            {
                _userEventsFinal = true;
            }
        }
    }

    /// <summary>Pause 中、もしくは Pause 境界より前の発生（後追い処理）かどうか。</summary>
    private bool IsOutsideCanonicalTimeline(long timestampMs)
    {
        var boundary = Volatile.Read(ref _pauseBoundaryMs);
        return _clock.IsPaused || (boundary >= 0 && timestampMs <= boundary);
    }

    /// <summary>
    /// ユーザー Event（mouse.* / keyboard.specialKey / keyboard.shortcut）を events.jsonl へ出力する。
    /// Stop の最終 flush（_userEventsFinal 設定）と同じロックで直列化するため、
    /// worker の Join がタイムアウトして処理が後追いで進んでも
    /// recording.stopped より後へ書かれることはない（監査指摘 §2）。
    /// </summary>
    private void AppendUserEvent(string type, long timestampMs, object? payload)
    {
        lock (_textSync)
        {
            if (_userEventsFinal || _writer is null)
            {
                return;
            }

            // UIA・スクリーンショットなど重い処理を挟んだ間に Pause された場合、
            // この Event は Canonical Timeline の外側へ後追いで書かれないよう、
            // 書き出し直前に境界を再判定する（recording.paused と同じロックで
            // 直列化されているため、paused 行より後への割り込みも起きない）。
            if (IsOutsideCanonicalTimeline(timestampMs))
            {
                return;
            }

            _writer.Append(type, timestampMs, payload);
        }
    }

    private bool IsFiltered(WindowInfo.WindowInfo window)
    {
        return _options.WindowFilter is { } filter ? filter(window) : window.ProcessId == _ownProcessId;
    }

    // ---- テスト用シーム（InternalsVisibleTo: TrainingContent.EventCapture.Tests） ----

    /// <summary>テスト用: フックスレッドが動作中か（Start 失敗時の残留確認に使用）。</summary>
    internal bool IsHookThreadAliveForTest => _hookThread?.IsAlive ?? false;

    /// <summary>テスト用: ワーカースレッドが動作中か（Stop 時の後片付け確認に使用）。</summary>
    internal bool IsWorkerAliveForTest => _worker?.IsAlive ?? false;

    /// <summary>テスト用: 保留左クリック QPC の取得元を差し替える（null なら実フック参照）。
    /// 保留クリックと後発キーの書き出し順を疑似入力で再現するために使う。</summary>
    internal Func<long?>? PendingLeftClickQpcSourceForTest { get; set; }

    /// <summary>テスト用: false で Start するとグローバル フック / フックスレッドを
    /// 設置しない（実機ではテスト実行中の実入力が queue へ流れ込み、UIA の重い処理で
    /// worker が滞留するため、queue / worker / 書き出し順の検証を妨げる）。</summary>
    internal bool InstallHooksForTest { get; set; } = true;

    /// <summary>テスト用: 実フックを経由せず mouse.click 相当を queue へ投入する。
    /// 時刻は実クロック（QPC）から採番するため、実入力が混ざっても timestampMs の単調性は保たれる。</summary>
    internal void EnqueueMouseClickForTest(int x, int y)
    {
        Enqueue(new RawItem(Stopwatch.GetTimestamp(), RawKind.Mouse, X: x, Y: y, ClickType: MouseClickKind.Click));
    }

    /// <summary>テスト用: 物理クリック時刻（QPC）を指定して mouse.click 相当を投入する
    /// （保留クリック確定を後追いで到達する Event として再現するため）。</summary>
    internal void EnqueueMouseClickForTest(int x, int y, long qpcTimestamp)
    {
        Enqueue(new RawItem(qpcTimestamp, RawKind.Mouse, X: x, Y: y, ClickType: MouseClickKind.Click));
    }

    /// <summary>テスト用: keyboard.shortcut 相当を queue へ投入する。</summary>
    internal void EnqueueShortcutForTest(string shortcutName)
    {
        Enqueue(new RawItem(
            Stopwatch.GetTimestamp(), RawKind.Key,
            KeyKind: KeyboardInputKind.Shortcut, ShortcutName: shortcutName));
    }

    private static UiElementPayload? ToUiElementPayload(UiElementInfo info)
    {
        // UIA 失敗時も Event 自体は破棄しない（契約 §10）。
        return new UiElementPayload(
            info.Name,
            info.AutomationId,
            info.ControlType,
            info.ClassName,
            info.IsEditable,
            info.IsPassword,
            info.Bounds is { } b ? new BoundsPayload(b.X, b.Y, b.Width, b.Height) : null);
    }

    private static TargetPayload? ToTargetPayload(UiElementInfo? element)
    {
        return element is null
            ? null
            : new TargetPayload(element.Name, element.AutomationId, element.ControlType);
    }

    private static class RawKind
    {
        public const string Mouse = "mouse";
        public const string Key = "key";
    }

    internal sealed record RawItem(
        long QpcTimestamp,
        string Kind,
        int X = 0,
        int Y = 0,
        MouseClickKind ClickType = MouseClickKind.Click,
        KeyboardInputKind KeyKind = KeyboardInputKind.Text,
        string? KeyName = null,
        string? ShortcutName = null);
}
