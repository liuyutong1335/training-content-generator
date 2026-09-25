using System.Diagnostics;
using System.IO;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using TrainingContent.Video;

namespace TrainingContent.App.Services;

/// <summary>
/// Video 生成の backend orchestration。View から filesystem / Project metadata を触らせないための窓口。
///
/// <para>
/// 流れ: Project load → precondition → staging output → <see cref="IVideoComposer.ComposeAsync"/> →
/// Revision recheck → canonical artifact replacement → TrainingVideo metadata save → Current Project 更新。
/// 置換と metadata commit は <see cref="VideoArtifactTransaction"/>（Storage）が所有する。
/// </para>
/// <para>
/// Video module 本体（Renderer / Subtitle / Timeline）は担当 A の成果物であり、ここからは
/// <see cref="IVideoComposer"/> 経由でしか触らない。
/// </para>
/// <para>
/// 意図的にやらないこと: Project.Revision の increment（output generation は teaching content の
/// 編集ではない）、UI 都合の状態保持、例外の丸投げ。
/// </para>
/// </summary>
public sealed class VideoGenerationCoordinator
{
    private readonly ProjectStore _projectStore;
    private readonly CurrentProjectContext _currentProject;
    private readonly VideoArtifactTransaction _transaction;
    private readonly Func<IVideoComposer> _composerFactory;

    /// <param name="composerFactory">
    /// <see cref="IVideoComposer"/> を必要時に生成する factory。<b>constructor では呼ばない</b>——
    /// <see cref="FfmpegVideoRenderer"/> は生成時に ffmpeg.exe を探索して不在なら例外を投げるため、
    /// App 起動時に評価すると FFmpeg 未導入の環境でアプリが起動できなくなる。
    /// </param>
    public VideoGenerationCoordinator(
        ProjectStore projectStore,
        CurrentProjectContext currentProject,
        VideoArtifactTransaction transaction,
        Func<IVideoComposer> composerFactory)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(composerFactory);

        _projectStore = projectStore;
        _currentProject = currentProject;
        _transaction = transaction;
        _composerFactory = composerFactory;
    }

    /// <summary>
    /// 指定 Project の Video を生成し、成功したら canonical artifact と metadata を置換する。
    /// 失敗しても既存の canonical video / metadata は変更しない。
    ///
    /// <para>
    /// <paramref name="progress"/> は <see cref="VideoCompositionOptions.Progress"/> へそのまま渡す。
    /// ここでは値の加工（stage 名の翻訳・進捗の丸め・捏造）を行わない
    /// （<see cref="VideoCompositionProgress"/> の値は Video Core が authority）。
    /// </para>
    /// <para>
    /// <paramref name="cancellationToken"/> が cancel された場合は例外を投げず
    /// <see cref="VideoGenerationStatus.Cancelled"/> を返す。staging は破棄し、
    /// canonical / metadata / Revision / UpdatedAtUtc は生成前のままにする。
    /// rollback の判断は <see cref="VideoArtifactTransaction"/> の既存 semantics に従い、
    /// ここで rollback logic を複製しない（recovery が必要な失敗は Cancelled へ丸めない）。
    /// </para>
    /// </summary>
    public async Task<VideoGenerationResult> GenerateAsync(
        Guid projectId,
        IProgress<VideoCompositionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TrainingProject? project;
        try
        {
            project = await _projectStore.LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // staging も canonical も触っていない。cancel は failure ではない。
            return Cancelled();
        }

        if (project is null)
        {
            return new VideoGenerationResult(
                VideoGenerationStatus.ProjectNotFound,
                ErrorMessage: $"Project が見つかりません: {projectId:D}");
        }

        if (project.Recording is null)
        {
            return new VideoGenerationResult(
                VideoGenerationStatus.RecordingMissing,
                ErrorMessage: "録画がありません。");
        }

        var recordingPath = _projectStore.GetRecordingOutputPath(projectId);
        if (!File.Exists(recordingPath))
        {
            return new VideoGenerationResult(
                VideoGenerationStatus.RecordingMissing,
                ErrorMessage: "録画ファイルが見つかりません。");
        }

        // Steps が空 = canonical reviewed training content がまだ無い。Video の入力にならない。
        if (project.Steps.Count == 0)
        {
            return new VideoGenerationResult(
                VideoGenerationStatus.StepsMissing,
                ErrorMessage: "手順がありません。先に録画を停止して手順を確定してください。");
        }

        IVideoComposer composer;
        try
        {
            composer = _composerFactory();
        }
        catch (Exception ex)
        {
            // ffmpeg 未導入は App の不具合ではなく環境要因なので、UI が識別できる status にする。
            if (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Trace.TraceWarning("VideoGenerationCoordinator: composer を初期化できません — {0}", ex);
                return new VideoGenerationResult(
                    VideoGenerationStatus.ComposerUnavailable,
                    ErrorMessage: ex.Message);
            }

            // 想定外の初期化失敗も裸の例外として View へ流さない。
            Trace.TraceError("VideoGenerationCoordinator: composer の初期化に失敗しました — {0}", ex);
            return new VideoGenerationResult(
                VideoGenerationStatus.Failed,
                ErrorMessage: ex.Message,
                Error: ex);
        }

        var sourceRevision = project.Revision;
        VideoArtifactStaging? staging = null;

        try
        {
            staging = _transaction.BeginStaging(projectId);

            await composer.ComposeAsync(
                    new VideoCompositionRequest
                    {
                        RecordingPath = recordingPath,
                        OutputPath = staging.StagingPath,
                        Project = project,
                    },
                    // MVP のその他 options は既定値のまま。UI から接続するのは progress だけ。
                    new VideoCompositionOptions { Progress = progress },
                    cancellationToken)
                .ConfigureAwait(true);

            var commit = await _transaction
                .CommitAsync(staging, sourceRevision, cancellationToken)
                .ConfigureAwait(true);

            if (commit.Status != VideoArtifactCommitStatus.Committed)
            {
                // Storage 側の DiscardStaging は recovery backup が残っていれば削除を拒否する
                // （rollback failure 時の唯一の複製を守る defense-in-depth）。
                _transaction.DiscardStaging(staging);

                if (commit.RecoveryRequired)
                {
                    Trace.TraceWarning(
                        "VideoGenerationCoordinator: 手動 recovery が必要です。backup を {0} に残しました。",
                        commit.RecoveryDirectory);
                }

                var status = VideoGenerationCommitClassifier.ToStatus(commit);
                if (status == VideoGenerationStatus.Cancelled)
                {
                    Trace.TraceInformation(
                        "VideoGenerationCoordinator: commit 中に cancel されました（rollback 済み）。");
                }

                return new VideoGenerationResult(
                    status,
                    // cancel された場合に raw exception の message を UI へ流さない。
                    ErrorMessage: status == VideoGenerationStatus.Cancelled ? null : commit.ErrorMessage,
                    RecoveryDirectory: commit.RecoveryRequired ? commit.RecoveryDirectory : null);
            }

            // 生成対象が Current Project のときだけ差し替える（別 Project の生成で巻き戻さない）。
            // UI が観測する state なので、この publish は caller の context（UI thread）で行う。
            if (commit.Project is { } updated && _currentProject.IsCurrent(projectId))
            {
                _currentProject.SetCurrent(updated);
            }

            return new VideoGenerationResult(
                VideoGenerationStatus.Generated,
                CanonicalPath: commit.CanonicalPath);
        }
        catch (OperationCanceledException)
        {
            // compose 中の user cancel（renderer は ffmpeg の process tree を kill してから throw する）。
            if (staging is not null)
            {
                _transaction.DiscardStaging(staging);
            }

            Trace.TraceInformation("VideoGenerationCoordinator: Video 生成をキャンセルしました。");
            return Cancelled();
        }
        catch (Exception ex)
        {
            // staging が未作成なら cleanup 対象は無い。途中まで作成されていれば安全に破棄する。
            if (staging is not null)
            {
                _transaction.DiscardStaging(staging);
            }

            Trace.TraceError("VideoGenerationCoordinator: Video 生成に失敗しました — {0}", ex);
            return new VideoGenerationResult(
                VideoGenerationStatus.Failed,
                ErrorMessage: ex.Message,
                Error: ex);
        }
    }

    private static VideoGenerationResult Cancelled() => new(VideoGenerationStatus.Cancelled);
}

