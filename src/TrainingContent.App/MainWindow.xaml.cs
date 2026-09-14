using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrainingContent.App.Views;

namespace TrainingContent.App;

/// <summary>
/// Application Shell。D1 では「表示中ページの切替」のみを担う。
/// 業務ロジック（ProjectStore / Recording / Review 等）はここに置かない。
/// Core Model にも依存しない。
/// </summary>
public partial class MainWindow : Window
{
    // View インスタンスは 1 回だけ生成して使い回す（切替のたびに作り直さない）。
    private readonly HomeView _homeView = new();
    private readonly RecordingView _recordingView = new();
    private readonly ReviewView _reviewView = new();
    private readonly ContentsView _contentsView = new();

    public MainWindow()
    {
        InitializeComponent();
        ShowPage(NavHomeButton, _homeView);
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavHomeButton, _homeView);

    private void NavRecording_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavRecordingButton, _recordingView);

    private void NavReview_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavReviewButton, _reviewView);

    private void NavContents_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavContentsButton, _contentsView);

    /// <summary>メイン領域の View を差し替え、選択中ナビ項目とステータスを更新する。</summary>
    private void ShowPage(Button navButton, UserControl view)
    {
        PageHost.Content = view;

        var pageName = navButton.Content?.ToString() ?? string.Empty;
        StatusText.Text = $"{pageName} を表示中";

        SetActiveNav(navButton);
    }

    /// <summary>選択中のナビ項目だけを強調表示にする。</summary>
    private void SetActiveNav(Button active)
    {
        var navButtons = new[] { NavHomeButton, NavRecordingButton, NavReviewButton, NavContentsButton };
        var activeBrush = (Brush)FindResource("NavButtonActiveBrush");

        foreach (var button in navButtons)
        {
            var isActive = ReferenceEquals(button, active);
            button.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            button.Background = isActive ? activeBrush : Brushes.Transparent;
        }
    }
}
