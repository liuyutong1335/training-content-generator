using TrainingContent.Capture;
using Xunit;

namespace TrainingContent.Capture.Tests;

/// <summary>
/// 録画エンジンの状態機械テスト（デバイス非依存の範囲）。
/// ScreenRecorderLib の実機検証は Spike A（計画書 §14 Spike A / Gate A）で行う。
/// </summary>
public class RecordingEngineStateTests
{
    [Fact]
    public void Stop_BeforeStart_Throws()
    {
        // ScreenRecorderRecordingEngine は StartAsync 前に StopAsync を受け付けない
        // （実装確定は Spike A。ここでは契約レベルの期待値を固定する）
        var engine = new ScreenRecorderRecordingEngine();
        Assert.ThrowsAsync<InvalidOperationException>(() => engine.StopAsync());
    }

    [Fact]
    public void Pause_BeforeStart_Throws()
    {
        var engine = new ScreenRecorderRecordingEngine();
        Assert.ThrowsAsync<InvalidOperationException>(() => engine.PauseAsync());
    }

    [Fact]
    public void RecordingResult_PauseIntervals_DefaultIsEmpty()
    {
        var result = new RecordingResult
        {
            FilePath = "raw/recording.mp4",
            Duration = TimeSpan.FromSeconds(10),
            StartedAtUtc = DateTimeOffset.UtcNow,
        };
        Assert.Empty(result.PauseIntervals);
    }
}
