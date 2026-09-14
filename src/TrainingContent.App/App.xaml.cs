using System.Windows;
using TrainingContent.Storage;

namespace TrainingContent.App;

/// <summary>
/// Composition Root。ProjectStore はここで 1 instance だけ生成し、View へ渡す。
/// View 側で new したり、Projects Root のパスを再定義したりしない。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Production の Projects Root 決定は Storage 側の責任（%LOCALAPPDATA%\TrainingContentGenerator\projects）。
        var projectStore = new ProjectStore(ProjectStore.DefaultProjectsRoot);

        var window = new MainWindow(projectStore);
        MainWindow = window;
        window.Show();
    }
}
