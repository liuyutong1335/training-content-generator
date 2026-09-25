using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// B2: <see cref="ManualStepPlacementCalculator"/> の frozen rule（P1〜P11）。
///
/// <para>
/// midpoint rule と no-time-space rejection を pure に固定する。既存 Step の timestamp を動かさないこと、
/// Order 昇順の neighbor を使うこと（StartMs chronology ではない）もここで押さえる。
/// </para>
/// </summary>
public class ManualStepPlacementCalculatorTests
{
    private static TrainingStep Step(long startMs, int order = 0, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Order = order,
        StartMs = startMs,
        Action = StepActions.Click,
        Title = $"step @{startMs}",
    };

    // =====================================================================
    // P1 — empty Steps
    // =====================================================================

    [Fact]
    public void P1_Steps_が空なら_index0_StartMs0()
    {
        var placement = ManualStepPlacementCalculator.Calculate([], recordingDurationMs: 5000, afterStepId: null);

        Assert.Equal(ManualStepPlacementStatus.EmptySteps, placement.Status);
        Assert.Equal(0, placement.InsertionIndex);
        Assert.Equal(0, placement.StartMs);
    }

    // =====================================================================
    // P2 / P3 / P4 / P5 / P6 — between
    // =====================================================================

    [Fact]
    public void P2_1000_と_2000_の間は_1500()
    {
        var first = Step(1000, 1);
        var second = Step(2000, 2);

        var placement = ManualStepPlacementCalculator.Calculate([first, second], 5000, first.Id);

        Assert.Equal(ManualStepPlacementStatus.BetweenSteps, placement.Status);
        Assert.Equal(1, placement.InsertionIndex);
        Assert.Equal(1500, placement.StartMs);
    }

    [Fact]
    public void P3_1000_と_1002_の間は_1001()
    {
        var first = Step(1000, 1);
        var second = Step(1002, 2);

        var placement = ManualStepPlacementCalculator.Calculate([first, second], 5000, first.Id);

        Assert.Equal(ManualStepPlacementStatus.BetweenSteps, placement.Status);
        Assert.Equal(1001, placement.StartMs);
    }

    [Fact]
    public void P4_1000_と_1001_の間は_NoTimeSpace()
    {
        var placement = ManualStepPlacementCalculator.Calculate([Step(1000, 1), Step(1001, 2)], 5000, null);

        // append 側の判定になるため、between を明示する。
        var first = Step(1000, 1);
        var second = Step(1001, 2);
        var between = ManualStepPlacementCalculator.Calculate([first, second], 5000, first.Id);

        Assert.Equal(ManualStepPlacementStatus.NoTimeSpace, between.Status);
        _ = placement;
    }

    [Fact]
    public void P5_1000_と_1000_の間は_NoTimeSpace()
    {
        var first = Step(1000, 1);
        var second = Step(1000, 2);

        var placement = ManualStepPlacementCalculator.Calculate([first, second], 5000, first.Id);

        Assert.Equal(ManualStepPlacementStatus.NoTimeSpace, placement.Status);
        Assert.Equal(0, placement.StartMs);
    }

    [Fact]
    public void P6_5000_と_2000_は順序が逆なので_NoTimeSpace()
    {
        // B1 では UI order と StartMs chronology は独立。Order 順の neighbor で判定する。
        var first = Step(5000, 1);
        var second = Step(2000, 2);

        var placement = ManualStepPlacementCalculator.Calculate([first, second], 10000, first.Id);

        Assert.Equal(ManualStepPlacementStatus.NoTimeSpace, placement.Status);
    }

    // =====================================================================
    // P7 / P8 / P9 — append（right bound = Recording.DurationMs）
    // =====================================================================

    [Fact]
    public void P7_last1000_Duration3000_は_2000()
    {
        var last = Step(1000, 1);

        var placement = ManualStepPlacementCalculator.Calculate([last], 3000, last.Id);

        Assert.Equal(ManualStepPlacementStatus.Append, placement.Status);
        Assert.Equal(1, placement.InsertionIndex);
        Assert.Equal(2000, placement.StartMs);
    }

    [Fact]
    public void P8_last1000_Duration1002_は_1001()
    {
        var last = Step(1000, 1);

        var placement = ManualStepPlacementCalculator.Calculate([last], 1002, last.Id);

        Assert.Equal(ManualStepPlacementStatus.Append, placement.Status);
        Assert.Equal(1001, placement.StartMs);
    }

    [Fact]
    public void P9_last1000_Duration1001_は_NoTimeSpace()
    {
        var last = Step(1000, 1);

        var placement = ManualStepPlacementCalculator.Calculate([last], 1001, last.Id);

        Assert.Equal(ManualStepPlacementStatus.NoTimeSpace, placement.Status);
    }

    // =====================================================================
    // P10 / P11 — anchor
    // =====================================================================

    [Fact]
    public void P10_unknown_afterStepId_は_AnchorNotFound()
    {
        var placement = ManualStepPlacementCalculator.Calculate(
            [Step(1000, 1), Step(3000, 2)], 5000, Guid.NewGuid());

        Assert.Equal(ManualStepPlacementStatus.AnchorNotFound, placement.Status);
        Assert.Equal(0, placement.InsertionIndex);
    }

    [Fact]
    public void P10b_Steps_が空で_afterStepId_指定も_AnchorNotFound()
    {
        var placement = ManualStepPlacementCalculator.Calculate([], 5000, Guid.NewGuid());

        Assert.Equal(ManualStepPlacementStatus.AnchorNotFound, placement.Status);
    }

    [Fact]
    public void P11_selection_無しなら_last_Order_の後ろへ_append()
    {
        IReadOnlyList<TrainingStep> steps = [Step(1000, 1), Step(3000, 2), Step(4000, 3)];

        var placement = ManualStepPlacementCalculator.Calculate(steps, 8000, afterStepId: null);

        Assert.Equal(ManualStepPlacementStatus.Append, placement.Status);
        Assert.Equal(3, placement.InsertionIndex);
        Assert.Equal(6000, placement.StartMs); // 4000 + (8000-4000)/2
    }
}
