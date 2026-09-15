using Xunit;
using TrainingContent.EventCapture;

namespace TrainingContent.EventCapture.Tests;

/// <summary>Canonical Timeline（契約 §5: 録画開始 = 0ms / Pause 時間を除外）の検証。</summary>
public class MasterClockTests
{
    [Fact]
    public void Start前は0を返す()
    {
        var clock = new MasterClock(() => 99999);
        Assert.Equal(0, clock.NowMs());
    }

    [Fact]
    public void 録画開始からの経過ミリ秒を返す()
    {
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();
        now = 1500;
        Assert.Equal(1500, clock.NowMs());
    }

    [Fact]
    public void Pause中はCanonical時間が凍結される()
    {
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();
        now = 1000;
        Assert.Equal(1000, clock.NowMs());

        clock.Pause();
        now = 11000; // Pause 中に実時間が 10 秒経過しても
        Assert.Equal(1000, clock.NowMs()); // Canonical 時間は 1000 のまま
    }

    [Fact]
    public void Resume後はPause時間を除いて進む()
    {
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();

        now = 1000;
        clock.Pause();
        now = 11000;
        clock.Resume();
        now = 12000;
        Assert.Equal(2000, clock.NowMs()); // 実 12000 - Pause 10000
    }

    [Fact]
    public void Pauseの重複呼び出しは無視される()
    {
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();

        now = 1000;
        clock.Pause();
        now = 2000;
        clock.Pause(); // 2 回目は無視される
        now = 3000;
        clock.Resume(); // Pause 累計 = 3000 - 1000 = 2000
        Assert.Equal(1000, clock.NowMs()); // 3000 - 2000
    }

    [Fact]
    public void Start前のPauseは無視される()
    {
        var clock = new MasterClock(() => 5000);
        clock.Pause();
        Assert.False(clock.IsPaused);
    }

    [Fact]
    public void 複数回のPauseResumeで累積される()
    {
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();

        now = 1000;
        clock.Pause();
        now = 3000;
        clock.Resume(); // Pause 累計 2000
        now = 4000;
        clock.Pause();
        now = 5000;
        clock.Resume(); // Pause 累計 3000
        now = 6000;
        Assert.Equal(3000, clock.NowMs()); // 6000 - 3000
    }

    [Fact]
    public void RebaseOriginToNowで現在時刻が0に張り直される()
    {
        // A の Engine が実際の撮影開始 (~2 秒後) を通知した想定。
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();
        now = 1900;
        Assert.Equal(1900, clock.NowMs());

        clock.RebaseOriginToNow(); // この瞬間が 0ms
        Assert.Equal(0, clock.NowMs());

        now = 3000;
        Assert.Equal(1100, clock.NowMs()); // 撮影開始から 1.1 秒
    }

    [Fact]
    public void Rebase後もPause除外は正しく動く()
    {
        long now = 0;
        var clock = new MasterClock(() => now);
        clock.Start();

        now = 2000;
        clock.RebaseOriginToNow(); // 原点を 2000 に移動
        now = 3000;
        clock.Pause();
        now = 8000;
        clock.Resume(); // Pause 5000 を除外
        now = 9000;
        Assert.Equal(2000, clock.NowMs()); // 9000 - 原点シフト 2000 - Pause 5000
    }
}
