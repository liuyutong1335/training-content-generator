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

    /// <summary>録画を開始する（R-01/R-02/R-03: 画面＋システム音声＋マイク）。開始が完了したら戻る。</summary>
    Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default);

    Task PauseAsync(CancellationToken cancellationToken = default);

    Task ResumeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// 録画を停止し、Recording を確定する（契約 §7: RecordingInfo Contract）。
    /// 録画完了時に RecordingResult を返す。
    /// </summary>
    Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default);

    /// <summary>録画状態の変化・失敗を通知する（UI はこのイベントのみ購読する）。</summary>
    event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// 実際の撮影（WGC 初期化完了・エンコーダー動作開始）が始まった瞬間に 1 回だけ発火する
    /// （統合メモ §1: Canonical Timeline の 0ms 基準点）。B の EventCapture Session や
    /// MasterClock は <c>StateChanged(Recording)</c> ではなく本イベントで開始すること
    /// （StartAsync 呼び出しから撮影開始まで ~2 秒かかるため）。
    /// </summary>
    event EventHandler? CaptureStarted;

    /// <summary>
    /// DeferredCommit モード（<see cref="RecordingOptions.DeferredCommit"/>）で確定待ちの録画を
    /// canonical へ確定する（staging → canonical の Move）。two-phase finalize の Commit 相当。
    /// 失敗時は staging が保持され、例外で通知される。通常モードでは呼べない。
    /// </summary>
    RecordingResult CommitPendingRecording();

    /// <summary>
    /// DeferredCommit モードで確定待ちの録画を破棄する（staging 削除のみ・canonical は無変更）。
    /// two-phase finalize の Abort 相当。通常モードでは呼べない。
    /// </summary>
    void AbortPendingRecording();
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
