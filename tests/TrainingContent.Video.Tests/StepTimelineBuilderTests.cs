using TrainingContent.Core.Models;
using TrainingContent.Video.Timeline;
using Xunit;

namespace TrainingContent.Video.Tests;

/// <summary>契約 §14（StartMs/EndMs 規則）と §29（録画 Duration 内）に基づく表示区間の変換テスト。</summary>
public class StepTimelineBuilderTests
{
    private static TrainingStep Step(int order, long startMs, long? endMs = null) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        StartMs = startMs,
        EndMs = endMs,
        Title = $"Step {order}",
    };

    [Fact]
    public void EndMs_あり_はそのまま使う()
    {
        var steps = new[] { Step(1, 1_000, 2_000) };
        var result = StepTimelineBuilder.Build(steps, 10_000);
        Assert.Equal((1_000, 2_000), (result[0].StartMs, result[0].EndMs));
    }

    [Fact]
    public void EndMs_null_は次のStepの開始まで()
    {
        var steps = new[] { Step(1, 1_000), Step(2, 4_500) };
        var result = StepTimelineBuilder.Build(steps, 10_000);
        Assert.Equal(4_500, result[0].EndMs);
    }

    [Fact]
    public void 最終StepのEndMs_null_はフォールバック秒()
    {
        var steps = new[] { Step(1, 8_000) };
        var result = StepTimelineBuilder.Build(steps, 20_000, fallbackSeconds: 4.0);
        Assert.Equal(12_000, result[0].EndMs);
    }

    [Fact]
    public void 区間は録画Durationにクランプされる()
    {
        var steps = new[] { Step(1, 8_000) };
        var result = StepTimelineBuilder.Build(steps, 10_000, fallbackSeconds: 4.0);
        Assert.Equal(10_000, result[0].EndMs);
    }

    [Fact]
    public void 録画の外側のStepはスキップされる()
    {
        var steps = new[] { Step(1, 15_000, 16_000) };
        var result = StepTimelineBuilder.Build(steps, 10_000);
        Assert.Empty(result);
    }

    [Fact]
    public void startMsとendMsが同値なら最低表示時間を確保する()
    {
        var steps = new[] { Step(1, 1_000, 1_000) };
        var result = StepTimelineBuilder.Build(steps, 10_000);
        Assert.Equal(1_000 + StepTimelineBuilder.MinimumDisplayMs, result[0].EndMs);
    }

    [Fact]
    public void 空_または全体が範囲外なら空を返す()
    {
        Assert.Empty(StepTimelineBuilder.Build([], 10_000));
        Assert.Empty(StepTimelineBuilder.Build(new[] { Step(1, 99_999) }, 10_000));
    }
}
