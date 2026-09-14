using System.Windows;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Storage;

namespace TrainingContent.App;

/// <summary>
/// Composition Root。ProjectStore / CurrentProjectContext / ProjectWorkspace を
/// ここで 1 instance ずつ生成し、MainWindow 経由で必要な View へ渡す。
/// View 側で new したり、Projects Root のパスを再定義したりしない。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Production の Projects Root 決定は Storage 側の責任（%LOCALAPPDATA%\TrainingContentGenerator\projects）。
        var projectStore = new ProjectStore(ProjectStore.DefaultProjectsRoot);

        // Current Project は Application Session 内だけの状態。永続化も自動復元もしない。
        var currentProject = new CurrentProjectContext();

        var workspace = new ProjectWorkspace(projectStore, currentProject);

        var window = new MainWindow(projectStore, currentProject, workspace);
        MainWindow = window;
        window.Show();
    }
}
