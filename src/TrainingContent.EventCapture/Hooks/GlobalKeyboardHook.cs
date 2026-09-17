// Ported from OpenSteps (MIT License)
// Repository: https://github.com/ebanez8/openstep
// Source file: src/OpenSteps.Capture/GlobalKeyboardHook.cs
//              (+ KeyboardInputEventArgs.cs / KeyboardInputKind.cs, merged here)
// Commit: 8058980865ac07f261b97b7270776c486b942a16
// Modification: namespace changed; special-key names aligned with the Phase 0 contract
//               MVP list (Enter/Tab/Escape/Backspace/Delete/Left/Right/Up/Down).
//               VK_PROCESSKEY (0xE5, IME 変換中のキー) を Text 入力として分類するよう追加
//               （IME 有効時は通常の印刷可能キーが VK_PROCESSKEY に置き換わるため、
//                 そのままだと日本語入力が一切記録されない）。
//               実入力文字は取得しない（契約 §11.1 / §27 セキュリティ方針）。
//               Alt/Ctrl 付きのキーを specialKey に分類しない（Alt+Tab → "Tab" 等の
//               誤記録防止。修飾キー付きは MVP 対象の Ctrl 系ショットカートのみ）。
//               Ctrl+Shift+S のような Shift 併用を表記に反映する。
using System.Runtime.InteropServices;

namespace TrainingContent.EventCapture.Hooks;

public enum KeyboardInputKind
{
    Text,
    SpecialKey,
    Shortcut
}

public sealed class KeyboardInputEventArgs(KeyboardInputKind kind, string? keyName, string? shortcutName) : EventArgs
{
    public KeyboardInputKind Kind { get; } = kind;

    public string? KeyName { get; } = keyName;

    public string? ShortcutName { get; } = shortcutName;
}

/// <summary>
/// グローバル キーボード フック。Keylogger ではない:
/// 入力文字そのものは保存せず、Text / SpecialKey / Shortcut の意味分類のみを通知する。
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelHookProc _callback;
    private IntPtr _hookHandle;

    public GlobalKeyboardHook()
    {
        _callback = HookCallback;
    }

    public event EventHandler<KeyboardInputEventArgs>? KeyboardInputCaptured;

    public bool IsRunning => _hookHandle != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _callback, IntPtr.Zero, 0);
        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to install the global keyboard hook.");
        }
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        NativeMethods.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        Stop();
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == NativeMethods.WM_KEYDOWN || wParam == NativeMethods.WM_SYSKEYDOWN))
        {
            try
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                var captured = ClassifyKey(
                    (int)data.VkCode,
                    ctrl: IsDown(0x11) || IsDown(0xA2) || IsDown(0xA3),
                    alt: IsDown(0x12) || IsDown(0xA4) || IsDown(0xA5),
                    shift: IsDown(0x10) || IsDown(0xA0) || IsDown(0xA1));
                if (captured is not null)
                {
                    KeyboardInputCaptured?.Invoke(this, captured);
                }
            }
            catch
            {
                // Keyboard capture should never interrupt the target application or the hook chain.
            }
        }

        return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    /// <summary>修飾キー状態は引数で受け取る（GetAsyncKeyState は実キー状態依存のためテストで注入できるように）。</summary>
    internal static KeyboardInputEventArgs? ClassifyKey(int vkCode, bool ctrl, bool alt, bool shift)
    {
        if (ctrl)
        {
            var shortcut = vkCode switch
            {
                0x41 => "Ctrl+A",
                0x43 => "Ctrl+C",
                0x53 => "Ctrl+S",
                0x56 => "Ctrl+V",
                0x5A => "Ctrl+Z",
                _ => null
            };

            if (shortcut is not null)
            {
                // Shift 押下を表記に反映する（Ctrl+Shift+S を Ctrl+S と誤記録しないため）。
                var name = shift ? shortcut.Replace("Ctrl+", "Ctrl+Shift+", StringComparison.Ordinal) : shortcut;
                return new KeyboardInputEventArgs(KeyboardInputKind.Shortcut, null, name);
            }
        }

        // Alt / Ctrl 付きのキーは specialKey にしない
        // （Alt+Tab を specialKey "Tab"、Ctrl+Enter を specialKey "Enter" と
        //   誤記録して手順書に余分な Step を混入させないため。契約 §11.2 の
        //   MVP 対象は修飾キーなしの特殊キーのみ）。
        if (alt || ctrl)
        {
            return null;
        }

        var special = vkCode switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x2E => "Delete",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            _ => null
        };

        if (special is not null)
        {
            return new KeyboardInputEventArgs(KeyboardInputKind.SpecialKey, special, null);
        }

        // VK_PROCESSKEY (0xE5): IME がキーを消費した合図。実文字は取得できないが
        // 「テキスト入力があった」ことは記録できる（日本語 IME 対応）。
        if (IsPrintableTypingKey(vkCode) || vkCode == 0x20 || vkCode == 0xE5)
        {
            return new KeyboardInputEventArgs(KeyboardInputKind.Text, shift ? "Shift+Text" : "Text", null);
        }

        return null;
    }

    private static bool IsPrintableTypingKey(int vkCode)
    {
        return vkCode is >= 0x30 and <= 0x39
            or >= 0x41 and <= 0x5A
            or >= 0x60 and <= 0x6F
            or >= 0xBA and <= 0xC0
            or >= 0xDB and <= 0xDE;
    }

    private static bool IsDown(int virtualKey)
    {
        return (NativeMethods.GetAsyncKeyState(virtualKey) & unchecked((short)0x8000)) != 0;
    }
}
