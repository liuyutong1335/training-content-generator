using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using TrainingContent.App.State;
using TrainingContent.Core.Models;

namespace TrainingContent.App.Views;

/// <summary>
/// 手順確認（Review）。Current Project の Steps を <b>読み取り専用</b>で表示する。
///
/// <para>
/// 現在は foundation のみ。Step の編集・削除・並べ替え・手動追加・Screenshot 差し替え・
/// Redaction・保存は持たない。editable property への TwoWay binding も Save path も存在しないため、
/// canonical な <see cref="TrainingProject"/> を UI から mutation しない。
/// 編集は後続の B1 で detached draft と共に導入する。
/// </para>
/// <para>
/// 意図的に持たないもの: ProjectStore / 独自の path resolver / Step の再採番 /
/// 独自の永続化 / MVVM framework。
/// </para>
/// </summary>
public partial class ReviewView : UserControl
{
    private readonly CurrentProjectContext _currentProject;

    public ReviewView(CurrentProjectContext currentProject)
    {
        ArgumentNullException.ThrowIfNull(currentProject);

        InitializeComponent();

        _currentProject = currentProject;

        // 別 Project が Current になったら表示を置き換える（旧 Project / Step を保持しない）。
        _currentProject.CurrentProjectChanged += (_, _) => Render();
        Render();
    }

    // ---------------------------------------------------------------------
    // Rendering
    // ---------------------------------------------------------------------

    private void Render()
    {
        var project = _currentProject.CurrentProject;

        if (project is null)
        {
            ShowPanel(NoProjectPanel);
            ClearSelection();
            return;
        }

        if (project.Steps.Count == 0)
        {
            ShowPanel(NoStepsPanel);
            ClearSelection();
            return;
        }

        ShowPanel(ReviewPanel);

        // 表示順は Order 昇順。TrainingStep.Order は保持値のまま使う（UI 側で再採番しない）。
        // OrderBy は新しい sequence を作るだけで、TrainingStep instance は変更しない。
        StepListBox.ItemsSource = project.Steps.OrderBy(step => step.Order).ToList();

        // Project が変わったら旧 Selection を引き継がず、先頭 Step を初期選択する。
        StepListBox.SelectedIndex = 0;
    }

    private void ShowPanel(FrameworkElement panel)
    {
        NoProjectPanel.Visibility = Visibility.Collapsed;
        NoStepsPanel.Visibility = Visibility.Collapsed;
        ReviewPanel.Visibility = Visibility.Collapsed;

        panel.Visibility = Visibility.Visible;
    }

    /// <summary>選択と詳細を破棄する。旧 Project の <see cref="TrainingStep"/> reference を残さない。</summary>
    private void ClearSelection()
    {
        StepListBox.ItemsSource = null;
        StepListBox.SelectedItem = null;
        ClearDetail();
    }

    private void StepListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StepListBox.SelectedItem is not TrainingStep step)
        {
            ClearDetail();
            return;
        }

        ShowDetail(step);
    }

    // ---------------------------------------------------------------------
    // Detail（値はすべて code-behind から設定する。binding は使わない）
    // ---------------------------------------------------------------------

    private void ShowDetail(TrainingStep step)
    {
        OrderText.Text = step.Order.ToString(CultureInfo.InvariantCulture);
        TitleText.Text = step.Title;
        ActionText.Text = step.Action;
        TargetText.Text = OrDash(step.Target);

        DescriptionText.Text = OrDash(step.Description);
        CautionText.Text = OrDash(step.Caution);
        ExpectedResultText.Text = OrDash(step.ExpectedResult);

        StartMsText.Text = $"{step.StartMs} ms";
        EndMsText.Text = step.EndMs is { } end ? $"{end} ms" : "—";

        ShowScreenshot(step.ScreenshotPath);
    }

    private void ClearDetail()
    {
        OrderText.Text = string.Empty;
        TitleText.Text = string.Empty;
        ActionText.Text = string.Empty;
        TargetText.Text = string.Empty;

        DescriptionText.Text = string.Empty;
        CautionText.Text = string.Empty;
        ExpectedResultText.Text = string.Empty;

        StartMsText.Text = string.Empty;
        EndMsText.Text = string.Empty;

        ScreenshotPlaceholder.Text = string.Empty;
        ScreenshotPathText.Text = string.Empty;
        ScreenshotPathText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// スクリーンショットは <b>placeholder のみ</b>。
    ///
    /// <para>
    /// Project 相対 path を絶対 path へ解決する read-only boundary が Storage にまだ無く、
    /// この View に独自 resolver を作らない方針のため、実画像はロードしない。
    /// path の有無と値の表示に留める（実画像表示は resolver API の追加後に検討する）。
    /// </para>
    /// </summary>
    private void ShowScreenshot(string? screenshotPath)
    {
        if (string.IsNullOrWhiteSpace(screenshotPath))
        {
            ScreenshotPlaceholder.Text = "スクリーンショットなし";
            ScreenshotPathText.Text = string.Empty;
            ScreenshotPathText.Visibility = Visibility.Collapsed;
            return;
        }

        ScreenshotPlaceholder.Text = "画像プレビューは未対応です";
        ScreenshotPathText.Text = screenshotPath;
        ScreenshotPathText.Visibility = Visibility.Visible;
    }

    private static string OrDash(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "—" : value;
}
