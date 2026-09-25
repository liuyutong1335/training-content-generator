using System.Diagnostics;
using TrainingContent.Core.Models;

namespace TrainingContent.Storage;

/// <summary>
/// Manual artifact（<c>manual/manual.md</c> + <c>manual/manual.html</c>）の staged pair replacement を行う
/// narrow な transaction boundary。
///
/// <para>
/// Manual の成果物は <b>Markdown + HTML の pair</b> であり、片方だけが current になる状態を正常成功として
/// 作らない（<see cref="Manual.ManualGenerator"/> の in-memory atomic は filesystem atomicity を保証しない）。
/// そこで canonical Project directory の外に staging / backup を作り、完全に成功したときだけ pair を置換する。
/// </para>
/// <para>
/// <b>配置</b>: ProjectsRoot の sibling（同じ volume）。Project directory 配下には置かない
/// （Contract §17 の固定構造は Shared Contract であり、ここで directory を追加できないため）。
/// </para>
/// <para>
/// 意図的にやらないこと: VideoArtifactTransaction との共通 framework 化 / journal transaction /
/// process crash からの自動復旧（捕捉可能な失敗の rollback までが本 class の保証範囲）。
/// </para>
/// </summary>
public sealed class ManualArtifactTransaction
{
    /// <summary>Contract §17 の canonical markdown path（project 相対）。</summary>
    public const string MarkdownRelativePath = "manual/manual.md";

    /// <summary>Contract §17 の canonical html path（project 相対）。</summary>
    public const string HtmlRelativePath = "manual/manual.html";

    private const string MarkdownFileName = "manual.md";
    private const string HtmlFileName = "manual.html";
    private const string StagingDirectoryName = "staging";
    private const string BackupDirectoryName = "backup";
    private const string TransactionsDirectoryName = "manual-transactions";

    private readonly ProjectStore _projectStore;

    public ManualArtifactTransaction(ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);

