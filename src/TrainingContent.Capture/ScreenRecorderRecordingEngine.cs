using ScreenRecorderLib;

namespace TrainingContent.Capture;

/// <summary>
/// ScreenRecorderLib を用いた IRecordingEngine の実装（§16）。
///
/// 注意: ScreenRecorderLib の正確な API（Recorder 作成・AudioDevices 指定・
/// Pause/Resume・コールバック）は Spike A で v6.6.0 と v7.0.1 の実機比較を行いながら
/// 確定させる（計画書 §4.1: v7 系でフリーズ/FPS 低下の報告があるため）。
/// その確定前の骨格実装として、記録対象の呼び出し形をここに集約する。
/// </summary>
public sealed class ScreenRecorderRecordingEngine : IRecordingEngine, IDisposable
{
    private Recorder? _recorder;
    private RecordingOptions? _currentOptions;
    private DateTimeOffset _startedAtUtc;
    private Stopwatch _clock = Stopwatch.StartNew(); // Master Session Clock
    private readonly List<(TimeSpan Start, TimeSpan End)> _pauseIntervals = [];
    private TimeSpan? _pauseStartedAt;
    private RecordingState _state = RecordingState.Idle;

    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

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
        // TODO(Spike A): Recorder.GetDisplays() の実機検証 — 取得できない場合は
        // EnumDisplayMonitors（spike/python-recording の devices.py 実装を移植）へフォールバック
        return Recorder.GetDisplays()
            .Select(d => new DisplayDevice(d.DeviceName, d.IsPrimary))
            .ToList();
    }

    public IReadOnlyList<AudioDevice> GetMicrophones()
    {
        // TODO(Spike A): CaptureAudioSource 側のデバイス列挙 API を確定する
        return Recorder.GetSystemAudioCaptureDevices()
            .Select(d => new AudioDevice(d.DeviceName, AudioDeviceSource.Capture))
            .ToList();
    }

    public IReadOnlyList<AudioDevice> GetSystemAudioDevices()
    {
        // TODO(Spike A): LoopbackAudioSource 側のデバイス列挙 API を確定する
        return Recorder.GetSystemAudioLoopbackDevices()
            .Select(d => new AudioDevice(d.DeviceName, AudioDeviceSource.Loopback))
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
        _recorder = Recorder.CreateRecorder(recorderOptions);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;

        _recorder.Record(options.OutputFilePath);
        _startedAtUtc = DateTimeOffset.UtcNow;
        _clock = Stopwatch.StartNew();
        State = RecordingState.Recording;
        return Task.CompletedTask;
    }

    public Task PauseAsync(CancellationToken cancellationToken = default)
    {
        EnsureRecording();
        _recorder!.PauseRecording();
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

        _recorder!.ResumeRecording();
        _pauseIntervals.Add((_pauseStartedAt!.Value, _clock.Elapsed));
        _pauseStartedAt = null;
        State = RecordingState.Recording;
        return Task.CompletedTask;
    }

    public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureRecording();
        State = RecordingState.Stopping;
        if (_pauseStartedAt is not null)
        {
            // Pause 中に Stop された場合も区間として閉じる
            _pauseIntervals.Add((_pauseStartedAt.Value, _clock.Elapsed));
            _pauseStartedAt = null;
        }

        var tcs = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completionSource = tcs;
        _recorder!.StopRecording();
        var result = await tcs.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        State = RecordingState.Idle;
        return result;
    }

    private TaskCompletionSource<RecordingResult>? _completionSource;

    private RecordingResult BuildResult()
    {
        var duration = _clock.Elapsed;
        return new RecordingResult
        {
            FilePath = _currentOptions!.OutputFilePath,
            Duration = duration,
            StartedAtUtc = _startedAtUtc,
            PauseIntervals = _pauseIntervals.ToArray(),
        };
    }

    private void OnRecordingComplete(object? sender, RecordingCompleteEventArgs e)
    {
        // TODO(Spike A): e.FilePath / e.Error の型を実機で確認し、失敗時の扱いを確定する
        _completionSource?.TrySetResult(BuildResult());
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        State = RecordingState.Failed;
        _completionSource?.TrySetException(new InvalidOperationException(e.Error));
    }

    private static ScreenRecorderLib.RecordingOptions MapToLibOptions(RecordingOptions options)
    {
        // TODO(Spike A): 実機比較 (v6.6.0 vs v7.0.1) で API の詳細を確定する。
        // - AudioDevices: LoopbackAudioSource（R-02）+ CaptureAudioSource（R-03）の同時指定
        // - Display 対応（全デスクトップ / 個別モニター）
        var libOptions = new ScreenRecorderLib.RecordingOptions(options.OutputFilePath)
        {
            FrameRate = options.FrameRate,
        };
        var audioDevices = new List<ScreenRecorderLib.AudioDevice>();
        if (options.SystemAudioDevice is not null)
        {
            audioDevices.Add(new ScreenRecorderLib.AudioDevice
            {
                DeviceName = options.SystemAudioDevice.DeviceName,
                IsLoopbackDevice = true,
            });
        }
        if (options.MicrophoneDevice is not null)
        {
            audioDevices.Add(new ScreenRecorderLib.AudioDevice
            {
                DeviceName = options.MicrophoneDevice.DeviceName,
                IsLoopbackDevice = false,
            });
        }
        libOptions.AudioDevices = audioDevices;
        return libOptions;
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
            _recorder = null;
        }
    }
}
