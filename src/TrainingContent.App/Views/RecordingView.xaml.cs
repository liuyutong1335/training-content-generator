using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Capture;

namespace TrainingContent.App.Views;

/// <summary>
/// Recording。担当A の <see cref="IRecordingEngine"/>（<see cref="RecordingCoordinator"/> 経由）で
/// 画面録画を操作し、停止後に RecordingInfo を project.json へ保存する。
///
/// <para>
/// D5-A の範囲: Current Project → device 選択 → Start/Pause/Resume/Stop → raw/recording.mp4 →
/// RecordingInfo → project.json → Current Project 更新 → Home へ反映。
/// 担当B の EventCapture（canonical timeline 同期）は D5-B まで接続しない。
/// </para>
/// <para>
/// この View は ScreenRecorderLib を知らない。Engine の境界は <see cref="IRecordingEngine"/> のみ。
/// </para>
/// </summary>
public partial class RecordingView : UserControl
{
    /// <summary>
    /// 表示用の選択肢。Engine へは <see cref="Device"/> をそのまま渡す（再構築しない）。
    /// ToString を Label に揃えるのは、UIA の Name が record の既定表現にならないようにするため。
    /// </summary>
    private sealed record DisplayChoice(DisplayDevice? Device, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AudioChoice(AudioDevice? Device, string Label)
    {
        public override string ToString() => Label;
    }

    private const string AllDesktopsLabel = "全デスクトップ";
    private const string NoAudioLabel = "録音しない";

    private readonly RecordingCoordinator _coordinator;
    private readonly CurrentProjectContext _currentProject;

    private bool _devicesLoaded;
    private bool _devicesFailed;

    /// <summary>エラーなど、Navigation を伴わない Status 表示。</summary>
    public event EventHandler<string>? StatusChanged;

    public RecordingView(RecordingCoordinator coordinator, CurrentProjectContext currentProject)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(currentProject);

        InitializeComponent();

        _coordinator = coordinator;
        _currentProject = currentProject;

        _currentProject.CurrentProjectChanged += (_, _) => Render();

        // Engine の StateChanged は UI thread から来る保証がない（担当A 実装）。
        _coordinator.ActivityChanged += OnCoordinatorActivityChanged;

        Render();
    }

