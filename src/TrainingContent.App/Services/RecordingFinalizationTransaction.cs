// UseWPF=true の project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using TrainingContent.Capture;
using TrainingContent.Core.Models;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>
/// 録画の finalization transaction（D 側 boundary）。DeferredCommit 状態の新録画を canonical
/// （<c>raw/recording.mp4</c>）へ確定し、<b>同じ logical transaction で</b> candidate project.json を保存する。
///
/// <para>
/// <b>なぜ transaction が必要か</b>: DeferredCommit（PR #20）は atomicity を提供しない。
/// <c>project.json save → Commit</c> は Commit 失敗で new metadata + old MP4、
/// <c>Commit → project.json save</c> は save 失敗で new MP4 + old metadata になる。
/// そこで順序を <b>preflight → 前提再確認 → 旧 canonical backup → engine Commit → project.json save →
/// 成功 cleanup</b> に固定し、Commit 以降の failure では canonical を transaction 前の状態へ戻す。
/// </para>
/// <para>
/// <b>保証範囲</b>: 捕捉可能な failure の rollback まで。process crash からの自動復旧 /
/// journal / WAL は対象外（<see cref="VideoArtifactTransaction"/> と同じ方針）。
/// project.json 自体の write atomicity は <see cref="ProjectStore.SaveProjectAsync"/> の
/// temp + replace semantics に委ねる。
/// </para>
/// <para>
/// <b>呼出側の責務（このクラスは持たない）</b>: CurrentProject の差し替え / View の更新 /
/// pending staging の Abort 判断 / 失敗後の session 終了。candidate は呼出側が完成させて渡す
/// （この transaction は Revision・RecordingInfo・Steps・UpdatedAtUtc を決めない）。
/// </para>
/// <para>
/// <b>engine の pending state</b>: <see cref="IRecordingEngine.CommitPendingRecording"/> が成功した時点で
/// engine 側の pending は解除される。その後 project.json 保存が失敗して canonical を rollback しても、
/// engine に pending は戻らない（<b>この状態は本 transaction では正常</b>）。
/// 失敗後の再試行で <c>CommitPendingRecording</c> をもう一度呼ぶ設計にはしない
/// （新録画の唯一のコピーである staging を <c>AbortPendingRecording</c> で消すかどうかは呼出側が判断する。
/// 本 transaction は Abort を自動では呼ばない）。
/// </para>
/// <para>
/// <b>例外</b>: <see cref="CommitAsync"/> は null 引数以外では throw せず、failure を result で返す。
/// ただし cancellation は、まだ何も変更していない段階（preflight / 前提再確認）ではそのまま伝播させ、
/// canonical を変更し得る段階（backup 以降）では failure として rollback してから Failed を返す
/// （rollback の保証を優先する）。
/// </para>
/// <para>
/// 意図的にやらないこと: Repository pattern / UnitOfWork / DI Container / View 依存 /
/// StepBuilder 呼び出し / CurrentProjectContext への書き込み / 独自の JSON 直列化。
/// </para>
/// </summary>
public sealed class RecordingFinalizationTransaction
{
    private const string BackupDirectoryName = "backup";
    private const string BackupFileName = "recording.mp4";
    private const string TransactionsRootDirectoryName = "recording-transactions";

    private readonly IRecordingEngine _engine;
    private readonly ProjectStore _projectStore;

    public RecordingFinalizationTransaction(IRecordingEngine engine, ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(projectStore);

        _engine = engine;
        _projectStore = projectStore;
        TransactionsRoot = DeriveTransactionsRoot(projectStore.ProjectsRoot);
    }

    /// <summary>transaction workspace の root（canonical Project directory の外・同一 volume）。</summary>
    public string TransactionsRoot { get; }

