using TrainingContent.Core.Models;

namespace TrainingContent.App.Services;

/// <summary><see cref="RecordingFinalizationTransaction.CommitAsync"/> の結果。</summary>
public enum RecordingFinalizationCommitStatus
{
    /// <summary>canonical MP4 の確定と candidate project.json の保存が両方成功した。</summary>
    Committed,

    /// <summary>persisted Project が存在しない（削除済み等）。</summary>
    ProjectNotFound,

    /// <summary>persisted Revision が期待値と異なる（録画中に別の更新が入った）。</summary>
    SourceChanged,

    /// <summary>確定待ち録画（<see cref="TrainingContent.Capture.RecordingResult.PendingCommit"/>）が
    /// 前提を満たしていない。engine Commit は呼ばれていない。</summary>
    InvalidPendingRecording,

    /// <summary>backup / engine Commit / project.json 保存のいずれかが失敗した（rollback は試行済み）。</summary>
    Failed,
}

/// <summary>
/// Recording Finalization Transaction の結果。
///
/// <para>
/// <see cref="ErrorMessage"/> / <see cref="Error"/> は <b>log 用</b>。
/// View へ raw に表示しないこと（呼出側がユーザー向け文言へ変換する）。
/// </para>
/// </summary>
/// <param name="CanonicalPath">対象の canonical 録画パス（<c>raw/recording.mp4</c> の絶対パス）。</param>
/// <param name="Project">成功時の保存済み candidate。失敗時は null。</param>
/// <param name="RecoveryRequired">
/// <c>true</c> = rollback に失敗し、canonical が transaction 前の状態へ戻っていない。
/// <paramref name="RecoveryDirectory"/> 配下の backup が旧 canonical の唯一の複製になり得るため、
/// <b>手動 recovery が必要</b>。workspace は削除されず保持される。
/// </param>
/// <param name="RecoveryDirectory">recovery backup を保持している transaction directory。</param>
public sealed record RecordingFinalizationCommitResult(
    RecordingFinalizationCommitStatus Status,
    string? CanonicalPath = null,
    string? ErrorMessage = null,
    Exception? Error = null,
    TrainingProject? Project = null,
    bool RecoveryRequired = false,
    string? RecoveryDirectory = null);
