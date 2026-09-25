using TrainingContent.Core.Models;

namespace TrainingContent.Storage;

/// <summary>manual Step を挿入できる位置の分類。</summary>
public enum ManualStepPlacementStatus
{
    /// <summary>Steps が空。最初の Step として挿入する（StartMs = 0 = canonical recording origin）。</summary>
    EmptySteps,

    /// <summary>隣接 Step の間へ挿入する。</summary>
    BetweenSteps,

    /// <summary>最後の Step の後ろ（Recording の末尾まで）へ挿入する。</summary>
    Append,

    /// <summary>時間上の余裕が無い（追加しない）。</summary>
    NoTimeSpace,

    /// <summary>指定された anchor Step が見つからない（追加しない）。</summary>
    AnchorNotFound,
}

/// <summary>
/// manual Step の挿入位置。<see cref="InsertionIndex"/> は Order 昇順 list への index、
/// <see cref="StartMs"/> は compute された timestamp（失敗時は 0）。
/// </summary>
public readonly record struct ManualStepPlacement(
    ManualStepPlacementStatus Status,
    int InsertionIndex,
    long StartMs);

/// <summary>
/// manual Step の StartMs を決める pure helper（B2 の frozen rule）。
///
/// <para>
/// <b>midpoint rule</b>: 挿入位置の左右の timestamp から <c>L + (R - L) / 2</c>（integer division）で決める。
/// 右端は「次 Step の StartMs」または「Recording.DurationMs」。余裕（<c>R - L</c>）が 2ms 未満なら
/// <see cref="ManualStepPlacementStatus.NoTimeSpace"/> として追加しない。
/// </para>
/// <para>
/// <b>既存 Step の timestamp は動かさない</b>: 自動補正・magic offset（+500ms 等）は行わない。
/// Order（UI 順）と StartMs chronology は独立のため、neighbor は <b>Order 昇順の canonical 順序</b>で決める
/// （StartMs で並べ替えない）。
/// </para>
/// </summary>
public static class ManualStepPlacementCalculator
{
    /// <summary>midpoint を作るのに必要な最小の余裕（ms）。</summary>
    public const long MinimumGapMs = 2;

    /// <summary>
    /// <paramref name="orderedSteps"/>（<b>Order 昇順</b>）と <paramref name="recordingDurationMs"/> から
    /// 挿入位置を決める。
    /// </summary>
    /// <param name="afterStepId">
    /// この Step の直後へ挿入する。<c>null</c> の場合は Steps が空なら最初、それ以外は最後の後ろ。
    /// </param>
    public static ManualStepPlacement Calculate(
        IReadOnlyList<TrainingStep> orderedSteps,
        long recordingDurationMs,
        Guid? afterStepId)
    {
        ArgumentNullException.ThrowIfNull(orderedSteps);

        if (orderedSteps.Count == 0)
        {
            // 空の Steps に anchor を指定することはできない（stale selection を勝手に fallback しない）。
            return afterStepId is not null
                ? new ManualStepPlacement(ManualStepPlacementStatus.AnchorNotFound, 0, 0)
                : new ManualStepPlacement(ManualStepPlacementStatus.EmptySteps, 0, 0);
        }

        var anchorIndex = afterStepId is { } anchorId
            ? IndexOf(orderedSteps, anchorId)
            : orderedSteps.Count - 1;

        if (anchorIndex < 0)
        {
            return new ManualStepPlacement(ManualStepPlacementStatus.AnchorNotFound, 0, 0);
        }

        var left = orderedSteps[anchorIndex].StartMs;
        var hasNext = anchorIndex + 1 < orderedSteps.Count;
        var right = hasNext ? orderedSteps[anchorIndex + 1].StartMs : recordingDurationMs;

        if (right - left < MinimumGapMs)
        {
            // 同 timestamp の Step 間も追加不可。既存 timestamp は触らない。
            return new ManualStepPlacement(ManualStepPlacementStatus.NoTimeSpace, 0, 0);
        }

        return new ManualStepPlacement(
            hasNext ? ManualStepPlacementStatus.BetweenSteps : ManualStepPlacementStatus.Append,
            anchorIndex + 1,
            left + ((right - left) / 2));
    }

    private static int IndexOf(IReadOnlyList<TrainingStep> steps, Guid stepId)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Id == stepId)
            {
                return i;
            }
        }

        return -1;
    }
}