        _projectStore = projectStore;
        TransactionsRoot = DeriveTransactionsRoot(projectStore.ProjectsRoot);
    }

    /// <summary>transaction workspace の root（canonical Project directory の外）。</summary>
    public string TransactionsRoot { get; }

    /// <summary>
    /// staging workspace を作成し、書き込み先を返す。生成側はここへ Markdown / HTML を書く。
    /// </summary>
    public ManualArtifactStaging BeginStaging(Guid projectId)
    {
        var transactionDirectory = Path.Combine(
            TransactionsRoot, projectId.ToString("D"), Guid.NewGuid().ToString("N"));
        var stagingDirectory = Path.Combine(transactionDirectory, StagingDirectoryName);
        var backupDirectory = Path.Combine(transactionDirectory, BackupDirectoryName);

        Directory.CreateDirectory(stagingDirectory);
        Directory.CreateDirectory(backupDirectory);

        var projectDirectory = _projectStore.GetProjectDirectory(projectId);

        return new ManualArtifactStaging(
            projectId,
            transactionDirectory,
            Path.Combine(stagingDirectory, MarkdownFileName),
            Path.Combine(stagingDirectory, HtmlFileName),
            Path.Combine(backupDirectory, MarkdownFileName),
            Path.Combine(backupDirectory, HtmlFileName),
            Path.Combine(projectDirectory, "manual", MarkdownFileName),
            Path.Combine(projectDirectory, "manual", HtmlFileName));
    }

    /// <summary>
    /// staging を破棄する。生成失敗時・commit 失敗時に呼ぶ。冪等（既に消えていても例外にしない）。
    ///
    /// <para>
    /// <b>defense-in-depth</b>: backup（旧 pair の唯一の複製）が残っている transaction は削除しない。
    /// rollback に失敗した場合、その backup は recovery 不能になるのを防ぐため。
    /// </para>
    /// </summary>
    public void DiscardStaging(ManualArtifactStaging staging)
    {
        ArgumentNullException.ThrowIfNull(staging);

        if (File.Exists(staging.BackupMarkdownPath) || File.Exists(staging.BackupHtmlPath))
        {
            Trace.TraceWarning(
                "ManualArtifactTransaction: recovery backup が残っているため staging を削除しません — {0}",
                staging.TransactionDirectory);
            return;
        }

        TryDeleteDirectory(staging.TransactionDirectory);
    }

    /// <summary>
    /// staged pair を canonical へ commit する。
    ///
    /// <para>
    /// 順序: ① staged markdown / html の存在と size 確認 ② project 再 load と Revision recheck
    /// ③ 既存 canonical の有無を記録 ④ 既存 pair の backup ⑤ staged markdown → canonical
    /// ⑥ staged html → canonical ⑦ 同一 <c>now</c> で Outputs の pair 更新 ⑧ UpdatedAtUtc 更新
    /// ⑨ project.json Save ⑩ backup / transaction cleanup。
    /// </para>
    /// <para>
    /// ②で Revision が変わっていた場合は <b>何も変更せず</b> SourceChanged を返す。④以降で失敗した場合は
    /// markdown / html の<b>両方</b>を commit 前の exact filesystem state へ戻す（非対称な既存状態もそのまま復元し、
    /// 勝手に「修復」しない）。片方だけ戻して終了しない。
    /// </para>
    /// <para>Manual 生成では <c>Project.Revision</c> を増やさない（teaching content の編集ではない）。</para>
    /// </summary>
    public async Task<ManualArtifactCommitResult> CommitAsync(
        ManualArtifactStaging staging,
        int sourceRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(staging);

        if (!IsValidStagedFile(staging.StagingMarkdownPath) || !IsValidStagedFile(staging.StagingHtmlPath))
        {
            return new ManualArtifactCommitResult(
                ManualArtifactCommitStatus.StagingMissing,
                ErrorMessage: "staging に有効な Markdown / HTML がありません。");
        }

        var project = await _projectStore.LoadProjectAsync(staging.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        if (project is null)
        {
            return new ManualArtifactCommitResult(
                ManualArtifactCommitStatus.ProjectNotFound,
                ErrorMessage: $"Project が見つかりません: {staging.ProjectId:D}");
        }

        // Revision recheck: 生成中に teaching content が変わっていたら commit しない。
        if (project.Revision != sourceRevision)
        {
            return new ManualArtifactCommitResult(
                ManualArtifactCommitStatus.SourceChanged,
                ErrorMessage:
                    $"生成中に Project が更新されました（開始時 Revision={sourceRevision} / 現在={project.Revision}）。");
        }

        var markdownExisted = File.Exists(staging.CanonicalMarkdownPath);
        var htmlExisted = File.Exists(staging.CanonicalHtmlPath);
        var markdownBackedUp = false;
        var htmlBackedUp = false;

        try
        {
            if (markdownExisted)
            {
                File.Copy(staging.CanonicalMarkdownPath, staging.BackupMarkdownPath, overwrite: true);
                markdownBackedUp = true;
            }

            if (htmlExisted)
            {
                File.Copy(staging.CanonicalHtmlPath, staging.BackupHtmlPath, overwrite: true);
                htmlBackedUp = true;
            }

            // 置換（同一 volume 前提。cross-volume は MVP の対象外）。
            File.Move(staging.StagingMarkdownPath, staging.CanonicalMarkdownPath, overwrite: true);
            File.Move(staging.StagingHtmlPath, staging.CanonicalHtmlPath, overwrite: true);

            // pair の metadata は同一時刻・同一 Revision で揃える。
            var now = DateTimeOffset.UtcNow;
            project.Outputs.ManualMarkdown = new GeneratedArtifact
            {
                Path = MarkdownRelativePath,
                GeneratedAtUtc = now,
                SourceRevision = sourceRevision,
            };
            project.Outputs.ManualHtml = new GeneratedArtifact
            {
                Path = HtmlRelativePath,
                GeneratedAtUtc = now,
                SourceRevision = sourceRevision,
            };
            // artifact metadata の保存時刻として更新する。Revision は進めない。
            project.UpdatedAtUtc = now;

            await _projectStore.SaveProjectAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var restored = Rollback(staging, markdownExisted, markdownBackedUp, htmlExisted, htmlBackedUp);

            if (restored)
            {
                // canonical は commit 前の状態へ戻った。workspace ごと片付けてよい。
                TryDeleteDirectory(staging.TransactionDirectory);
                Trace.TraceError("ManualArtifactTransaction: commit に失敗し rollback しました — {0}", ex);

                return new ManualArtifactCommitResult(
                    ManualArtifactCommitStatus.Failed,
                    ErrorMessage: ex.Message,
                    Error: ex);
            }

            // rollback に失敗 = backup が旧 pair の唯一の複製。workspace を消さず残す。
            Trace.TraceWarning(
                "ManualArtifactTransaction: rollback に失敗しました。recovery backup を残します — {0}",
                staging.TransactionDirectory);

            return new ManualArtifactCommitResult(
                ManualArtifactCommitStatus.Failed,
                ErrorMessage: ex.Message,
                Error: ex,
                RecoveryRequired: true,
                RecoveryDirectory: staging.TransactionDirectory);
        }

        TryDeleteDirectory(staging.TransactionDirectory);
        return new ManualArtifactCommitResult(
            ManualArtifactCommitStatus.Committed,
            Project: project);
    }

    /// <summary>
    /// markdown / html の両方を commit 前の exact filesystem state へ戻す。
    ///
    /// <para>
    /// 各 file を独立に処理し、片方の失敗で他方の復元を止めない（「片方だけ rollback して終了」しない）。
    /// rollback 自体の失敗で元の例外を隠さないよう、ここでは例外を投げず Trace に留める。
    /// </para>
    /// </summary>
    /// <returns>
    /// <c>true</c> = 両方を commit 前状態へ復元できた（workspace を片付けてよい）。
    /// <c>false</c> = 復元できなかった file がある（backup が旧 pair の唯一の複製なので残す必要がある）。
    /// </returns>
    private static bool Rollback(
        ManualArtifactStaging staging,
        bool markdownExisted,
        bool markdownBackedUp,
        bool htmlExisted,
        bool htmlBackedUp)
    {
        var markdownRestored = TryRestoreFile(
            staging.BackupMarkdownPath, staging.CanonicalMarkdownPath, markdownExisted, markdownBackedUp);
        var htmlRestored = TryRestoreFile(
            staging.BackupHtmlPath, staging.CanonicalHtmlPath, htmlExisted, htmlBackedUp);

        return markdownRestored && htmlRestored;
    }

    private static bool TryRestoreFile(string backupPath, string canonicalPath, bool existed, bool backedUp)
    {
        try
        {
            if (backedUp)
            {
                // 置換後なら canonical は新しい内容。backup で上書きして旧 artifact に戻す。
                File.Copy(backupPath, canonicalPath, overwrite: true);
                TryDeleteFile(backupPath);
                return true;
            }

            if (!existed)
            {
                // 旧 artifact が無かったので、置換済みの新規 canonical を取り除く。
                TryDeleteFile(canonicalPath);
                return !File.Exists(canonicalPath);
            }

            // 旧 canonical があり backup 作成前に失敗 = canonical は未置換。復元不要。
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning(
                "ManualArtifactTransaction: rollback に失敗しました。手動確認が必要です — {0}", ex);
            return false;
        }
    }

    private static bool IsValidStagedFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>ProjectsRoot の sibling を transactions root にする（同一 volume を維持）。</summary>
    private static string DeriveTransactionsRoot(string projectsRoot)
    {
        var parent = Path.GetDirectoryName(projectsRoot);
        return Path.Combine(
            string.IsNullOrEmpty(parent) ? projectsRoot : parent, TransactionsDirectoryName);
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
            Trace.TraceWarning("ManualArtifactTransaction: 一時 directory を削除できません — {0}", ex);
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
            Trace.TraceWarning("ManualArtifactTransaction: 一時 file を削除できません — {0}", ex);
        }
    }
}

