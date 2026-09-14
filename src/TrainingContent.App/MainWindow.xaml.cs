using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrainingContent.App.Views;
using TrainingContent.Storage;

namespace TrainingContent.App;

/// <summary>
/// Application Shell。責任は Navigation と View composition のみ。
/// Project 一覧の業務ロジックはここへ置かない（ContentsView の責任）。
/// </summary>
public partial class MainWindow : Window
{
    // View インスタンスは 1 回だけ生成して使い回す（切替のたびに作り直さない）。
    private readonly HomeView _homeView = new();
    private readonly RecordingView _recordingView = new();
    private readonly ReviewView _reviewView = new();
    private readonly ContentsView _contentsView;

    public MainWindow(ProjectStore projectStore)
    {
        ArgumentNullException.ThrowIfNull(projectStore);

        InitializeComponent();

        // ProjectStore は App から受け取る。ここで new したり Path を組み立てたりしない。
        _contentsView = new ContentsView(projectStore);
        _contentsView.StatusChanged += OnContentsStatusChanged;

        ShowPage(NavHomeButton, _homeView);
    }

    private void NavHome_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavHomeButton, _homeView);

    private void NavRecording_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavRecordingButton, _recordingView);

    private void NavReview_Click(object sender, RoutedEventArgs e) =>
        ShowPage(NavReviewButton, _reviewView);

    private async void NavContents_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(NavContentsButton, _contentsView);

        // 初回表示時に一度だけ読み込む。2 回目以降の navigation では再走査しない。
        await _contentsView.EnsureLoadedAsync();
    }

    private void OnContentsStatusChanged(object? sender, string message) =>
        StatusText.Text = message;

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