    /// <summary>
    /// 確定待ち録画（<paramref name="pendingResult"/>）を canonical へ commit し、
    /// <paramref name="candidate"/> を project.json へ 1 回だけ保存する。
    /// </summary>
    /// <param name="projectId">対象 Project の Id。</param>
    /// <param name="candidate">呼出側が完成させた保存対象。Revision は base + 1 であること。</param>
    /// <param name="expectedBaseRevision">録画開始時点の <c>project.Revision</c>（persisted と一致することを検査する）。</param>
    /// <param name="pendingResult">DeferredCommit モードで停止した録画の <see cref="RecordingResult"/>。</param>
    public async Task<RecordingFinalizationCommitResult> CommitAsync(
        Guid projectId,
        TrainingProject candidate,
        int expectedBaseRevision,
        RecordingResult pendingResult,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(pendingResult);

        var canonicalPath = _projectStore.GetRecordingOutputPath(projectId);

        // ---- 1. preflight（filesystem mutation / engine Commit の前に、変更なしで判定できる前提を検査）----
        if (InspectPreflight(projectId, candidate, expectedBaseRevision, pendingResult, canonicalPath) is { } invalidReason)
        {
            return new RecordingFinalizationCommitResult(
                RecordingFinalizationCommitStatus.InvalidPendingRecording,
                CanonicalPath: canonicalPath,
                ErrorMessage: invalidReason);
        }

        // ---- 2. persisted Project / Revision recheck ----
        TrainingProject? persisted;
        try
        {
            persisted = await _projectStore.LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // まだ何も変更していないため、cancellation はそのまま呼出側へ返す。
            throw;
        }
        catch (Exception ex)
        {
            // 破損・未対応 schema・validation 違反・I/O 失敗。まだ何も変更していない。
            Trace.TraceError("RecordingFinalizationTransaction: persisted Project を読み込めません — {0}", ex);
            return FailedResult(canonicalPath, ex);
        }

        if (persisted is null)
        {
            return new RecordingFinalizationCommitResult(
                RecordingFinalizationCommitStatus.ProjectNotFound,
                CanonicalPath: canonicalPath,
                ErrorMessage: $"Project が見つかりません: {projectId:D}");
        }

        // 録画中に teaching content が変わっていたら commit しない（古い base の candidate を保存しない）。
        if (persisted.Revision != expectedBaseRevision)
        {
            return new RecordingFinalizationCommitResult(
                RecordingFinalizationCommitStatus.SourceChanged,
                CanonicalPath: canonicalPath,
                ErrorMessage:
                    $"録画中に Project が更新されました（期待 Revision={expectedBaseRevision} / 現在={persisted.Revision}）。");
        }

        // ---- 3〜5. backup → engine Commit → project.json save ----
        var transactionDirectory = NewTransactionDirectory(projectId);
        var backupPath = Path.Combine(transactionDirectory, BackupDirectoryName, BackupFileName);
        var hadExistingCanonical = File.Exists(canonicalPath);
        var backedUp = false;

        try
        {
            Directory.CreateDirectory(transactionDirectory);

            // ③ 旧 canonical の backup（engine Commit 前に、canonical を動かさずに複製する）。
            if (hadExistingCanonical)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                File.Copy(canonicalPath, backupPath, overwrite: true);
                backedUp = true;
            }

            // ④ engine に canonical 置換をさせる（staging → canonical）。
            var committed = _engine.CommitPendingRecording();

            // 返り値を最低限検査する（pending 解除と canonical 位置）。不一致は rollback 対象の failure。
            if (committed.PendingCommit || !PathsEqual(committed.FilePath, canonicalPath))
            {
                throw new InvalidOperationException(
                    "CommitPendingRecording の結果が期待と一致しません" +
                    $"（PendingCommit={committed.PendingCommit} / FilePath={committed.FilePath ?? "(null)"}）。");
            }

            // ⑤ candidate project.json を exactly once で保存する。
            await _projectStore.SaveProjectAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // ④⑤ 以降の failure（cancellation を含む）は canonical を transaction 前の状態へ戻す。
            var restored = Rollback(canonicalPath, backupPath, hadExistingCanonical, backedUp);

            if (restored)
            {
                TryDeleteDirectory(transactionDirectory);
                Trace.TraceError("RecordingFinalizationTransaction: commit に失敗し rollback しました — {0}", ex);
                return FailedResult(canonicalPath, ex);
            }

            // rollback に失敗 = canonical が transaction 前の状態に戻っていない。
            // backup が旧 canonical の唯一の複製になり得るため workspace を残す。
            Trace.TraceWarning(
                "RecordingFinalizationTransaction: rollback に失敗しました。recovery backup を残します — {0}",
                transactionDirectory);

            return new RecordingFinalizationCommitResult(
                RecordingFinalizationCommitStatus.Failed,
                CanonicalPath: canonicalPath,
                ErrorMessage: ex.Message,
                Error: ex,
                RecoveryRequired: true,
                RecoveryDirectory: transactionDirectory);
        }

        // ---- 6. 成功 cleanup（cleanup の失敗で成功済み transaction を失敗扱いにしない）----
        TryDeleteDirectory(transactionDirectory);

