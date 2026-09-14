using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using TrainingContent.Capture;

namespace RecordingSpikeGui;

public partial class MainWindow : Window
{
    private readonly ScreenRecorderRecordingEngine _engine = new();
    private RecordingOptions? _currentOptions;
    private RecordingResult? _lastResult;

    public MainWindow()
    {
        InitializeComponent();
        LoadDevices();
    }

    private void LoadDevices()
    {
        MicBox.ItemsSource = _engine.GetMicrophones().Select(d => d.DeviceName).ToList();
        MicBox.SelectedIndex = 0;
        SysBox.ItemsSource = _engine.GetSystemAudioDevices().Select(d => d.DeviceName).ToList();
        SysBox.SelectedIndex = 0;
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            Log.AppendText(message + Environment.NewLine);
            Log.ScrollToEnd();
        });
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"{DateTime.Now:yyyyMMdd-HHmmss}.mp4",
            Filter = "MP4 動画 (*.mp4)|*.mp4",
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // ComboBox は表示名（FriendlyName）一覧なので、対応する DeviceId を引く
        var mic = UseMic.IsChecked == true
            ? _engine.GetMicrophones().FirstOrDefault(d => d.DeviceName == (MicBox.SelectedItem as string ?? ""))
            : null;
        var sys = UseSys.IsChecked == true
            ? _engine.GetSystemAudioDevices().FirstOrDefault(d => d.DeviceName == (SysBox.SelectedItem as string ?? ""))
            : null;
        if (UseMic.IsChecked == true && mic is null)
        {
            MessageBox.Show(this, "選択されたマイクが見つかりません", "録画テスト", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _currentOptions = new RecordingOptions
        {
            OutputFilePath = dialog.FileName,
            FrameRate = 30,
            MicrophoneDevice = mic,
            SystemAudioDevice = sys,
        };

        try
        {
            await _engine.StartAsync(_currentOptions);
            StatusText.Text = "録画中…";
            AppendLog($"[開始] mic={(mic?.DeviceName ?? "なし")} / sys={(sys?.DeviceName ?? "なし")}");
            AppendLog("       （録画中に他アプリを操作してアプリ切替も確認できます）");
            StartBtn.IsEnabled = false;
            PauseBtn.IsEnabled = true;
            ResumeBtn.IsEnabled = false;
            StopBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] 開始に失敗: {ex.Message}");
            StatusText.Text = "エラー";
        }
    }

    private async void OnPause(object sender, RoutedEventArgs e)
    {
        await _engine.PauseAsync();
        StatusText.Text = "一時停止中";
        AppendLog("[一時停止]");
        PauseBtn.IsEnabled = false;
        ResumeBtn.IsEnabled = true;
    }

    private async void OnResume(object sender, RoutedEventArgs e)
    {
        await _engine.ResumeAsync();
        StatusText.Text = "録画中…";
        AppendLog("[再開]");
        ResumeBtn.IsEnabled = false;
        PauseBtn.IsEnabled = true;
    }

    private async void OnStop(object sender, RoutedEventArgs e)
    {
        try
        {
            var result = await _engine.StopAsync();
            _lastResult = result;
            StatusText.Text = "待機中";
            AppendLog("[停止]");
            AppendLog($"  file    : {result.FilePath}");
            AppendLog($"  duration: {result.Duration.TotalMilliseconds:F0} ms（Pause を含まない）");
            AppendLog($"  pause   : {string.Join(", ", result.PauseIntervals.Select(p => $"{p.Start.TotalSeconds:F1}-{p.End.TotalSeconds:F1}s"))}");
            AppendLog("  → プレーヤーで開き、音声・シーク・音ズレを確認してください");
            StartBtn.IsEnabled = true;
            PauseBtn.IsEnabled = false;
            ResumeBtn.IsEnabled = false;
            StopBtn.IsEnabled = false;
        }
        catch (Exception ex)
        {
            AppendLog($"[エラー] 停止に失敗: {ex.Message}");
            StatusText.Text = "エラー";
            StartBtn.IsEnabled = true;
            PauseBtn.IsEnabled = false;
            ResumeBtn.IsEnabled = false;
            StopBtn.IsEnabled = false;
        }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var path = _lastResult?.FilePath is { } f ? Path.GetDirectoryName(f) : AppDomain.CurrentDomain.BaseDirectory;
        if (Directory.Exists(path))
        {
            Process.Start("explorer.exe", path);
        }
    }
}
