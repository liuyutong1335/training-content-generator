// 参考実装: OpenSteps src/OpenSteps.Capture/ActiveWindowService.cs (MIT License)
// Commit: 8058980865ac07f261b97b7270776c486b942a16
// Spike 向けに最小化したもの（クリック座標・フォアグラウンドから
// Process 名と Window タイトルを取得するだけ）。
using System.Diagnostics;
using OperationCaptureSpike.Hooks;

namespace OperationCaptureSpike;

public sealed record WindowInfo(uint ProcessId, string? ProcessName, string? WindowTitle)
{
    public static readonly WindowInfo Empty = new(0, null, null);
}

public static class WindowInfoService
{
    /// <summary>クリック座標上のウィンドウ（トップレベル）の情報を取得する。</summary>
    public static WindowInfo FromPoint(int x, int y)
    {
        var hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (hwnd != IntPtr.Zero)
        {
            hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        }

        return FromHandle(hwnd);
    }

    /// <summary>フォアグラウンドウィンドウの情報を取得する（キーボード入力用）。</summary>
    public static WindowInfo FromForegroundWindow()
    {
        return FromHandle(NativeMethods.GetForegroundWindow());
    }

    private static WindowInfo FromHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return WindowInfo.Empty;
        }

        var title = GetWindowTitle(hwnd);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);

        // タスクバー等のシェルウィンドウでは PID 取得やプロセス解決に失敗することが
        // あるため、その場合はフォアグラウンドウィンドウへフォールバックする。
        if (processId == 0)
        {
            return FromForegroundWindow();
        }

        string? processName = null;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            // プロセスが既に終了している場合は null のまま続行する。
        }

        return new WindowInfo(processId, processName, title);
    }

    private static string? GetWindowTitle(IntPtr hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        var length = NativeMethods.GetWindowText(hwnd, buffer, buffer.Length);
        var title = buffer[..length].ToString();
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }
}
