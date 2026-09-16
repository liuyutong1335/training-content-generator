using System.Collections.Concurrent;
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
    private long? _pauseBoundaryMs;
    private (UiElementInfo? Element, string? ProcessName, string? WindowTitle) _textTarget;
    private string? _lastTextWindowKey;
    private bool _started;
    private bool _stopped;
    private bool _startFailed;

    public string ProjectDirectory { get; }

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

            _worker = new Thread(ProcessQueue) { IsBackground = true };
            _worker.Start();

            _clock.Start();
            _writer.Append("recording.started", _clock.NowMs(), new { });
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
    }

    /// <summary>録画を一時停止する。Pause 中の実時間は Canonical Timeline に含まれない（契約 §5.2）。</summary>
    public void Pause()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();
            FlushTextBuffer();
            _clock.Pause();
            _pauseBoundaryMs = _clock.NowMs();
            _writer!.Append("recording.paused", _clock.NowMs(), new { });
        }
    }

    /// <summary>録画を再開する。</summary>
    public void Resume()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();
            FlushTextBuffer();
            _clock.Resume();
            _writer!.Append("recording.resumed", _clock.NowMs(), new { });
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
                _worker?.Join(15000); // 終端イベントの取りこぼし防止（UIA・撮影は 1 Event 百ms 級）

                try
                {
                    // textEntry バーストはワーカーが queue を処理し終わった後に締め切る。
                    // drain 前に flush すると queue 残存イベントより古い textEntry が先に
                    // 書かれ、ファイル行順の timestampMs 非減少が崩れるため。
                    // （ワーカーの各処理パスは書き込み直前に flush 済みなので、ここで残るのは
                    //   最後の Text キーで始まった未締め切りバーストのみ）
                    FlushTextBuffer();
                }
                catch
                {
                    // 書き込み失敗でもリソース解放は続行する。
                }

                try
                {
                    _writer?.Append("recording.stopped", durationMs, new { });
                }
                catch
                {
                    // 終端イベントの書き込みに失敗してもリソース解放は続行する
                    // （events.jsonl が掴まれている場合など。フック / スレッドの残留を防ぐ方を優先）。
                }

                return durationMs;
            }
            finally
            {
                TeardownHooks(); // 冪等。途中で例外が出てもフック解除を保証する
            }
        }
    }

    public void Dispose()
    {
        lock (_stateSync)
        {
            try
            {
                if (_started && !_stopped)
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
        if (_startFailed || !_started || _stopped)
        {
            throw new InvalidOperationException("セッションは録画中ではありません。");
        }
    }

    private void Enqueue(RawItem item)
    {
        if (!_queue.IsAddingCompleted)
        {
            _queue.Add(item);
        }
    }

    private void OnMouseClick(object? sender, ClickCapturedEventArgs e)
    {
        Enqueue(new RawItem(_clock.NowMs(), RawKind.Mouse, X: e.X, Y: e.Y, ClickType: e.ActionType));
    }

    private void OnKeyboardInput(object? sender, KeyboardInputEventArgs e)
    {
        Enqueue(new RawItem(_clock.NowMs(), RawKind.Key, KeyKind: e.Kind, KeyName: e.KeyName, ShortcutName: e.ShortcutName));
    }

    private void ProcessQueue()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            try
            {
                ProcessItem(item);
            }
            catch
            {
                // 1 Event の処理失敗で録画全体を止めない。
            }
        }
    }

    private void ProcessItem(RawItem item)
    {
        // Pause 中のユーザー操作は Canonical Timeline の外側のため破棄する。
        // さらに Pause より前に発生していながら処理が追いつかなかった
        // 後追いイベント（例: P 押下自体のキーダウン）も破棄する。
        var boundary = _pauseBoundaryMs;
        if (_clock.IsPaused || (boundary.HasValue && item.TimestampMs < boundary.Value))
        {
            return;
        }

        if (item.Kind == RawKind.Mouse)
        {
            ProcessMouse(item);
        }
        else if (item.Kind == RawKind.Key)
        {
            ProcessKey(item);
        }
    }

    private void ProcessMouse(RawItem item)
    {
        lock (_textSync)
        {
            FlushTextBuffer();
        }

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

        _writer!.Append(eventType, item.TimestampMs, new MousePayload(
            item.X,
            item.Y,
            item.ClickType == MouseClickKind.RightClick ? "right" : "left",
            item.ClickType == MouseClickKind.DoubleClick ? 2 : 1,
            window.ProcessName,
            window.WindowTitle,
            ToUiElementPayload(uiInfo),
            screenshotPath));
    }

    private void ProcessKey(RawItem item)
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

                lock (_textSync)
                {
                    // Password 判定は毎キー行う（バースト途中でパスワード欄へ
                    // 移った場合も keyCount 漏れがないように。契約 §11.1）。
                    // target はバースト先頭のフォーカス要素を使う。
                    var focused = _uiAutomation!.GetFocusedElement();
                    var windowKey = $"{window.ProcessId}:{window.WindowTitle}";
                    if (_textAggregator.IsEmpty || _lastTextWindowKey != windowKey)
                    {
                        _textTarget = (focused, window.ProcessName, window.WindowTitle);
                        _lastTextWindowKey = windowKey;
                    }

                    var isPassword = focused.IsPassword;
                    var flushed = _textAggregator.Add(
                        item.TimestampMs,
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
                lock (_textSync)
                {
                    FlushTextBuffer();
                }

                _writer!.Append("keyboard.specialKey", item.TimestampMs, new SpecialKeyPayload(item.KeyName ?? "Unknown"));
                break;

            case KeyboardInputKind.Shortcut:
                lock (_textSync)
                {
                    FlushTextBuffer();
                }

                _writer!.Append("keyboard.shortcut", item.TimestampMs, new ShortcutPayload(item.ShortcutName ?? "Unknown"));
                break;
        }
    }

    /// <summary>保持中の textEntry バーストを締め切って events.jsonl に出力する。</summary>
    private void FlushTextBuffer()
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

    /// <summary>テスト用: 実フックを経由せず mouse.click 相当を queue へ投入する。
    /// 時刻は実クロックから採番するため、実入力が混ざっても timestampMs の単調性は保たれる。</summary>
    internal void EnqueueMouseClickForTest(int x, int y)
    {
        Enqueue(new RawItem(_clock.NowMs(), RawKind.Mouse, X: x, Y: y, ClickType: MouseClickKind.Click));
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

    private sealed record RawItem(
        long TimestampMs,
        string Kind,
        int X = 0,
        int Y = 0,
        MouseClickKind ClickType = MouseClickKind.Click,
        KeyboardInputKind KeyKind = KeyboardInputKind.Text,
        string? KeyName = null,
        string? ShortcutName = null);
}
