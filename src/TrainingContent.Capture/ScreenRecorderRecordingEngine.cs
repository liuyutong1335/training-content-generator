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
    // staging パス（RC-2 監査 R-2 対策）: lib には必ずこの一時パスを渡し、
    // 成功時のみ canonical（options.OutputFilePath）へ置換する。preparation cancel や
    // 失敗時は canonical を触らないため、再録画時に既存の正常な録画を壊さない
    private string? _stagingFilePath;
    // DeferredCommit モード（統合メモ §7）で録画に成功し、canonical 置換が caller の判断待ちの間 true
    private bool _pendingCommit;
    // 確定待ち録画の確定時成果（StopAsync 完了時点で固定。Commit をいつ呼んでも
    // Duration / PauseIntervals が stop 時点の値になる — clock は stop 後も進むため再計算しない）
    private RecordingResult? _pendingResult;

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

        if (_pendingCommit)
        {
            // 確定待ちの staging がある状態での再録画は、Commit / Abort の機会を失わせるため拒否する
            throw new InvalidOperationException(
                $"確定待ちの録画があります（{_stagingFilePath}）。CommitPendingRecording / AbortPendingRecording を先に呼び出してください");
        }

        _currentOptions = options ?? throw new ArgumentNullException(nameof(options));
        _pauseIntervals.Clear();
        _pauseStartedAt = null;

        // 前セッションの Recorder が残留している場合は先に解放する（再利用時のリソースリーク防止）
        DisposeRecorder();

        var recorderOptions = MapToLibOptions(options);
        _recorder = ScreenRecorderLib.Recorder.CreateRecorder(recorderOptions);
        _recorder.OnRecordingComplete += OnRecordingComplete;
        _recorder.OnRecordingFailed += OnRecordingFailed;
        _recorder.OnStatusChanged += OnStatusChanged;

        _completionSource = new TaskCompletionSource<RecordingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // lib へは staging パスを渡す（canonical は成功時の Move で置換 — R-2 対策）。
        // staging は canonical と同じディレクトリに作る（同一ボリュームで File.Move が確実に機能する）
        _stagingFilePath = BuildStagingPath(options.OutputFilePath);
        _recorder.Record(_stagingFilePath);
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
        // 監査 m-1: 準備中（CaptureStarted 前）の Pause → そのまま撮影開始になった場合、
        // OnStatusChanged が旧クロック領域ごと Pause 情報を破棄する（知見 11）ため
        // _pauseStartedAt が null のまま Resume され得る。区間が無ければ追記しないだけとし、
        // ここで NRE しない（D の UI は準備中 Pause を許可しないが、エンジン単独利用時は到達し得る）。
        if (_pauseStartedAt is { } pauseStart)
        {
            _pauseIntervals.Add((pauseStart, _clock.Elapsed));
            _pauseStartedAt = null;
        }

        State = RecordingState.Recording;
        return Task.CompletedTask;
    }

    public async Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        EnsureRecording();
        if (_pauseStartedAt is not null)
        {
            // Pause 中に Stop された場合も区間として閉じる
            _pauseIntervals.Add((_pauseStartedAt.Value, _clock.Elapsed));
            _pauseStartedAt = null;
        }

        State = RecordingState.Stopping;

        if (!_captureStarted)
        {
            // CaptureStarted 前（WGC 初期化窓内）の停止: lib の Stop() を呼ばずに
            // Recorder を直接破棄して打ち切る。初期化中の Stop() では MP4 シンクが
            // 正常に閉じず、0 バイト MP4 がプロセス終了までロック残留する
            // （preparation-cancel-check で実機確認。Recorder.Dispose でも解放されない
            //   ScreenRecorderLib 6.6.0 の制約）。Canonical Timeline はまだ始まっていないため、
            // Duration = 0 の結果を返す。
            // 残留する 0 バイトファイルは staging 側に発生する（R-2 対策により canonical は
            // 触っていない）。削除を試み、ハンドルリークで失敗した場合は無視してよい
            // （staging 名は次回 StartAsync で再利用されない）。
            DisposeRecorder();
            TryDeleteStaging();
            State = RecordingState.Idle;
            var aborted = BuildResult(_currentOptions!.OutputFilePath);
            _completionSource?.TrySetResult(aborted);
            return aborted;
        }

        _recorder!.Stop();
        // OnRecordingComplete / OnRecordingFailed で完了する
        // 注意: 完了後にここで Dispose しない（lib の完了処理と競合し MP4 の終端書き込みが
        // 欠けることがある — integration-smoke 項目 4 で実機検出）。解放は次の StartAsync
        // （先頭の DisposeRecorder）か engine.Dispose() で行う。
        return await _completionSource!.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// DeferredCommit モード（統合メモ §7）で確定待ちの録画を canonical
    /// （<see cref="RecordingOptions.OutputFilePath"/>）へ確定する（staging → canonical の Move）。
    /// Move が失敗した場合（完成 MP4 が視聴中でロックされる等）は例外を送出するが
    /// staging は保持される（R-2 と同じ方針: 録画データを失わない。取り込み直しはこの例外後、
    /// staging に対して行う）。成功すると確定済みの RecordingResult（FilePath = canonical）を返す。
    /// </summary>
    public RecordingResult CommitPendingRecording()
    {
        if (!_pendingCommit || _stagingFilePath is null || _pendingResult is null || _currentOptions is null)
        {
            throw new InvalidOperationException("確定待ちの録画がありません（DeferredCommit モードの StopAsync 成功後に呼び出せます）");
        }

        var canonical = _currentOptions.OutputFilePath;
        try
        {
            File.Move(_stagingFilePath, canonical, overwrite: true);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"録画ファイルを {canonical} へ確定できませんでした（staging に残留: {_stagingFilePath}）: {ex.Message}", ex);
        }

        _stagingFilePath = null;
        _pendingCommit = false;
        var committed = _pendingResult;
        _pendingResult = null;
        // Duration 等は stop 完了時点の固定値を使い直す（この時点の clock 再計算はしない）
        return new RecordingResult
        {
            FilePath = canonical,
            Duration = committed.Duration,
            StartedAtUtc = committed.StartedAtUtc,
            PauseIntervals = committed.PauseIntervals,
            PendingCommit = false,
            PendingCommitPath = canonical,
        };
    }

    /// <summary>
    /// DeferredCommit モードで確定待ちの録画を破棄する（staging の削除のみ。
    /// canonical は一切変更されない）。トランザクションを失敗させる場合に呼ぶ。
    /// staging は既知の lib ハンドルリークにより削除に失敗し得るが、その場合も canonical は無傷。
    /// </summary>
    public void AbortPendingRecording()
    {
        if (!_pendingCommit || _stagingFilePath is null)
        {
            throw new InvalidOperationException("確定待ちの録画がありません（DeferredCommit モードの StopAsync 成功後に呼び出せます）");
        }

        TryDeleteStaging();
        _pendingCommit = false;
        _pendingResult = null;
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
        var canonicalPath = _currentOptions?.OutputFilePath;
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
                PendingCommit = _pendingCommit,
                PendingCommitPath = canonicalPath,
            };
        }

        return new RecordingResult
        {
            FilePath = filePath,
            Duration = CanonicalDuration(),
            StartedAtUtc = _startedAtUtc,
            PauseIntervals = _pauseIntervals.ToArray(),
            PendingCommit = _pendingCommit,
            PendingCommitPath = canonicalPath,
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
        var finalPath = _currentOptions!.OutputFilePath;
        var deferred = _currentOptions.DeferredCommit;
        if (_captureStarted && _stagingFilePath is not null)
        {
            if (deferred)
            {
                // two-phase finalize（統合メモ §7）: staging を正常録画として保持し、
                // canonical への置換を caller の Commit / Abort に委ねる。
                // D 側は StepBuilder → validation → project.json save を経てから確定できる。
                // Duration 等はこの時点（stop 完了時）で固定して持ち回る
                _pendingCommit = true;
                _pendingResult = BuildResult(_stagingFilePath);
                _completionSource?.TrySetResult(_pendingResult);
                return;
            }

            // 撮影に成功した場合のみ canonical へ置換する（R-2: 再録画時に既存の
            // 正常な録画を staging で壊さない。同一ボリュームなので Move で原子的に入れ替わる）。
            // Move が失敗した場合（再生中のロック等）は録画データを失わないよう、
            // staging パスを保持したまま失敗として完了させる
            try
            {
                File.Move(_stagingFilePath, finalPath, overwrite: true);
                _stagingFilePath = null;
            }
            catch (Exception ex)
            {
                _completionSource?.TrySetException(new InvalidOperationException(
                    $"録画ファイルを {finalPath} へ確定できませんでした（staging に残留: {_stagingFilePath}）: {ex.Message}", ex));
                return;
            }
        }
        else
        {
            // CaptureStarted に到達しないまま完了した場合の保険（通常この経路は StopAsync の
            // aborted 経路で閉じられる）。staging が残っていれば canonical を壊さないよう削除のみ
            TryDeleteStaging();
        }

        _pendingCommit = false;
        _completionSource?.TrySetResult(BuildResult(finalPath));
    }

    private void OnRecordingFailed(object? sender, RecordingFailedEventArgs e)
    {
        State = RecordingState.Failed;
        TryDeleteStaging();
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
            // 契約 §7: StartedAtUtc は Master Session Clock の起点 = Canonical 0ms。
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
        DisposeRecorder();
        // DeferredCommit で確定待ち（PendingCommit）の staging は「成功した録画の唯一のコピー」
        // であるため、Dispose では削除しない（統合メモ §7）。caller が Commit / Abort で
        // 明示的に判断する。通常モードでは確定済み / 不存在のため従来どおり冪等に後片付けする
        if (!_pendingCommit)
        {
            TryDeleteStaging();
        }
    }

    /// <summary>
    /// canonical パスから staging パスを組み立てる（同一ディレクトリ・同一拡張子・
    /// GUID 付きのため複数 StartAsync や前回残留とも衝突しない）。テスト用に internal。
    /// </summary>
    internal static string BuildStagingPath(string canonicalPath)
    {
        var dir = Path.GetDirectoryName(canonicalPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(canonicalPath);
        var ext = Path.GetExtension(canonicalPath);
        return Path.Combine(dir, $"{name}.staging-{Guid.NewGuid():N}{ext}");
    }

    /// <summary>staging ファイルの後片付け（冪等・失敗は無視 = 既知の lib ハンドルリーク）。</summary>
    private void TryDeleteStaging()
    {
        if (_stagingFilePath is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_stagingFilePath))
            {
                File.Delete(_stagingFilePath);
            }

            _stagingFilePath = null;
        }
        catch (IOException)
        {
            // ハンドルが解放されない（0 バイト残留 = preparation cancel 経路の既知の lib 制約）。
            // canonical recording ではないため無視してよい。次回 StartAsync では別名を使う
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    /// <summary>Recorder を解放し、イベント購読を解除する（冪等）。</summary>
    private void DisposeRecorder()
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
