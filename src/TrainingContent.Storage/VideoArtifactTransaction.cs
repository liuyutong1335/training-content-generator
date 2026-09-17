using System.Diagnostics;
using TrainingContent.Core.Models;

namespace TrainingContent.Storage;

/// <summary>
/// Video artifact（<c>output/training_video.mp4</c>）の staged replacement を行う最小の transaction boundary。
///
/// <para>
/// FfmpegVideoRenderer は <c>request.OutputPath</c> へ直接出力するため、canonical path をそのまま渡すと
/// 生成失敗時に既存の video を壊す。そこで <b>canonical Project directory の外</b>に staging workspace を作り、
/// 完全に成功したときだけ canonical へ置換する。
/// </para>
/// <para>
/// <b>配置</b>: ProjectsRoot の sibling（同じ volume）。Project directory 配下には置かない
/// （Contract §17 の固定構造は Shared Contract であり、D が directory を追加できないため）。
/// </para>
/// <para>
/// 意図的にやらないこと: Repository pattern / UnitOfWork / journal transaction / crash recovery。
/// process crash からの自動復旧は対象外（捕捉可能な失敗の rollback までが本 class の保証範囲）。
/// </para>
/// </summary>
public sealed class VideoArtifactTransaction
{
    /// <summary>Contract §17 の canonical video path（project 相対）。</summary>
    public const string CanonicalRelativePath = "output/training_video.mp4";

    /// <summary>staging / backup に使うファイル名（canonical と同じ名前に揃える）。</summary>
    private const string ArtifactFileName = "training_video.mp4";

    private const string StagingDirectoryName = "staging";
    private const string BackupDirectoryName = "backup";

    private readonly ProjectStore _projectStore;

    public VideoArtifactTransaction(ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);

