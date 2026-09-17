using System.Diagnostics;
using System.IO;
using TrainingContent.App.State;
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
    /// </summary>
    public async Task<VideoGenerationResult> GenerateAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await _projectStore.LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(true);
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
                    options: null,
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

                return new VideoGenerationResult(
                    ToStatus(commit.Status),
                    ErrorMessage: commit.ErrorMessage,
                    RecoveryDirectory: commit.RecoveryRequired ? commit.RecoveryDirectory : null);
            }

            // 生成対象が Current Project のときだけ差し替える（別 Project の生成で巻き戻さない）。
            if (commit.Project is { } updated && _currentProject.IsCurrent(projectId))
            {
                _currentProject.SetCurrent(updated);
            }

            return new VideoGenerationResult(
                VideoGenerationStatus.Generated,
                CanonicalPath: commit.CanonicalPath);
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

    private static VideoGenerationStatus ToStatus(VideoArtifactCommitStatus status) => status switch
    {
        VideoArtifactCommitStatus.StagingMissing => VideoGenerationStatus.Failed,
        VideoArtifactCommitStatus.ProjectNotFound => VideoGenerationStatus.ProjectNotFound,
        VideoArtifactCommitStatus.SourceChanged => VideoGenerationStatus.SourceChanged,
        _ => VideoGenerationStatus.Failed,
    };
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
