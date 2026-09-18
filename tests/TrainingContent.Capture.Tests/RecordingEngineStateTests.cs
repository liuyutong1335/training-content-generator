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
    public async Task Stop_BeforeStart_Throws()
    {
        // ScreenRecorderRecordingEngine は StartAsync 前に StopAsync を受け付けない
        var engine = new ScreenRecorderRecordingEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.StopAsync());
    }

    [Fact]
    public async Task Pause_BeforeStart_Throws()
    {
        var engine = new ScreenRecorderRecordingEngine();
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PauseAsync());
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

    [Fact]
    public void BuildStagingPath_SameDirectory_SameExtension_UniqueName()
    {
        var canonical = Path.Combine("raw", "recording.mp4");
        var staging1 = ScreenRecorderRecordingEngine.BuildStagingPath(canonical);
        var staging2 = ScreenRecorderRecordingEngine.BuildStagingPath(canonical);

        // 同一ディレクトリ（同一ボリューム）で File.Move が機能する配置であること
        Assert.Equal(Path.GetDirectoryName(canonical), Path.GetDirectoryName(staging1));
        // 拡張子を保つ（lib が MP4 シンクを拡張子で決めるため）
        Assert.Equal(".mp4", Path.GetExtension(staging1));
        // canonical 名そのもの・既存 staging と衝突しない
        Assert.NotEqual(canonical, staging1);
        Assert.NotEqual(staging1, staging2);
        Assert.Contains(".staging-", Path.GetFileName(staging1));
    }

    [Fact]
    public void BuildStagingPath_NoDirectory_Works()
    {
        var staging = ScreenRecorderRecordingEngine.BuildStagingPath("recording.mp4");
        Assert.EndsWith(".mp4", staging, StringComparison.Ordinal);
        Assert.Contains(".staging-", staging);
    }
}
