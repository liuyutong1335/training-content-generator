// Ported from OpenSteps (MIT License)
// Repository: https://github.com/ebanez8/openstep
// Source file: src/OpenSteps.Capture/GlobalMouseHook.cs (+ ClickCapturedEventArgs.cs)
// Commit: 8058980865ac07f261b97b7270776c486b942a16
// Modification: namespace changed; StepActionType replaced with local MouseClickKind;
//               event args merged into this file and trimmed to the fields this module uses.
//               单発クリックをダブルクリック判定待ちで保留する間も、元のクリック時刻
//               （QPC タイムスタンプ）を ClickCapturedEventArgs に保持して引き継ぐ
//               （タイマー満了時刻で採番すると約 500ms の系統誤差になるため）。
//               ダブルクリック判定外の新クリックで保留中の旧クリックを潰さず、
//               先に確定させて発火させる（上書きすると連続クリックの 1 回目が消失する）。
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TrainingContent.EventCapture.Hooks;

public enum MouseClickKind
{
    Click,
    DoubleClick,
    RightClick
}

public sealed class ClickCapturedEventArgs(
    int x,
    int y,
    MouseClickKind actionType,
    string mouseButton,
    int clickCount,
    long capturedQpcTimestamp) : EventArgs
{
    public int X { get; } = x;

    public int Y { get; } = y;

    public MouseClickKind ActionType { get; } = actionType;

    public string MouseButton { get; } = mouseButton;

    public int ClickCount { get; } = clickCount;

    /// <summary>物理クリック瞬間の Stopwatch.GetTimestamp() 値（保留解除時刻ではなく）。</summary>
    public long CapturedQpcTimestamp { get; } = capturedQpcTimestamp;
}

/// <summary>
/// グローバル マウス フック。イベントはフックスレッド上で即座に発火するため、
/// ハンドラ側で重い処理をしないこと（Low-Level Hook の応答制限）。
/// 設置スレッドでメッセージループを回す必要がある（OperationCaptureSession が担う）。
/// </summary>
public sealed class GlobalMouseHook : IDisposable
{
    private readonly object _clickSync = new();
    private readonly TimeSpan _doubleClickTime;
    private readonly int _doubleClickWidth;
    private readonly int _doubleClickHeight;
    private readonly NativeMethods.LowLevelHookProc _callback;
    private PendingLeftClick? _pendingLeftClick;
    private System.Threading.Timer? _pendingLeftClickTimer;

    /// <summary>保留クリックのタイマー世代（監査 MIN-5）。保留が更新されるたびに増やす。
    /// 前の保留のタイマー コールバックが飛行中のときに新しい保留を置き換えると、
    /// 飛行中のコールバックが新しい保留を判定時間待たずに flush して
    /// ダブルクリックが 2 単発に分裂するため、コールバック側で世代一致を確認する。</summary>
    private int _pendingGeneration;
    private IntPtr _hookHandle;

    public GlobalMouseHook()
    {
        _callback = HookCallback;
        _doubleClickTime = TimeSpan.FromMilliseconds(Math.Clamp(NativeMethods.GetDoubleClickTime(), 250, 900));
        _doubleClickWidth = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDOUBLECLK));
        _doubleClickHeight = Math.Max(4, NativeMethods.GetSystemMetrics(NativeMethods.SM_CYDOUBLECLK));
    }

    public event EventHandler<ClickCapturedEventArgs>? ClickCaptured;

    public bool IsRunning => _hookHandle != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _callback, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to install the global mouse hook.");
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        FlushPendingLeftClick();
        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WM_LBUTTONDOWN || wParam == NativeMethods.WM_RBUTTONDOWN))
        {
            var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var qpc = Stopwatch.GetTimestamp(); // 物理クリック瞬間の時刻を保持する
            if (wParam == NativeMethods.WM_RBUTTONDOWN)
            {
                FlushPendingLeftClick();
                ClickCaptured?.Invoke(this, new ClickCapturedEventArgs(data.Pt.X, data.Pt.Y, MouseClickKind.RightClick, "right", 1, qpc));
            }
            else
            {
                HandleLeftClick(data.Pt.X, data.Pt.Y, qpc);
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>左クリックの確定/保留を処理する（テスト用に internal。実フックなしでクリック系列を検証できる）。</summary>
    internal void HandleLeftClick(int x, int y, long qpc)
    {
        ClickCapturedEventArgs? doubleClick = null;
        ClickCapturedEventArgs? flushed = null;
        lock (_clickSync)
        {
            var now = DateTimeOffset.Now;
            if (_pendingLeftClick is { } pending && IsDoubleClick(pending, x, y, now))
            {
                _pendingLeftClickTimer?.Dispose();
                _pendingLeftClickTimer = null;
                _pendingLeftClick = null;
                _pendingGeneration++; // 飛行中の旧タイマー コールバックを無効化する
                doubleClick = new ClickCapturedEventArgs(x, y, MouseClickKind.DoubleClick, "left", 2, qpc);
            }
            else
            {
                // ダブルクリック判定外の新クリックで保留中の旧クリックを潰さない:
                // 先に確定させて発火し、その後で新クリックを保留する
                // （上書きすると連続クリックの 1 回目がイベントもスクリーンショットも残らず消失する）。
                if (_pendingLeftClick is { } old)
                {
                    flushed = ToSingleClickArgs(old);
                }

                _pendingLeftClickTimer?.Dispose();
                _pendingLeftClick = new PendingLeftClick(x, y, now, qpc);
                _pendingGeneration++;
                var generation = _pendingGeneration;
                _pendingLeftClickTimer = new System.Threading.Timer(_ =>
                {
                    // このコールバックが飛行中に保留が更新された場合、新しい保留を
                    // 判定時間待たずに flush しない（監査 MIN-5）。
                    if (Volatile.Read(ref _pendingGeneration) == generation)
                    {
                        FlushPendingLeftClick();
                    }
                }, null, _doubleClickTime, Timeout.InfiniteTimeSpan);
            }
        }

        if (flushed is not null)
        {
            ClickCaptured?.Invoke(this, flushed);
        }

        if (doubleClick is not null)
        {
            ClickCaptured?.Invoke(this, doubleClick);
        }
    }

    private bool IsDoubleClick(PendingLeftClick pending, int x, int y, DateTimeOffset now)
    {
        return now - pending.Timestamp <= _doubleClickTime
            && Math.Abs(x - pending.X) <= _doubleClickWidth
            && Math.Abs(y - pending.Y) <= _doubleClickHeight;
    }

    internal void FlushPendingLeftClick()
    {
        ClickCapturedEventArgs? click = null;
        lock (_clickSync)
        {
            if (_pendingLeftClick is null)
            {
                return;
            }

            _pendingLeftClickTimer?.Dispose();
            _pendingLeftClickTimer = null;
            click = ToSingleClickArgs(_pendingLeftClick);
            _pendingLeftClick = null;
        }

        ClickCaptured?.Invoke(this, click);
    }

    private static ClickCapturedEventArgs ToSingleClickArgs(PendingLeftClick pending)
    {
        // タイマー満了時刻ではなく保留クリックの物理クリック時刻を引き継ぐ
        // （タイマー満了時刻で採番するとダブルクリック待ち時間ぶん遅れる）。
        return new ClickCapturedEventArgs(pending.X, pending.Y, MouseClickKind.Click, "left", 1, pending.QpcTimestamp);
    }

    private sealed record PendingLeftClick(int X, int Y, DateTimeOffset Timestamp, long QpcTimestamp);
}
