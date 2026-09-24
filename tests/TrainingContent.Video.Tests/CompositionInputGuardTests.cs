using TrainingContent.Core.Models;
using TrainingContent.Video;
using Xunit;

namespace TrainingContent.Video.Tests;

/// <summary>監査 m-5（Recording == null で字幕 0 件の動画が「成功」として返る guard）。</summary>
public class CompositionInputGuardTests
{
    private static TrainingStep MakeStep(int order) => new() { Order = order, StartMs = 1_000, Title = $"Step {order}" };

    [Fact]
    public void RecordingありのProjectは拒否しない()
    {
        var project = new TrainingProject
        {
            Recording = new RecordingInfo { MediaPath = "raw/recording.mp4", DurationMs = 10_000 },
            Steps = [MakeStep(1)],
        };

        CompositionInputGuard.EnsureRecordingPresent(project); // 例外が出ないこと
    }

    [Fact]
    public void RecordingnullのProjectは拒否する()
    {
        var project = new TrainingProject { Steps = [MakeStep(1)] };

        var ex = Assert.Throws<ArgumentException>(() => CompositionInputGuard.EnsureRecordingPresent(project));
        Assert.Contains("Recording", ex.Message);
    }

    [Fact]
    public void Project自身がnullの場合も拒否する()
    {
        Assert.Throws<ArgumentNullException>(() => CompositionInputGuard.EnsureRecordingPresent(null!));
    }
}