    private void OnCoordinatorActivityChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            Render();
            return;
        }

        Dispatcher.BeginInvoke(new Action(Render));
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
            RecordingPanel.Visibility = Visibility.Collapsed;
            return;
        }

        NoProjectPanel.Visibility = Visibility.Collapsed;
        RecordingPanel.Visibility = Visibility.Visible;

        ProjectTitleText.Text = project.Title;

        EnsureDevicesLoaded();
        RenderState();
    }

    private void RenderState()
    {
        var state = _coordinator.State;
        var busy = _coordinator.IsCommandRunning;
        var ready = _coordinator.IsCaptureReady;
        var faulted = _coordinator.HasEventCaptureFault;

        // Engine の Recording は実際の撮影開始より前に立つ。EventCapture が開始できた
        // （CaptureStarted → session.Start 完了）までは「録画準備中」として区別する。
        StateText.Text = state switch
        {
            RecordingState.Idle => "待機中",
            RecordingState.Recording => ready ? "録画中" : "録画準備中",
            RecordingState.Paused => "一時停止中",
            RecordingState.Stopping => "停止処理中",
            RecordingState.Failed => "エラー",
            _ => "不明",
        };

        if (state == RecordingState.Failed)
        {
            // Engine Failed は recovery 不可（既存方針: App 再起動）なので EventCapture fault より優先する。
            // 直前の fault message が残っていても上書きする。
            ShowMessage("録画エンジンでエラーが発生しました。アプリを再起動して再試行してください。");
        }
        else if (faulted)
        {
            // EventCapture の失敗はユーザーが確認できる必要がある（録画はまだ Stop できる）。
            ShowMessage(_coordinator.EventCaptureFaultMessage ?? "操作記録の取得に失敗しました。");
        }

        var idle = state == RecordingState.Idle && !busy;

        // device を列挙できていない間は開始させない（録画対象の選択が成立しないため）。
        StartButton.IsEnabled = idle && !_devicesFailed;

        // 準備中に Pause すると A/B の Canonical Timeline が壊れるため、撮影開始後だけ許可する。
        var canOperate = ready && !faulted && !busy;
        PauseButton.IsEnabled = state == RecordingState.Recording && canOperate;
        ResumeButton.IsEnabled = state == RecordingState.Paused && canOperate;

        // EventCapture が失敗して Engine だけ Recording / Paused に残った場合も、
        // ユーザーが終了できるよう Stop は残す。
        StopButton.IsEnabled =
            state is RecordingState.Recording or RecordingState.Paused && !busy && (ready || faulted);

        // 録画中は device を変更させない（選択と実際の録音対象が食い違わないように）。
        var devicesEnabled = idle && !_devicesFailed;
        DisplayComboBox.IsEnabled = devicesEnabled;
        SystemAudioComboBox.IsEnabled = devicesEnabled;
        MicrophoneComboBox.IsEnabled = devicesEnabled;

        // Refresh は失敗状態でも押せるようにする（再試行の唯一の手段を残す）。
        RefreshDevicesButton.IsEnabled = idle;
    }

    // ---------------------------------------------------------------------
    // Device enumeration
    // ---------------------------------------------------------------------

    private void EnsureDevicesLoaded()
    {
        if (_devicesLoaded)
        {
            return;
        }

        LoadDevices();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        LoadDevices();
        RenderState();
    }

    /// <summary>
    /// 3 種類の device を列挙して ComboBox を作る。
    /// 失敗しても App を落とさず、device を使えない旨を表示する（§11）。
    /// </summary>
    private void LoadDevices()
    {
        try
        {
            var displays = _coordinator.GetDisplays();
            var systemAudioDevices = _coordinator.GetSystemAudioDevices();
            var microphones = _coordinator.GetMicrophones();

            // Display: 先頭は「全デスクトップ」= null（§12）。主ディスプレイの推測はしない。
            var displayChoices = new List<DisplayChoice> { new(null, AllDesktopsLabel) };
            displayChoices.AddRange(displays.Select(d => new DisplayChoice(d, DeviceLabel(d.DeviceName, d.DeviceId))));
            DisplayComboBox.ItemsSource = displayChoices;
            DisplayComboBox.SelectedIndex = 0;

            // System Audio / Microphone: 先頭は「録音しない」= null（§13）。
            // null は現実装では「その音源を録音しない」を意味する。
            var systemAudioChoices = new List<AudioChoice> { new(null, NoAudioLabel) };
            systemAudioChoices.AddRange(systemAudioDevices.Select(d => new AudioChoice(d, DeviceLabel(d.DeviceName, d.DeviceId))));
            SystemAudioComboBox.ItemsSource = systemAudioChoices;

            var microphoneChoices = new List<AudioChoice> { new(null, NoAudioLabel) };
            microphoneChoices.AddRange(microphones.Select(d => new AudioChoice(d, DeviceLabel(d.DeviceName, d.DeviceId))));
            MicrophoneComboBox.ItemsSource = microphoneChoices;

            // 実機での確認を容易にするため、device があれば最初の実 device を初期選択する（§14）。
            SystemAudioComboBox.SelectedIndex = systemAudioChoices.Count > 1 ? 1 : 0;
            MicrophoneComboBox.SelectedIndex = microphoneChoices.Count > 1 ? 1 : 0;

            _devicesLoaded = true;
            _devicesFailed = false;
            DevicesErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Trace.TraceError("RecordingView: device の列挙に失敗しました — {0}", ex);

            _devicesLoaded = false;
            _devicesFailed = true;

            DisplayComboBox.ItemsSource = null;
            SystemAudioComboBox.ItemsSource = null;
            MicrophoneComboBox.ItemsSource = null;

            DevicesErrorText.Text = "デバイス情報の取得に失敗しました。";
            DevicesErrorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 表示用ラベル。<c>GetDisplays()</c> の FriendlyName は v6.6.0 では空になることがあるため、
    /// その場合は DeviceId を表示する（Engine へ渡す instance 自体は変更しない）。
    /// </summary>
    private static string DeviceLabel(string? friendlyName, string deviceId) =>
        string.IsNullOrWhiteSpace(friendlyName) ? deviceId : friendlyName;

    private DisplayDevice? SelectedDisplay => (DisplayComboBox.SelectedItem as DisplayChoice)?.Device;

    private AudioDevice? SelectedSystemAudioDevice => (SystemAudioComboBox.SelectedItem as AudioChoice)?.Device;

    private AudioDevice? SelectedMicrophoneDevice => (MicrophoneComboBox.SelectedItem as AudioChoice)?.Device;

    // ---------------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------------

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        var project = _currentProject.CurrentProject;
        if (project is null)
        {
            return;
        }

        // 既存録画がある場合は上書き確認（§8）。既存 MP4 はこの時点では削除しない。
        if (project.Recording is not null)
        {
            var answer = MessageBox.Show(
                Window.GetWindow(this),
                "このプロジェクトには既存の録画があります。新しい録画で上書きしますか？",
                "録画の上書き確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        ShowMessage(string.Empty);
        SetStatus("録画を開始しています...");

        var result = await _coordinator.StartAsync(
            SelectedDisplay,
            SelectedSystemAudioDevice,
            SelectedMicrophoneDevice);

        if (!result.Succeeded)
        {
            ShowMessage(result.ErrorMessage ?? "録画を開始できませんでした。");
            SetStatus("録画を開始できませんでした。");
            return;
        }

        ShowMessage(string.Empty);
        SetStatus("録画を開始しました。");
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        var result = await _coordinator.PauseAsync();

        if (!result.Succeeded)
        {
            ShowMessage(result.ErrorMessage ?? "一時停止できませんでした。");
            return;
        }

        ShowMessage(string.Empty);
        SetStatus("録画を一時停止しました。");
    }

    private async void Resume_Click(object sender, RoutedEventArgs e)
    {
        var result = await _coordinator.ResumeAsync();

        if (!result.Succeeded)
        {
            ShowMessage(result.ErrorMessage ?? "再開できませんでした。");
            return;
        }

        ShowMessage(string.Empty);
        SetStatus("録画を再開しました。");
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("録画を停止しています...");

        var outcome = await _coordinator.StopAsync();

        switch (outcome.Status)
        {
            case RecordingStopStatus.Saved:
                ShowMessage(string.Empty);
                SetStatus("録画を保存しました。");
                break;

            case RecordingStopStatus.SaveFailed:
                ShowMessage(outcome.ErrorMessage ?? "プロジェクト情報の保存に失敗しました。");
                SetStatus("録画結果の保存に失敗しました。");
                break;

            case RecordingStopStatus.EngineFailed:
                ShowMessage(outcome.ErrorMessage ?? "録画エンジンでエラーが発生しました。");
                SetStatus("録画の停止に失敗しました。");
                break;

            case RecordingStopStatus.EventCaptureFailed:
                // 操作記録が壊れた recording は保存しない（MP4 等の artifact は残す）。
                ShowMessage(outcome.ErrorMessage ?? "操作記録の取得に失敗しました。");
                SetStatus("録画は保存されませんでした。");
                break;

            default:
                ShowMessage(outcome.ErrorMessage ?? "録画を停止できませんでした。");
                break;
        }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStatus(string message) => StatusChanged?.Invoke(this, message);
}
