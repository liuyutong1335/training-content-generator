// UseWPF=true の project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using TrainingContent.Capture;
using TrainingContent.Core;
using TrainingContent.Core.Models;
using TrainingContent.Core.Validation;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>1 recording session 分の finalization 入力（Engine 停止結果 + EventCapture 停止結果）。</summary>
/// <param name="ProjectId">session 開始時に固定した Project ID。</param>
/// <param name="Options">録画時に Engine へ渡した <see cref="RecordingOptions"/>（Device metadata の導出元）。</param>
/// <param name="EngineResult">DeferredCommit モードの <c>StopAsync</c> 結果。</param>
/// <param name="SessionEventsStartOffset">session 開始直前の events.jsonl byte length。</param>
/// <param name="BaseRevision">録画開始時点の <c>project.Revision</c>。</param>
/// <param name="EventCaptureFaulted">EventCapture 側が fault したか。</param>
/// <param name="EventCaptureFaultMessage">fault のユーザー向け message（null なら既定）。</param>
public sealed record RecordingFinalizationInput(
    Guid ProjectId,
    RecordingOptions Options,
    RecordingResult EngineResult,
    long SessionEventsStartOffset,
    int BaseRevision,
    bool EventCaptureFaulted = false,
    string? EventCaptureFaultMessage = null);

/// <summary><see cref="RecordingFinalizationPipeline.FinalizeAsync"/> の結果。</summary>
/// <param name="Outcome">Stop の結果（ユーザー向け message を含む）。</param>
/// <param name="CommittedProject">
/// 成功時のみ、canonical MP4 と一緒に確定した Project。<b>CurrentProject への反映は呼出側が UI thread で
/// 行う</b>（この class は <see cref="CurrentProjectContext"/> に触れない）。失敗時は null。
/// </param>
public sealed record RecordingFinalizationPipelineResult(
    RecordingStopOutcome Outcome,
    TrainingProject? CommittedProject)
{
    /// <summary><see cref="Outcome"/> の Status（呼出側の分岐用 accessor）。</summary>
    public RecordingStopStatus Status => Outcome.Status;
}

/// <summary>
/// recording stop の finalization pipeline（D-owned）。Engine 停止 + EventCapture 停止が終わった後の
/// 確定処理を 1 箇所に集約する:
/// <b>current-session events の抽出 → StepBuilder → candidate 構築 → validation →
/// <see cref="RecordingFinalizationTransaction"/> → CurrentProject の success-only swap</b>。
///
/// <para>
/// <b>session 分離</b>: events.jsonl は re-record で append されるため、ファイル全体ではなく
/// <see cref="RecordingSessionEventsReader"/> で current session の範囲だけを <c>StepBuilder</c> へ渡す。
/// </para>
/// <para>
/// <b>失敗時の pending 解決</b>: canonical を変更しない failure では、確定待ち recording を
/// <c>AbortPendingRecording</c> で best-effort に破棄する（放置すると次回 <c>StartAsync</c> が拒否される）。
/// ただし engine が既に Commit 済みの場合は Abort が <see cref="InvalidOperationException"/> になるだけで、
/// これは「pending は既に解消済み」として Trace に留める。
/// <c>RecoveryRequired</c>（rollback 失敗 = 復旧用 backup を保持）では <b>Abort しない</b>
/// （recovery artifact の状態をこれ以上変えないため）。
/// </para>
/// <para>
/// <b>CurrentProject には触れない</b>: <see cref="CurrentProjectContext.SetCurrent"/> は
/// CurrentProjectChanged を同期発火し、購読している View が WPF オブジェクトへ触るため、
/// <b>UI thread からしか呼べない</b>。この class は SynchronizationContext 非依存
/// （<c>ConfigureAwait(false)</c> 前提）に保ち、publish は呼出側
/// （<see cref="RecordingCoordinator.PublishFinalizedProject"/>）が UI thread で行う。
/// 結果として committed な Project を返すだけにする。
/// </para>
///<para>
/// 意図的にやらないこと: 独自の backup / rollback（<see cref="RecordingFinalizationTransaction"/> が唯一の
/// commit boundary）/ StepBuilder の再実装 / EventCapture / Capture の変更 / View 依存。
/// </para>
/// </summary>
public sealed class RecordingFinalizationPipeline
{
    /// <summary>EventCapture fault のユーザー向け message（coordinator 側の既定値と同じ）。</summary>
    private const string EventCaptureFaultedFallback =
        "操作記録の取得に失敗しました。録画を停止して再試行してください。";

    private const string SourceChangedMessage =
        "録画中にプロジェクトの内容が更新されたため、今回の録画は保存しませんでした。";

    private const string StepBuildFailedMessage =
        "操作記録から手順を生成できなかったため、今回の録画は保存しませんでした。";

    private const string FinalizationFailedMessage =
        "録画の確定に失敗しました。録画ファイルの状態を確認してください。";

    private const string RecoveryRequiredMessage =
        "録画の確定に失敗し、復旧用データを保持しています。サポートに連絡してください。";

