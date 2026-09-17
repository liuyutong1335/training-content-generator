using System.Diagnostics;
using ScreenRecorderLib;

namespace TrainingContent.Capture;

/// <summary>
/// ScreenRecorderLib を用いた IRecordingEngine の実装（開発計画書 §16）。
/// v6.6.0 の実 API に対応（Spike A で v7.0.1 との比較を行い、差分があればここに集約する）。
/// </summary>
public sealed class ScreenRecorderRecordingEngine : IRecordingEngine, IDisposable
{
    private Recorder? _recorder;
    private RecordingOptions? _currentOptions;
    private DateTimeOffset _startedAtUtc;
    private readonly Stopwatch _clock = Stopwatch.StartNew(); // Master Session Clock（NessStudio RecordAssist 相当）
    private readonly List<(TimeSpan Start, TimeSpan End)> _pauseIntervals = [];
    private TimeSpan? _pauseStartedAt;
    private RecordingState _state = RecordingState.Idle;
    private bool _captureStarted; // 実際の撮影開始（RecorderStatus.Recording）を検出したか
    private TaskCompletionSource<RecordingResult>? _completionSource;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    public event EventHandler? CaptureStarted;

    public RecordingState State
    {
        get => _state;
        private set
        {
            _state = value;
            StateChanged?.Invoke(this, new RecordingStateChangedEventArgs { State = value });
        }
    }

    public IReadOnlyList<DisplayDevice> GetDisplays()
    {
        // DeviceId は契約 §7 により RecordingInfo.DisplayId へ保存される識別子。
        // v6.6.0 には IsPrimary の公開 API がないため TODO(Spike A) で補完する。
        return Recorder.GetDisplays()
            .Select(d => new DisplayDevice(d.DeviceName, d.FriendlyName, IsPrimary: false))
            .ToList();
    }

    public IReadOnlyList<AudioDevice> GetMicrophones()
    {
        return Recorder.GetSystemAudioDevices(ScreenRecorderLib.AudioDeviceSource.InputDevices)
            .Select(d => new AudioDevice(d.DeviceName, d.FriendlyName, AudioDeviceSource.Capture))
            .ToList();
    }

    public IReadOnlyList<AudioDevice> GetSystemAudioDevices()
    {
        return Recorder.GetSystemAudioDevices(ScreenRecorderLib.AudioDeviceSource.OutputDevices)
            .Select(d => new AudioDevice(d.DeviceName, d.FriendlyName, AudioDeviceSource.Loopback))
            .ToList();
    }

    public Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        if (State is RecordingState.Recording or RecordingState.Paused)
        {
            throw new InvalidOperationException("録画セッションは既に開始されています");
        }

        _currentOptions = options ?? throw new ArgumentNullException(nameof(options));
        _pauseIntervals.Clear();
        _pauseStartedAt = null;

        var recorderOptions = MapToLibOptions(options);
        _recorder = ScreenRecorderLib.Recorder.CreateRecorder(recorderOptions);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
        _recorder.OnStatusChanged += OnStatusChanged;

