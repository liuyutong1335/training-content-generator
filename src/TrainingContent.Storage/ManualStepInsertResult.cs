using TrainingContent.Core.Models;

namespace TrainingContent.Storage;

/// <summary><see cref="ProjectStore.InsertManualStepAsync"/> の結果分類。</summary>
public enum ManualStepInsertStatus
{
    /// <summary>manual Step を挿入し project.json へ保存した（Revision +1）。</summary>
    Inserted,

    /// <summary>Recording が無い（manual Step の timestamp を決められない）。</summary>
    RecordingMissing,

    /// <summary>指定された anchor Step が存在しない（stale selection を別位置へ fallback しない）。</summary>
    AnchorNotFound,

    /// <summary>隣接する timestamp の間に余裕が無い（既存 timestamp は動かさない）。</summary>
    NoTimeSpace,

    /// <summary>Title が blank。</summary>
    InvalidTitle,
}

/// <summary>
/// <see cref="ProjectStore.InsertManualStepAsync"/> の結果。
/// </summary>
/// <param name="Project">成功時の updated Project（未保存の参照ではなく保存済みの内容）。</param>
/// <param name="StepId">成功時に作成された manual Step の Id（Review の draft 再構築後の選択に使う）。</param>
/// <param name="StartMs">成功時に決まった StartMs。</param>
public sealed record ManualStepInsertResult(
    ManualStepInsertStatus Status,
    TrainingProject? Project = null,
    Guid? StepId = null,
    long StartMs = 0)
{
    public bool Succeeded => Status == ManualStepInsertStatus.Inserted;
}
