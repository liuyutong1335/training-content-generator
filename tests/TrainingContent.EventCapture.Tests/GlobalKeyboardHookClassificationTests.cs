using Xunit;
using TrainingContent.EventCapture.Hooks;

namespace TrainingContent.EventCapture.Tests;

/// <summary>
/// GlobalKeyboardHook のキー分類の検証（実キー状態に依存しない ClassifyKey 経由）。
/// 対応する欠陥: special 判定が Alt/Ctrl 判定より前にあり、Alt+Tab が
/// specialKey "Tab" として誤記録される（監査 KB-1 / High・CONFIRMED）。
/// </summary>
public class GlobalKeyboardHookClassificationTests
{
    [Theory]
    [InlineData(0x09)] // Tab
    [InlineData(0x0D)] // Enter
    [InlineData(0x1B)] // Escape
    public void Alt付きの特殊キーはspecialKeyとして誤記録されない(int vkCode)
    {
        var captured = GlobalKeyboardHook.ClassifyKey(vkCode, ctrl: false, alt: true, shift: false);
        Assert.Null(captured);
    }

    [Theory]
    [InlineData(0x09)] // Tab
    [InlineData(0x0D)] // Enter
    public void Ctrl付きの特殊キーはspecialKeyとして誤記録されない(int vkCode)
    {
        var captured = GlobalKeyboardHook.ClassifyKey(vkCode, ctrl: true, alt: false, shift: false);
        Assert.Null(captured);
    }

    [Fact]
    public void 単独のTabはspecialKeyとして記録される()
    {
        var captured = GlobalKeyboardHook.ClassifyKey(0x09, ctrl: false, alt: false, shift: false);
        Assert.NotNull(captured);
        Assert.Equal(KeyboardInputKind.SpecialKey, captured!.Kind);
        Assert.Equal("Tab", captured.KeyName);
    }

    [Fact]
    public void Shift付きのTabは逆Tab移動としてspecialKeyのまま()
    {
        // Shift 単独の修飾は specialKey 判定を妨げない（契約 §11.2 の MVP 対象は修飾キーなし表記）。
        var captured = GlobalKeyboardHook.ClassifyKey(0x09, ctrl: false, alt: false, shift: true);
        Assert.NotNull(captured);
        Assert.Equal(KeyboardInputKind.SpecialKey, captured!.Kind);
        Assert.Equal("Tab", captured.KeyName);
    }

    [Fact]
    public void CtrlSはshortcutとして記録される()
    {
        var captured = GlobalKeyboardHook.ClassifyKey(0x53, ctrl: true, alt: false, shift: false);
        Assert.NotNull(captured);
        Assert.Equal(KeyboardInputKind.Shortcut, captured!.Kind);
        Assert.Equal("Ctrl+S", captured.ShortcutName);
    }

    [Fact]
    public void CtrlShiftSはShiftを省略せず記録される()
    {
        // 修正前は Ctrl+S と誤記録され、手順書の操作が変わってしまう。
        var captured = GlobalKeyboardHook.ClassifyKey(0x53, ctrl: true, alt: false, shift: true);
        Assert.NotNull(captured);
        Assert.Equal(KeyboardInputKind.Shortcut, captured!.Kind);
        Assert.Equal("Ctrl+Shift+S", captured.ShortcutName);
    }

    [Fact]
    public void MVP対象外のCtrlキーは記録されない()
    {
        var captured = GlobalKeyboardHook.ClassifyKey(0x4E, ctrl: true, alt: false, shift: false); // Ctrl+N
        Assert.Null(captured);
    }
}