        return new RecordingFinalizationCommitResult(
            RecordingFinalizationCommitStatus.Committed,
            CanonicalPath: canonicalPath,
            Project: candidate);
    }

    /// <summary>
    /// 前提検査。<b>filesystem を変更せず、engine も呼ばない</b>段階で判定できる項目だけを見る。
    /// </summary>
    /// <returns>問題があればその理由、なければ null。</returns>
    private static string? InspectPreflight(
        Guid projectId,
        TrainingProject candidate,
        int expectedBaseRevision,
        RecordingResult pendingResult,
        string canonicalPath)
    {
        if (candidate.Id != projectId)
        {
            return $"candidate.Id が projectId と一致しません（candidate={candidate.Id:D} / project={projectId:D}）。";
        }

        if (!pendingResult.PendingCommit)
        {
            return "確定待ちの録画ではありません（PendingCommit = false）。DeferredCommit モードの StopAsync 結果を渡すこと。";
        }

        if (pendingResult.PendingCommitPath is null)
        {
            return "PendingCommitPath がありません（canonical 予定地を特定できません）。";
        }

        if (!PathsEqual(pendingResult.PendingCommitPath, canonicalPath))
        {
            return $"PendingCommitPath が canonical path と一致しません（{pendingResult.PendingCommitPath} ≠ {canonicalPath}）。";
        }

        if (pendingResult.FilePath is null || !File.Exists(pendingResult.FilePath))
        {
            return "確定待ちの staging ファイルが存在しません。";
        }

        if (new FileInfo(pendingResult.FilePath).Length == 0)
        {
            return "確定待ちの staging ファイルが 0 バイトです。";
        }

        if (candidate.Revision != expectedBaseRevision + 1)
        {
            return $"candidate.Revision が期待値と一致しません（期待 {expectedBaseRevision + 1} / 実際 {candidate.Revision}）。";
        }

        if (candidate.Recording is null)
        {
            return "candidate.Recording が null です（録画を伴わない Project は finalize できません）。";
        }

        if (!string.Equals(candidate.Recording.MediaPath, ProjectStore.RecordingMediaPath, StringComparison.Ordinal))
        {
            return "candidate.Recording.MediaPath が Contract §17 と一致しません" +
                $"（{candidate.Recording.MediaPath} ≠ {ProjectStore.RecordingMediaPath}）。";
        }

        return null;
    }

    /// <summary>
    /// canonical を transaction 前の状態へ戻す。「commit がどこまで進んだか」を決め打ちせず、
    /// backup があれば戻し、旧 canonical が無かったなら新 canonical を取り除く。
    ///
    /// <para>rollback 自体の失敗で元の例外を隠さないよう、ここでは throw せず Trace に留める。</para>
    /// </summary>
    /// <returns>
    /// <c>true</c> = canonical を transaction 前の状態へ戻せた（workspace を片付けてよい）。
    /// <c>false</c> = 戻せなかった（backup が旧 canonical の唯一の複製になり得るので残す必要がある）。
    /// </returns>
    private static bool Rollback(string canonicalPath, string backupPath, bool hadExistingCanonical, bool backedUp)
    {
        try
        {
            if (backedUp)
            {
                // 置換後なら canonical は新録画。backup で上書きして旧録画に戻す。
                File.Copy(backupPath, canonicalPath, overwrite: true);
                return true;
            }

            if (!hadExistingCanonical)
            {
                // 旧 canonical が無かったので、置換済みの新 canonical を取り除く。
                if (File.Exists(canonicalPath))
                {
                    File.Delete(canonicalPath);
                }

                return !File.Exists(canonicalPath);
            }

            // 旧 canonical があり backup 作成前に失敗 = canonical 未置換。復元不要。
            return true;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("RecordingFinalizationTransaction: rollback に失敗しました。手動確認が必要です — {0}", ex);
            return false;
        }
    }

    private static RecordingFinalizationCommitResult FailedResult(string canonicalPath, Exception ex) =>
        new(
            RecordingFinalizationCommitStatus.Failed,
            CanonicalPath: canonicalPath,
            ErrorMessage: ex.Message,
            Error: ex);

    /// <summary>ProjectsRoot の sibling を transaction root にする（同一 volume を維持）。</summary>
    private static string DeriveTransactionsRoot(string projectsRoot)
    {
        var parent = Path.GetDirectoryName(projectsRoot);
        return Path.Combine(
            string.IsNullOrEmpty(parent) ? projectsRoot : parent,
            TransactionsRootDirectoryName);
    }

    /// <summary>canonical と比較する path を絶対形へ正規化して Windows semantics で比較する。</summary>
    private static bool PathsEqual(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            // 正規化できない path（空・不正文字・長すぎる等）は文字列比較へフォールバックする。
            // ここで throw すると「引数以外では throw しない」契約を破るため、判定を落とさない。
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private string NewTransactionDirectory(Guid projectId) =>
        Path.Combine(TransactionsRoot, projectId.ToString("D"), Guid.NewGuid().ToString("N"));

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
            Trace.TraceWarning("RecordingFinalizationTransaction: 一時 directory を削除できません — {0}", ex);
        }
    }
}
