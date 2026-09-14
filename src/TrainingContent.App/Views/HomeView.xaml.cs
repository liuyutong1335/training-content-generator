using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;

namespace TrainingContent.App.Views;

/// <summary>
/// Home。Current Project の概要（Project Overview）を表示する。
///
/// <para>
/// D4 では Project を作る / 開く / 現在の Project を知る、までが範囲。
/// Recording(D5) と Review(D6) の起動導線はここに置かない。
/// </para>
/// </summary>
public partial class HomeView : UserControl
{
    private readonly CurrentProjectContext _currentProject;
    private readonly ProjectWorkspace _workspace;

    /// <summary>エラーなど、Navigation を伴わない Status 表示。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>Project が作成・オープンされ、Home 表示へ切り替えるべきとき。</summary>
    public event EventHandler<string>? ProjectActivated;

    public HomeView(CurrentProjectContext currentProject, ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(currentProject);
        ArgumentNullException.ThrowIfNull(workspace);

        InitializeComponent();

        _currentProject = currentProject;
        _workspace = workspace;

        // Create / Open / Rename / Delete のいずれでも Home 表示が追随する。
        _currentProject.CurrentProjectChanged += (_, _) => Render();
        Render();
    }

    // ---------------------------------------------------------------------
    // New Project
    // ---------------------------------------------------------------------

    private async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new NewProjectWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() != true || dialog.Input is null)
        {
            return; // Cancel
        }

        try
        {
            var project = await _workspace.CreateProjectAsync(dialog.Input);
            ProjectActivated?.Invoke(this, $"「{project.Title}」を作成しました。");
        }
        catch (Exception ex)
        {
            // 詳細は通常 UI に出さない。Debug 用に Trace へ残す。
            Trace.TraceError("HomeView: Project の作成に失敗しました — {0}", ex);

            StatusChanged?.Invoke(this, "プロジェクトの作成に失敗しました。");
            MessageBox.Show(
                Window.GetWindow(this),
                "プロジェクトの作成に失敗しました。",
                "エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    // ---------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------

    private void Render()
    {
        var project = _currentProject.CurrentProject;

        if (project is null)
        {
            NoProjectPanel.Visibility = Visibility.Visible;
            ProjectPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NoProjectPanel.Visibility = Visibility.Collapsed;
        ProjectPanel.Visibility = Visibility.Visible;

        TitleText.Text = project.Title;

        SetOptionalRow(ObjectiveRow, ObjectiveText, project.Objective);
        SetOptionalRow(TargetAudienceRow, TargetAudienceText, project.TargetAudience);
        SetOptionalRow(PrerequisitesRow, PrerequisitesText, FormatPrerequisites(project.Prerequisites));

        StepCountText.Text = project.Steps.Count.ToString(CultureInfo.InvariantCulture);
        RecordingText.Text = project.Recording is null ? "未録画" : "録画済み";
        UpdatedText.Text = project.UpdatedAtUtc.LocalDateTime.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>値が無い項目は行ごと隠す（空ラベルだけが残らないように）。</summary>
    private static void SetOptionalRow(FrameworkElement row, TextBlock value, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            row.Visibility = Visibility.Collapsed;
            return;
        }

        row.Visibility = Visibility.Visible;
        value.Text = text;
    }

    private static string? FormatPrerequisites(IReadOnlyList<string> prerequisites) =>
        prerequisites.Count == 0
            ? null
            : string.Join(Environment.NewLine, prerequisites.Select(item => $"・{item}"));
}