        _projectStore = projectStore;
        TransactionsRoot = DeriveTransactionsRoot(projectStore.ProjectsRoot);
    }

    /// <summary>transaction workspace の root（canonical Project directory の外）。</summary>
    public string TransactionsRoot { get; }

    /// <summary>
    /// staging workspace を作成し、書き込み先を返す。生成側はここへ出力する。
    /// </summary>
    public VideoArtifactStaging BeginStaging(Guid projectId)
    {
        var transactionDirectory = Path.Combine(
            TransactionsRoot, projectId.ToString("D"), Guid.NewGuid().ToString("N"));
        var stagingDirectory = Path.Combine(transactionDirectory, StagingDirectoryName);
        var backupDirectory = Path.Combine(transactionDirectory, BackupDirectoryName);

        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);

        return new VideoArtifactStaging(
            projectId,
            transactionDirectory,
            Path.Combine(stagingDirectory, ArtifactFileName),
            Path.Combine(backupDirectory, ArtifactFileName),
            CanonicalPath(projectId));
    }

    /// <summary>
    /// staging を破棄する。生成失敗時・commit 失敗時に呼ぶ。冪等（既に消えていても例外にしない）。
    ///
    /// <para>
    /// <b>defense-in-depth</b>: <c>backup/training_video.mp4</c> が残っている transaction は削除しない。
    /// rollback に失敗した場合、その backup は旧 canonical の<b>唯一の複復元可能な複製</b>であり、
    /// 呼出側が誤って削除すると recovery 不能になるため。将来別の呼出側が追加されても同じ保証が効く。
    /// </para>
    /// </summary>
    public void DiscardStaging(VideoArtifactStaging staging)
    {
        ArgumentNullException.ThrowIfNull(staging);

        if (File.Exists(staging.BackupPath))
        {
            Trace.TraceWarning(
                "VideoArtifactTransaction: recovery backup が残っているため staging を削除しません — {0}",
                staging.TransactionDirectory);
            return;
        }

        TryDeleteDirectory(staging.TransactionDirectory);
    }

    /// <summary>canonical video の実パス。</summary>
    public string CanonicalPath(Guid projectId) =>
        Path.Combine(
            _projectStore.GetProjectDirectory(projectId),
            "output",
            ArtifactFileName);

    /// <summary>
    /// staged video を canonical へ commit する。
    ///
    /// <para>
    /// 順序: ① staged の存在 / size 確認 ② project 再 load と Revision recheck ③ 既存 canonical の backup
    /// ④ staged → canonical 置換 ⑤ TrainingVideo metadata 更新 ⑥ project.json Save ⑦ backup cleanup。
    /// </para>
    /// <para>
    /// ②で Revision が変わっていた場合は <b>何も変更せず</b> SourceChanged を返す（生成中に teaching content が
    /// 編集された場合、古い Revision の動画を current artifact として commit しない）。
    /// ④以降で失敗した場合は rollback する（backup があれば戻し、無ければ新しい canonical を削除する）。
    /// </para>
    /// </summary>
    /// <param name="staging"><see cref="BeginStaging"/> が返した workspace。</param>
    /// <param name="sourceRevision">生成開始時の <c>project.Revision</c>。</param>
    public async Task<VideoArtifactCommitResult> CommitAsync(
        VideoArtifactStaging staging,
        int sourceRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);

        if (!File.Exists(staging.StagingPath) || new FileInfo(staging.StagingPath).Length == 0)
        {
            return new VideoArtifactCommitResult(
                VideoArtifactCommitStatus.StagingMissing,
                ErrorMessage: "staging に有効な動画がありません。");
        }

        var project = await _projectStore.LoadProjectAsync(staging.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return new VideoArtifactCommitResult(
                VideoArtifactCommitStatus.ProjectNotFound,
                ErrorMessage: $"Project が見つかりません: {staging.ProjectId:D}");
        }

        // Revision recheck: 生成中に teaching content が変わっていたら commit しない。
        if (project.Revision != sourceRevision)
        {
            return new VideoArtifactCommitResult(
                VideoArtifactCommitStatus.SourceChanged,
                CanonicalPath: staging.CanonicalPath,
                ErrorMessage:
                    $"生成中に Project が更新されました（開始時 Revision={sourceRevision} / 現在={project.Revision}）。");
        }

        var hadExistingCanonical = File.Exists(staging.CanonicalPath);
        var backedUp = false;

        try
        {
            if (hadExistingCanonical)
            {
                File.Copy(staging.CanonicalPath, staging.BackupPath, overwrite: true);
                backedUp = true;
            }

            // 置換（同一 volume 前提。cross-volume は MVP の対象外）。
            File.Move(staging.StagingPath, staging.CanonicalPath, overwrite: true);

            var now = DateTimeOffset.UtcNow;
            project.Outputs.TrainingVideo = new GeneratedArtifact
            {
                Path = CanonicalRelativePath,
                GeneratedAtUtc = now,
                SourceRevision = sourceRevision,
            };
            // artifact metadata の保存時刻として更新する。Revision は進めない
            // （output generation は teaching content の編集ではない）。
            project.UpdatedAtUtc = now;

            await _projectStore.SaveProjectAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var restored = Rollback(staging, hadExistingCanonical, backedUp);

            if (restored)
            {
                // canonical は commit 前の状態へ戻った。workspace ごと片付けてよい。
                TryDeleteDirectory(staging.TransactionDirectory);
                Trace.TraceError("VideoArtifactTransaction: commit に失敗し rollback しました — {0}", ex);

                return new VideoArtifactCommitResult(
                    VideoArtifactCommitStatus.Failed,
                    CanonicalPath: staging.CanonicalPath,
                    ErrorMessage: ex.Message,
                    Error: ex);
            }

            // rollback に失敗 = backup が旧 canonical の唯一の複製。workspace を消さず残す。
            Trace.TraceWarning(
                "VideoArtifactTransaction: rollback に失敗しました。recovery backup を残します — {0}",
                staging.TransactionDirectory);

            return new VideoArtifactCommitResult(
                VideoArtifactCommitStatus.Failed,
                CanonicalPath: staging.CanonicalPath,
                ErrorMessage: ex.Message,
                Error: ex,
                RecoveryRequired: true,
                RecoveryDirectory: staging.TransactionDirectory);
        }

        TryDeleteDirectory(staging.TransactionDirectory);
        return new VideoArtifactCommitResult(
            VideoArtifactCommitStatus.Committed,
            CanonicalPath: staging.CanonicalPath,
            Project: project);
    }

    /// <summary>
    /// canonical を commit 前の状態へ戻す。
    /// rollback 自体の失敗で元の例外を隠さないよう、ここでは例外を投げず Trace に留める。
    /// </summary>
    /// <returns>
    /// <c>true</c> = canonical artifact を commit 前状態へ復元できた（workspace を片付けてよい）。
    /// <c>false</c> = 復元できなかった（<c>backup</c> が旧 canonical の唯一の複製なので残す必要がある）。
    /// </returns>
    private static bool Rollback(VideoArtifactStaging staging, bool hadExistingCanonical, bool backedUp)
    {
        try
        {
            if (backedUp)
            {
                // 置換後なら canonical は新しいファイル。backup で上書きして旧 artifact に戻す。
                File.Copy(staging.BackupPath, staging.CanonicalPath, overwrite: true);
                TryDeleteFile(staging.BackupPath);
                return true;
            }

            if (!hadExistingCanonical)
            {
                // 旧 artifact が無かったので、置換済みの新規 canonical を取り除く。
                TryDeleteFile(staging.CanonicalPath);
                return !File.Exists(staging.CanonicalPath);
            }

            // 旧 canonical があり backup 作成前に失敗 = canonical は未置換。復元不要。
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(
                "VideoArtifactTransaction: rollback に失敗しました。手動確認が必要です — {0}", ex);
            return false;
        }
    }

    /// <summary>ProjectsRoot の sibling を transactions root にする（同一 volume を維持）。</summary>
    private static string DeriveTransactionsRoot(string projectsRoot)
    {
        var parent = Path.GetDirectoryName(projectsRoot);
        return Path.Combine(string.IsNullOrEmpty(parent) ? projectsRoot : parent, "transactions");
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("VideoArtifactTransaction: 一時 directory を削除できません — {0}", ex);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("VideoArtifactTransaction: 一時 file を削除できません — {0}", ex);
        }
    }
}