/// <summary>
/// <see cref="VideoArtifactTransaction.CommitAsync"/> の結果を <see cref="VideoGenerationStatus"/> へ分類する。
///
/// <para>
/// 順序が意味を持つ: recovery が必要な失敗（rollback に失敗し backup を保持）は
/// <b>cancellation へ丸めない</b>。manual recovery 対象の storage failure state を
/// 「ユーザーが cancel した」と表示すると、backup の存在を UI から隠してしまうため。
/// </para>
/// <para>
/// 逆に rollback 成功済みの失敗（<c>RecoveryRequired == false</c>）が
/// <see cref="OperationCanceledException"/> を報告した場合は cancel として扱う
/// （canonical は commit 前の状態へ戻っており、failure として見せる必要がない）。
/// </para>
/// </summary>
public static class VideoGenerationCommitClassifier
{
    /// <summary>commit の失敗が「cancel された」ことを意味するか。</summary>
    public static bool IsCancellation(VideoArtifactCommitResult commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        return commit.Status != VideoArtifactCommitStatus.Committed
            && !commit.RecoveryRequired
            && commit.Error is OperationCanceledException;
    }

    /// <summary>非 Committed な commit 結果を status へ写す。</summary>
    public static VideoGenerationStatus ToStatus(VideoArtifactCommitResult commit)
    {
        ArgumentNullException.ThrowIfNull(commit);

        if (commit.RecoveryRequired)
        {
            // rollback failure は manual recovery が必要な storage failure。cancel ではない。
            return VideoGenerationStatus.Failed;
        }

        if (IsCancellation(commit))
        {
            return VideoGenerationStatus.Cancelled;
        }

        return commit.Status switch
        {
            VideoArtifactCommitStatus.StagingMissing => VideoGenerationStatus.Failed,
            VideoArtifactCommitStatus.ProjectNotFound => VideoGenerationStatus.ProjectNotFound,
            VideoArtifactCommitStatus.SourceChanged => VideoGenerationStatus.SourceChanged,
            _ => VideoGenerationStatus.Failed,
        };
    }
}

/// <summary>Video 生成の結果。View が例外ではなくこの status で分岐できるようにする。</summary>
public enum VideoGenerationStatus
{
    Generated,
    ProjectNotFound,
    RecordingMissing,
    StepsMissing,
    SourceChanged,
    ComposerUnavailable,
    Failed,

    /// <summary>ユーザー操作で cancel された（failure ではない）。canonical / metadata は生成前のまま。</summary>
    Cancelled,
}

/// <summary>
/// <see cref="VideoGenerationCoordinator.GenerateAsync"/> の結果。
/// </summary>
/// <param name="RecoveryDirectory">
/// rollback に失敗し backup を保持している場合のみ、その transaction directory を示す。
/// 通常の failure では null。
/// </param>
public sealed record VideoGenerationResult(
    VideoGenerationStatus Status,
    string? CanonicalPath = null,
    string? ErrorMessage = null,
    Exception? Error = null,
    string? RecoveryDirectory = null);
