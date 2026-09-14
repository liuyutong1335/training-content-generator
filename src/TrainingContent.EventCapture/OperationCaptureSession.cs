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
    private readonly List<TextKey> _textBuffer = [];
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

    public string ProjectDirectory { get; }

    /// <summary>書き出した Event の総数（recording.* ライフサイクルを含む）。</summary>
    public long EventCount => _writer?.Count ?? 0;

    public OperationCaptureSession(string projectDirectory, OperationCaptureOptions? options = null)
    {
        ProjectDirectory = projectDirectory;
        _options = options ?? new OperationCaptureOptions();
    }

    /// <summary>録画を開始する（フック設置 + recording.started の書き出し）。</summary>
    public void Start()
    {
        lock (_stateSync)
        {
            if (_started)
            {
                throw new InvalidOperationException("このセッションは既に開始されています。");
            }

            _started = true;
        }

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

    /// <summary>録画を停止し、論理 DurationMs を返す。</summary>
    public long Stop()
    {
        lock (_stateSync)
        {
            ThrowIfNotRunning();
            _stopped = true;

            FlushTextBuffer();
            var durationMs = _clock.NowMs();
            System.Windows.Forms.Application.Exit(); // フックスレッドのメッセージループを抜ける
            _hookThread!.Join(5000);
            _writer!.Append("recording.stopped", durationMs, new { });
            _queue.CompleteAdding();
            _worker!.Join(3000);
            return durationMs;
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
            _mouseHook.Stop();
            _keyboardHook.Stop();
        }
        catch (Exception ex)
        {
            _hookInitError = ex;
            ready.Set();
        }
    }

    private void ThrowIfNotRunning()
    {
        if (!_started || _stopped)
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
                    // バースト先頭またはウィンドウが変わった時点でフォーカス要素を取得
                    // （Password 判定と target 用。スパイクでの簡略化を踏襲）。
                    var windowKey = $"{window.ProcessId}:{window.WindowTitle}";
                    if (_textBuffer.Count == 0 || _lastTextWindowKey != windowKey)
                    {
                        _textTarget = (_uiAutomation!.GetFocusedElement(), window.ProcessName, window.WindowTitle);
                        _lastTextWindowKey = windowKey;
                    }

                    _textBuffer.Add(new TextKey(
                        item.TimestampMs,
                        _textTarget.Element?.IsPassword == true,
                        window.ProcessName,
                        window.WindowTitle));

                    // 入力が途切れたらバーストを閉じる（別ウィンドウ / 2 秒以上の空白）。
                    if (_textBuffer.Count > 1
                        && item.TimestampMs - _textBuffer[^2].TimestampMs > 2000)
                    {
                        FlushTextBuffer();
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

    /// <summary>
    /// 連続するテキスト入力キーを 1 つの keyboard.textEntry Event にまとめる
    /// （契約 §11.1 の keyCount / §20 の例に対応）。実入力文字は取得しない。
    /// Password 判定のバーストは keyCount = null + isSensitive = true。
    /// </summary>
    private void FlushTextBuffer()
    {
        if (_textBuffer.Count == 0 || _writer is null)
        {
            return;
        }

        var isSensitive = _textBuffer.Any(k => k.IsPassword);
        var payload = new TextEntryPayload(
            isSensitive ? null : _textBuffer.Count,
            _textBuffer[0].ProcessName,
            _textBuffer[0].WindowTitle,
            !isSensitive && _textTarget.Element is { } el
                ? new TargetPayload(el.Name, el.AutomationId, el.ControlType)
                : null,
            isSensitive);
        _writer.Append("keyboard.textEntry", _textBuffer[0].TimestampMs, payload);
        _textBuffer.Clear();
        _textTarget = default;
        _lastTextWindowKey = null;
    }

    private bool IsFiltered(WindowInfo.WindowInfo window)
    {
        return _options.WindowFilter is { } filter ? filter(window) : window.ProcessId == _ownProcessId;
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

    private sealed record TextKey(
        long TimestampMs,
        bool IsPassword,
        string? ProcessName,
        string? WindowTitle);
}