/// <summary><see cref="ManualArtifactTransaction.BeginStaging"/> が返す staging workspace。</summary>
public sealed class ManualArtifactStaging
{
    internal ManualArtifactStaging(
        Guid projectId,
        string transactionDirectory,
        string stagingMarkdownPath,
        string stagingHtmlPath,
        string backupMarkdownPath,
        string backupHtmlPath,
        string canonicalMarkdownPath,
        string canonicalHtmlPath)
    {
        ProjectId = projectId;
        TransactionDirectory = transactionDirectory;
        StagingMarkdownPath = stagingMarkdownPath;
        StagingHtmlPath = stagingHtmlPath;
        BackupMarkdownPath = backupMarkdownPath;
        BackupHtmlPath = backupHtmlPath;
        CanonicalMarkdownPath = canonicalMarkdownPath;
        CanonicalHtmlPath = canonicalHtmlPath;
    }

    public Guid ProjectId { get; }

    /// <summary>この transaction の作業 directory（cleanup 単位）。</summary>
    public string TransactionDirectory { get; }

    /// <summary>生成側が Markdown を書き込む staging の実パス。</summary>
    public string StagingMarkdownPath { get; }

    /// <summary>生成側が HTML を書き込む staging の実パス。</summary>
    public string StagingHtmlPath { get; }

    /// <summary>既存 canonical markdown を退避する backup の実パス。</summary>
    public string BackupMarkdownPath { get; }

    /// <summary>既存 canonical html を退避する backup の実パス。</summary>
    public string BackupHtmlPath { get; }

    /// <summary>置換先の canonical markdown 実パス。</summary>
    public string CanonicalMarkdownPath { get; }

    /// <summary>置換先の canonical html 実パス。</summary>
    public string CanonicalHtmlPath { get; }
}

/// <summary><see cref="ManualArtifactTransaction.CommitAsync"/> の結果。</summary>
public enum ManualArtifactCommitStatus
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
/// 旧 pair の唯一の複製として残っている。<b>手動 recovery が必要</b>。
/// </param>
/// <param name="RecoveryDirectory">recovery backup を保持している transaction directory。</param>
public sealed record ManualArtifactCommitResult(
    ManualArtifactCommitStatus Status,
    string? ErrorMessage = null,
    Exception? Error = null,
    TrainingProject? Project = null,
    bool RecoveryRequired = false,
    string? RecoveryDirectory = null);