    private readonly IRecordingEngine _engine;
    private readonly ProjectStore _projectStore;
    private readonly RecordingFinalizationTransaction _transaction;

    public RecordingFinalizationPipeline(
        IRecordingEngine engine,
        ProjectStore projectStore,
        RecordingFinalizationTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(transaction);

        _engine = engine;
        _projectStore = projectStore;
        _transaction = transaction;
    }

    /// <summary>
    /// finalization を実行する。<b>例外は投げず</b>、失敗は
    /// <see cref="RecordingFinalizationPipelineResult.Outcome"/> の Status とユーザー向け message で返す
    /// （raw exception message / filesystem path は返さない）。成功時のみ committed な Project を返し、
    /// CurrentProject への反映は呼出側が UI thread で行う。
    /// </summary>
    public async Task<RecordingFinalizationPipelineResult> FinalizeAsync(
        RecordingFinalizationInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var projectId = input.ProjectId;
        var result = input.EngineResult;
        var absoluteFilePath = result.FilePath;

        // ---- EventCapture fault: integrated recording として確定しない（既存 semantics）----
        if (input.EventCaptureFaulted)
        {
            AbortPendingBestEffort(result, "event capture faulted");
            return Failure(
                RecordingStopStatus.EventCaptureFailed,
                input.EventCaptureFaultMessage ?? EventCaptureFaultedFallback,
                absoluteFilePath);
        }

        // ---- preparation stop 等: DeferredCommit recording が成立していない ----
        // CaptureStarted 前に停止した場合は pending が無い（canonical は Engine が触っていない）。
        // StepBuilder / transaction の対象外とし、既存 preparation-stop semantics を変えない。
        if (!result.PendingCommit)
        {
            Trace.TraceWarning(
                "RecordingFinalizationPipeline: 確定待ちの録画がありません（PendingCommit = false）。finalization を行いません。");
            return Failure(
                RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);
        }

        // ---- Revision gate: fresh persisted Project を取得 ----
        TrainingProject? persisted;
        try
        {
            persisted = await _projectStore.LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingFinalizationPipeline: Project を読み込めません — {0}", ex);
            AbortPendingBestEffort(result, "load failed");
            return Failure(
                RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);
        }

        if (persisted is null)
        {
            Trace.TraceError("RecordingFinalizationPipeline: Project が見つかりません — {0}", projectId);
            AbortPendingBestEffort(result, "project not found");
            return Failure(
                RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);
        }

        if (persisted.Revision != input.BaseRevision)
        {
            // 録画中に teaching content が更新された。古い base からの candidate を作らない。
            Trace.TraceWarning(
                "RecordingFinalizationPipeline: 録画中に Project が更新されました（期待 {0} / 現在 {1}）。",
                input.BaseRevision,
                persisted.Revision);
            AbortPendingBestEffort(result, "source changed");
            return Failure(
                RecordingStopStatus.SourceChanged, SourceChangedMessage, absoluteFilePath);
        }

        // ---- current session の events だけを読む ----
        var eventsPath = Path.Combine(
            _projectStore.GetProjectDirectory(projectId), ProjectStore.EventsFileName);

        var read = await RecordingSessionEventsReader
            .ReadSessionAsync(eventsPath, input.SessionEventsStartOffset, cancellationToken)
            .ConfigureAwait(false);

        if (!read.Succeeded)
        {
            Trace.TraceError(
                "RecordingFinalizationPipeline: current session の events を読み出せません — {0}",
                read.ErrorMessage);
            AbortPendingBestEffort(result, "session events unavailable");
            return Failure(
                RecordingStopStatus.StepBuildFailed, StepBuildFailedMessage, absoluteFilePath);
        }

        // ---- StepBuilder（current session の JSONL のみ）----
        var build = StepBuilder.BuildFromJsonl(read.Jsonl);

        if (build.HasErrors)
        {
            // partial Steps は保存しない（RecordingInfo / Revision / canonical も動かさない）。
            Trace.TraceError(
                "RecordingFinalizationPipeline: StepBuilder が error を返しました — {0}",
                string.Join(" / ", build.Errors));
            AbortPendingBestEffort(result, "step build failed");
            return Failure(
                RecordingStopStatus.StepBuildFailed, StepBuildFailedMessage, absoluteFilePath);
        }

        if (build.Warnings.Count > 0)
        {
            // warnings は failure にしない（保存は続行する）。
            Trace.TraceWarning(
                "RecordingFinalizationPipeline: StepBuilder が warning を返しました — {0}",
                string.Join(" / ", build.Warnings));
        }

        // ---- candidate（fresh persisted を detached candidate として使う）----
        // Outputs / CreatedAtUtc / SchemaVersion / Id / Title 等は保持し、Recording / Steps /
        // Revision / UpdatedAtUtc だけを finalization の結果へ更新する。
        persisted.Recording = RecordingCoordinator.MapToRecordingInfo(result, input.Options);
        persisted.Steps = [.. build.Steps];
        persisted.Revision = input.BaseRevision + 1;
        persisted.UpdatedAtUtc = DateTimeOffset.UtcNow;

        // ---- validation（MP4 を commit する前）----
        var validationErrors = ProjectValidator.Validate(persisted);
        if (validationErrors.Count > 0)
        {
            // StepBuilder が成功しても invalid candidate は engine commit へ進めない。
            Trace.TraceError(
                "RecordingFinalizationPipeline: candidate が validation に違反しています — {0}",
                string.Join(" / ", validationErrors));
            AbortPendingBestEffort(result, "candidate invalid");
            return Failure(
                RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);
        }

        // ---- finalization transaction（唯一の commit boundary）----
        RecordingFinalizationCommitResult commit;
        try
        {
            commit = await _transaction
                .CommitAsync(projectId, persisted, input.BaseRevision, result, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // transaction は引数以外で throw しない設計だが、万一の例外でも pending を残さない。
            Trace.TraceError("RecordingFinalizationPipeline: finalization transaction が例外を投げました — {0}", ex);
            AbortPendingBestEffort(result, "transaction threw");
            return Failure(
                RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);
        }

        return MapCommitResult(input, commit, absoluteFilePath);
    }

    /// <summary>transaction の結果を pipeline の結果へ写す（§17 の result handling）。</summary>
    private RecordingFinalizationPipelineResult MapCommitResult(
        RecordingFinalizationInput input,
        RecordingFinalizationCommitResult commit,
        string? absoluteFilePath)
    {
        var result = input.EngineResult;

        switch (commit.Status)
        {
            case RecordingFinalizationCommitStatus.Committed:
                Trace.TraceInformation(
                    "RecordingFinalizationPipeline: integrated recording を確定しました（Steps {0} 件）。",
                    commit.Project?.Steps.Count ?? 0);

                // CurrentProject への反映は呼出側（UI thread）の責務。ここでは committed を返すだけ。
                return Success(commit.Project, commit.CanonicalPath ?? absoluteFilePath ?? string.Empty);

            case RecordingFinalizationCommitStatus.SourceChanged:
                AbortPendingBestEffort(result, "transaction source changed");
                return Failure(
                    RecordingStopStatus.SourceChanged, SourceChangedMessage, absoluteFilePath);

            case RecordingFinalizationCommitStatus.ProjectNotFound:
            case RecordingFinalizationCommitStatus.InvalidPendingRecording:
                AbortPendingBestEffort(result, $"transaction {commit.Status}");
                return Failure(
                    RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);

            case RecordingFinalizationCommitStatus.Failed when commit.RecoveryRequired:
                // rollback に失敗 = 復旧用 backup を保持している。recovery artifact の状態を変えないため
                // Abort しない（RecoveryDirectory は Trace のみ。UI へ raw path を出さない）。
                Trace.TraceError(
                    "RecordingFinalizationPipeline: 録画の確定に失敗し、復旧用データを保持しています — {0}",
                    commit.RecoveryDirectory);
                return Failure(
                    RecordingStopStatus.RecoveryRequired, RecoveryRequiredMessage, absoluteFilePath);

            default:
                // Failed + RecoveryRequired=false: canonical は transaction 前の状態。pending が残っていれば
                // ここで解消する（engine commit 済みなら Abort は InvalidOperationException になるだけ）。
                AbortPendingBestEffort(result, "transaction failed");
                return Failure(
                    RecordingStopStatus.FinalizationFailed, FinalizationFailedMessage, absoluteFilePath);
        }
    }

    /// <summary>finalization 失敗の結果（committed Project は無し）。</summary>
    private static RecordingFinalizationPipelineResult Failure(
        RecordingStopStatus status,
        string message,
        string? absoluteFilePath = null) =>
        new(RecordingStopOutcome.Failure(status, message, absoluteFilePath), null);

    /// <summary>finalization 成功の結果（committed な Project を呼出側へ返す）。</summary>
    private static RecordingFinalizationPipelineResult Success(TrainingProject? project, string absoluteFilePath) =>
        new(RecordingStopOutcome.Success(absoluteFilePath), project);

    /// <summary>
    /// 確定待ち recording を best-effort で破棄する。canonical は変更されない。
    /// pending が既に解消済み（engine Commit 済み）の <see cref="InvalidOperationException"/> は Trace に留める。
    ///
    /// <para>
    /// finalization を実行しないと決めた呼出側が、pending を残さない（= 次回 <c>StartAsync</c> を
    /// 塞がない）ために使う。
    /// </para>
    /// </summary>
    public void AbortPendingBestEffort(RecordingResult result, string reason)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (!result.PendingCommit)
        {
            return;
        }

        try
        {
            _engine.AbortPendingRecording();
        }
        catch (InvalidOperationException ex)
        {
            // pending already resolved（engine commit 済み等）。異常ではない。
            Trace.TraceInformation(
                "RecordingFinalizationPipeline: Abort は不要でした（pending は既に解消済み / {0}） — {1}",
                reason,
                ex.Message);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning(
                "RecordingFinalizationPipeline: 確定待ち録画の破棄に失敗しました（{0}） — {1}",
                reason,
                ex);
        }
    }
}
