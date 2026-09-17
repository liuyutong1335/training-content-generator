using System.Windows;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Capture;
using TrainingContent.Storage;
using TrainingContent.Video.Renderer;

namespace TrainingContent.App;

/// <summary>
/// Composition Root。ProjectStore / CurrentProjectContext / ProjectWorkspace / IRecordingEngine /
/// RecordingCoordinator をここで 1 instance ずつ生成し、MainWindow 経由で必要な View へ渡す。
/// View 側で new したり、Projects Root のパスを再定義したりしない。
/// </summary>
public partial class App : Application
{
    private ScreenRecorderRecordingEngine? _recordingEngine;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Production の Projects Root 決定は Storage 側の責任（%LOCALAPPDATA%\TrainingContentGenerator\projects）。
        var projectStore = new ProjectStore(ProjectStore.DefaultProjectsRoot);

        // Current Project は Application Session 内だけの状態。永続化も自動復元もしない。
        var currentProject = new CurrentProjectContext();

        var workspace = new ProjectWorkspace(projectStore, currentProject);

        // 録画 Engine は App lifetime で 1 instance だけ生成する（IDisposable の所有はこの Root）。
        // App から ScreenRecorderLib を参照しない。境界は IRecordingEngine のみ。
        _recordingEngine = new ScreenRecorderRecordingEngine();
        var recordingCoordinator = new RecordingCoordinator(_recordingEngine, projectStore, currentProject);

        // Video 生成 backend。transaction（staging / backup）は Storage 側が所有する。
        var videoTransaction = new VideoArtifactTransaction(projectStore);

        // composer は factory として渡すだけにし、ここでは生成しない。
        // FfmpegVideoRenderer は生成時に ffmpeg.exe を探索して不在なら例外を投げるため、
        // startup で実体を作ると FFmpeg 未導入の環境でアプリが起動できなくなる。
        var videoGenerationCoordinator = new VideoGenerationCoordinator(
            projectStore,
            currentProject,
            videoTransaction,
            () => new FfmpegVideoRenderer());

        var window = new MainWindow(
            projectStore, currentProject, workspace, recordingCoordinator, videoGenerationCoordinator);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _recordingEngine?.Dispose();
        _recordingEngine = null;

        base.OnExit(e);
    }
}
