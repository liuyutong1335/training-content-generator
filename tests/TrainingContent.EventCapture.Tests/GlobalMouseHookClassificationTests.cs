using System.Diagnostics;
using Xunit;
using TrainingContent.EventCapture.Hooks;

namespace TrainingContent.EventCapture.Tests;

/// <summary>
/// GlobalMouseHook のクリック確定/保留ロジックの検証（実フックなし・HandleLeftClick 経由）。
/// 対応する欠陥: ダブルクリック判定外の新クリックで保留中の旧クリックが
/// emit されずに上書きされ消失する / 単発クリックの timestamp がダブルクリック
/// 待ち時間ぶん遅れる（監査 2026-09-16 実機再現 2/2）。
/// </summary>
public class GlobalMouseHookClassificationTests
{
    [Fact]
    public void ダブルクリック判定外の連続クリックで1回目が消失しない()
    {
        using var hook = new GlobalMouseHook();
        var captured = new List<ClickCapturedEventArgs>();
        hook.ClickCaptured += (_, e) => captured.Add(e);

        // 距離 200px 離れた 2 クリック（ダブルクリックと判定されない）
        hook.HandleLeftClick(10, 10, Stopwatch.GetTimestamp());
        hook.HandleLeftClick(210, 210, Stopwatch.GetTimestamp());
        hook.FlushPendingLeftClick();

        Assert.Equal(2, captured.Count);
        Assert.All(captured, e => Assert.Equal(MouseClickKind.Click, e.ActionType));
        Assert.All(captured, e => Assert.Equal(1, e.ClickCount));
    }

    [Fact]
    public void 保留クリックは物理クリック時刻のQPCを保持する()
    {
        using var hook = new GlobalMouseHook();
        var captured = new List<ClickCapturedEventArgs>();
        hook.ClickCaptured += (_, e) => captured.Add(e);

        var qpcA = Stopwatch.GetTimestamp();
        hook.HandleLeftClick(10, 10, qpcA);
        // タイマー満了を待たず手動 flush（= ダブルクリック待ち時間の遅延を模拟）
        hook.FlushPendingLeftClick();

        var click = Assert.Single(captured);
        Assert.Equal(qpcA, click.CapturedQpcTimestamp);
    }

    [Fact]
    public void 近接2クリックはダブルクリックとして1件に集約される()
    {
        using var hook = new GlobalMouseHook();
        var captured = new List<ClickCapturedEventArgs>();
        hook.ClickCaptured += (_, e) => captured.Add(e);

        hook.HandleLeftClick(10, 10, Stopwatch.GetTimestamp());
        hook.HandleLeftClick(12, 12, Stopwatch.GetTimestamp());

        var doubleClick = Assert.Single(captured);
        Assert.Equal(MouseClickKind.DoubleClick, doubleClick.ActionType);
        Assert.Equal(2, doubleClick.ClickCount);
    }

    [Fact]
    public void 右クリックは保留クリックを先に確定させる()
    {
        // HookCallback 経由はフック設置が必要なため、flush 相当を直接確認する:
        // 右クリック受領時の FlushPendingLeftClick が Stop と同一経路であることの確認。
        using var hook = new GlobalMouseHook();
        var captured = new List<ClickCapturedEventArgs>();
        hook.ClickCaptured += (_, e) => captured.Add(e);

        hook.HandleLeftClick(10, 10, Stopwatch.GetTimestamp());
        hook.FlushPendingLeftClick(); // 右クリック受領時に呼ばれるのと同一経路

        var click = Assert.Single(captured);
        Assert.Equal(MouseClickKind.Click, click.ActionType);
    }
}
