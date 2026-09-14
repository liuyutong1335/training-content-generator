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
    private TaskCompletionSource<RecordingResult>? _completionSource;

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
        _clock.Restart();
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
                IsMp4FastStartEnabled = true, // Gate A の MP4 seek 対応
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
        // エンジン側の状態遷移とライブラリの状態を同期する（停止リクエストの二重管理を避ける）
        if (_state == RecordingState.Stopping && e.Status == RecorderStatus.Finishing)
        {
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