/// <summary><see cref="VideoArtifactTransaction.BeginStaging"/> が返す staging workspace。</summary>
public sealed class VideoArtifactStaging
{
    internal VideoArtifactStaging(
        Guid projectId,
        string transactionDirectory,
        string stagingPath,
        string backupPath,
        string canonicalPath)
    {
        ProjectId = projectId;
        TransactionDirectory = transactionDirectory;
        StagingPath = stagingPath;
        BackupPath = backupPath;
        CanonicalPath = canonicalPath;
    }

    public Guid ProjectId { get; }

    /// <summary>この transaction の作業 directory（cleanup 単位）。</summary>
    public string TransactionDirectory { get; }

    /// <summary>生成側が書き込む staging の実パス。</summary>
    public string StagingPath { get; }

    /// <summary>既存 canonical を退避する backup の実パス。</summary>
    public string BackupPath { get; }

    /// <summary>置換先の canonical 実パス。</summary>
    public string CanonicalPath { get; }
}

/// <summary><see cref="VideoArtifactTransaction.CommitAsync"/> の結果。</summary>
public enum VideoArtifactCommitStatus
{
    Committed,
    StagingMissing,
    ProjectNotFound,
    SourceChanged,
    Failed,
}

/// <summary>
/// commit の結果。失敗理由を呼出側が判別できるようにする。
/// </summary>
/// <param name="RecoveryRequired">
/// <c>true</c> = rollback に失敗し、<paramref name="RecoveryDirectory"/> 配下の backup が
/// 旧 canonical の唯一の複製として残っている。<b>手動 recovery が必要</b>。
/// </param>
/// <param name="RecoveryDirectory">recovery backup を保持している transaction directory。</param>
public sealed record VideoArtifactCommitResult(
    VideoArtifactCommitStatus Status,
    string? CanonicalPath = null,
    string? ErrorMessage = null,
    Exception? Error = null,
    TrainingProject? Project = null,
    bool RecoveryRequired = false,
    string? RecoveryDirectory = null);