        _completionSource = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recorder.Record(options.OutputFilePath);
        _startedAtUtc = DateTimeOffset.UtcNow;
        _captureStarted = false; // OnStatusChanged で Recording になった瞬間（=実際の撮影開始）に時計を合わせる
        State = RecordingState.Recording;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        EnsureRecording();
        _recorder!.Pause();
        _pauseStartedAt = _clock.Elapsed;
        State = RecordingState.Paused;
        return Task.CompletedTask;
    }

    public Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        if (State != RecordingState.Paused)
        {
            throw new InvalidOperationException("一時停止中のセッションではありません");
        }

        _recorder!.Resume();
        _pauseIntervals.Add((_pauseStartedAt!.Value, _clock.Elapsed));
        _pauseStartedAt = null;
        State = RecordingState.Recording;
        return Task.CompletedTask;
    }

    public Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureRecording();
        if (_pauseStartedAt is not null)
        {
            // Pause 中に Stop された場合も区間として閉じる
            _pauseIntervals.Add((_pauseStartedAt.Value, _clock.Elapsed));
            _pauseStartedAt = null;
        }

        State = RecordingState.Stopping;
        _recorder!.Stop();
        // OnRecordingComplete / OnRecordingFailed で完了する
        return _completionSource!.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Pause 中の時間を除外した論理 Duration を返す（契約 §5.2:
    /// Canonical Timeline に Pause を含めない）。
    /// </summary>
    private TimeSpan CanonicalDuration()
    {
        var elapsed = _clock.Elapsed;
        var paused = TimeSpan.FromTicks(_pauseIntervals.Sum(i => (i.End - i.Start).Ticks));
        if (_pauseStartedAt is { } openPause)
        {
            paused += elapsed - openPause;
        }
        return elapsed - paused;
    }

    private RecordingResult BuildResult(string filePath)
    {
        // CaptureStarted 前に停止された場合（WGC 初期化の ~2 秒窓・2 回目の StartAsync 直後など）、
        // Canonical Timeline はまだ始まっていない。_clock はエンジン構築時から走っているため
        // 生の Elapsed を返すと実録時間より大幅に大きくなる → Duration は 0 を返す
        if (!_captureStarted)
        {
            return new RecordingResult
            {
                FilePath = filePath,
                Duration = TimeSpan.Zero,
                StartedAtUtc = _startedAtUtc,
                PauseIntervals = [],
            };
        }

        return new RecordingResult
        {
            FilePath = filePath,
            Duration = CanonicalDuration(),
            StartedAtUtc = _startedAtUtc,
            PauseIntervals = _pauseIntervals.ToArray(),
        };
    }

    private ScreenRecorderLib.RecorderOptions MapToLibOptions(RecordingOptions options)
    {
        // v6.6.0 の構成:
        // - SourceOptions.RecordingSources: 録画ソース（ディスプレイ）。GetDisplays() の実体を渡す
        // - AudioOptions: マイク (Input) とシステム音声 (Output) を同時指定するとミックスして MP4 に収まる
        var displays = ScreenRecorderLib.Recorder.GetDisplays();
        var sourceOptions = new SourceOptions();
        if (options.Display is { } display)
        {
            // TODO(Spike A): 複数モニター環境での個別指定を実機確認する
            var target = displays.FirstOrDefault(d => d.DeviceName == display.DeviceId)
                         ?? throw new InvalidOperationException($"指定ディスプレイが見つかりません: {display.DeviceId}");
            sourceOptions.RecordingSources.Add(target);
        }
        else
        {
            // 全デスクトップ（R-01: アプリを区別しない）— 接続中の全ディスプレイをソースにする
            foreach (var d in displays)
            {
                sourceOptions.RecordingSources.Add(d);
            }
        }

        var audioOptions = new AudioOptions
        {
            IsAudioEnabled = options.MicrophoneDevice is not null || options.SystemAudioDevice is not null,
            IsInputDeviceEnabled = options.MicrophoneDevice is not null,
            IsOutputDeviceEnabled = options.SystemAudioDevice is not null,
            // v6.6.0 の GetSystemAudioDevices が返す DeviceName はデバイス ID 形式
            // （{0.0.0.…}）であり、AudioInput/AudioOutputDevice にもその形式を渡す
            // （FriendlyName を渡すとデバイス解決に失敗し無音になる）
            AudioInputDevice = options.MicrophoneDevice?.DeviceId,
            AudioOutputDevice = options.SystemAudioDevice?.DeviceId,
        };

        return new ScreenRecorderLib.RecorderOptions
        {
            SourceOptions = sourceOptions,
            AudioOptions = audioOptions,
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Framerate = options.FrameRate,
                IsHardwareEncodingEnabled = true,
                // MP4 faststart（moov を先頭へ）= seek に強い。fragmented MP4 では効かないため明示的に無効化
                IsMp4FastStartEnabled = true,
                IsFragmentedMp4Enabled = false,
            },
        };
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        State = RecordingState.Idle;
        _completionSource?.TrySetResult(BuildResult(e.FilePath));
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        State = RecordingState.Failed;
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs { State = RecordingState.Failed, ErrorMessage = e.Error });
        _completionSource?.TrySetException(new InvalidOperationException($"録画に失敗しました: {e.Error}"));
    }

    private void OnStatusChanged(object? sender, RecordingStatusEventArgs e)
    {
        // WGC の初期化には ~2 秒かかるため、Canonical Timeline（録画開始 = 0ms・契約 §5.1）
        // は「実際に撮影が始まった瞬間」に時計を合わせてから測る
        if (e.Status == RecorderStatus.Recording && !_captureStarted)
        {
            _captureStarted = true;
            // 契約 §11: StartedAtUtc は Master Session Clock の起点 = Canonical 0ms。
            // StartAsync 時刻（Record 呼び出し）のままにすると WGC 初期化ぶん ~1.5〜2.0s ずれるため、
            // 実際の撮影開始瞬間で上書きする（監査 NEW-3 対応）
            _startedAtUtc = DateTimeOffset.UtcNow;
            _clock.Restart();
            // Restart 前のクロック領域で記録された Pause 情報は Canonical Timeline の外
            // （WGC 初期化窓内の Pause → Resume で旧領域の区間が残る）ため、ここで破棄する
            _pauseIntervals.Clear();
            _pauseStartedAt = null;
            // 統合メモ §1: B の EventCapture Session はこの瞬間に開始して
            // Canonical 0ms == MP4 の 0 秒に合わせる（README「A との時間同期」）
            CaptureStarted?.Invoke(this, EventArgs.Empty);
            return;
        }
    }

    private void EnsureRecording()
    {
        if (State is not (RecordingState.Recording or RecordingState.Paused))
        {
            throw new InvalidOperationException("開始されていない録画セッションに対する操作です");
        }
    }

    public void Dispose()
    {
        if (_recorder is not null)
        {
            _recorder.OnRecordingComplete -= OnRecordingComplete;
            _recorder.OnRecordingFailed -= OnRecordingFailed;
            _recorder.OnStatusChanged -= OnStatusChanged;
            _recorder.Dispose();
            _recorder = null;
        }
    }
}
