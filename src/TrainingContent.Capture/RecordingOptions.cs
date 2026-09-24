namespace TrainingContent.Capture;

/// <summary>
/// 録画オプション（F-02 録画デバイス設定の成果物）。
/// IRecordingEngine の実装に対してエンジン非依存の形で渡す。
/// </summary>
public sealed class RecordingOptions
{
    /// <summary>出力先（プロジェクトディレクトリ）。既定: raw/recording.mp4。</summary>
    public required string OutputFilePath { get; init; }

    /// <summary>フルフレームレート（既定 30fps）。</summary>
    public int FrameRate { get; init; } = 30;

    /// <summary>録画対象ディスプレイ。null で全デスクトップ（R-01: アプリを区別しない）。</summary>
    public DisplayDevice? Display { get; init; }

    /// <summary>システム音声の録音（R-02）。null で既定スピーカーの loopback。</summary>
    public AudioDevice? SystemAudioDevice { get; init; }

    /// <summary>マイク録音（R-03）。null で既定マイク。</summary>
    public AudioDevice? MicrophoneDevice { get; init; }

    /// <summary>
    /// 録画成功時の canonical 置換を caller に委ねる（two-phase finalize・統合メモ §7）。
    /// false（既定）では従来どおり StopAsync 完了時に staging → canonical へ置換する。
    /// true では StopAsync が staging パス（<see cref="RecordingResult.PendingCommit"/> = true）を
    /// 返し、caller が <see cref="ScreenRecorderRecordingEngine.CommitPendingRecording"/> か
    /// <see cref="ScreenRecorderRecordingEngine.AbortPendingRecording"/> を明示的に呼ぶまで
    /// canonical は一切変更されない。D 側の Recording Finalization Transaction
    /// （EventCapture finalize → StepBuilder → validation → project.json save → MP4 確定）用。
    /// </summary>
    public bool DeferredCommit { get; init; }
}

/// <summary>StopAsync の成果物。時間同期の基準を B（Event/Timeline）へ渡す。</summary>
public sealed class RecordingResult
{
    public required string FilePath { get; init; }

    /// <summary>
    /// 論理録画時間 = Master Session Clock から Pause 時間を除外した値
    /// （契約 §5.2 Canonical Timeline。RecordingInfo.DurationMs にそのまま入る）。
    /// </summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>録画開始の実時刻（tz 付き ISO 8601 で manifest 化する）。</summary>
    public required DateTimeOffset StartedAtUtc { get; init; }

    /// <summary>Pause していた区間（B が timeline から除外するために保持）。</summary>
    public IReadOnlyList<(TimeSpan Start, TimeSpan End)> PauseIntervals { get; init; } =
        Array.Empty<(TimeSpan, TimeSpan)>();

    /// <summary>
    /// DeferredCommit モードで録画に成功し、canonical への置換が確定待ちの場合 true。
    /// このとき <see cref="FilePath"/> は staging パス（正常な MP4 が存在する）であり、
    /// caller は <see cref="RecordingResult.PendingCommitPath"/>（canonical 予定地）への
    /// 置換を Commit / Abort で決定する。既定（即時確定）では false で FilePath は canonical。
    /// </summary>
    public bool PendingCommit { get; init; }

    /// <summary>DeferredCommit モードでの canonical 予定地（OutputFilePath）。確定済み / 通常モードでは FilePath と同じ。</summary>
    public string? PendingCommitPath { get; init; }
}
