namespace TrainingContent.Capture;

/// <summary>
/// 録画エンジンの抽象化（開発計画書 §16）。
/// ScreenRecorderLib を UI / Core から直接参照させないための境界。
/// 将来の別バックエンド（WGC 直接実装等）への交換可能性を保証する。
/// </summary>
public interface IRecordingEngine
{
    IReadOnlyList<DisplayDevice> GetDisplays();

    IReadOnlyList<AudioDevice> GetMicrophones();

    IReadOnlyList<AudioDevice> GetSystemAudioDevices();

    /// <summary>録画を開始する（R-01/R-02/R-03: 画面＋システム音声＋マイク）。</summary>
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 録画を停止し、Recording を確定する（計画書 §11: raw/recording.mp4 + manifest）。
    /// </summary>
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>録画状態の変化・失敗を通知する（UI はこのイベントのみ購読する）。</summary>
    event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
}

public enum RecordingState
{
    Idle,
    Recording,
    Paused,
    Stopping,
    Failed,
}

public sealed class RecordingStateChangedEventArgs : EventArgs
{
    public required RecordingState State { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>録画デバイス（ディスプレイ）。DeviceId は RecordingInfo.DisplayId へ保存する（契約 §7 Device Rule）。</summary>
public sealed record DisplayDevice(string DeviceId, string DeviceName, bool IsPrimary);

/// <summary>録音デバイス。Source でマイク.capture / スピーカー.loopback を区別する。再識別は DeviceId 優先。</summary>
public sealed record AudioDevice(string DeviceId, string DeviceName, AudioDeviceSource Source);

public enum AudioDeviceSource
{
    /// <summary>マイク等の入力キャプチャ（WASAPI Capture）。</summary>
    Capture,

    /// <summary>スピーカーで再生中の音声（WASAPI Loopback）。</summary>
    Loopback,
}
