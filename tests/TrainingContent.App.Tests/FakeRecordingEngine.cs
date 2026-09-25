using TrainingContent.Capture;

namespace TrainingContent.App.Tests;

/// <summary>
/// recording を実機で行わない test 用の <see cref="IRecordingEngine"/>。
///
/// <para>
/// 実 engine の DeferredCommit contract のうち、test で意味のある部分だけを模す:
/// <list type="bullet">
///   <item>Stop が <c>PendingCommit = true</c> を返したら pending が残る</item>
///   <item>pending が残っている間の <c>StartAsync</c> は拒否する（実 engine と同じ）</item>
///   <item>Commit / Abort の呼出回数を数え、pending を解消する</item>
/// </list>
/// </para>
/// </summary>
internal sealed class FakeRecordingEngine : IRecordingEngine
{
    private readonly List<RecordingOptions> _startedOptions = [];

    /// <summary>Stop で pending が残っているか（実 engine の <c>_pendingCommit</c> 相当）。</summary>
    public bool HasPending { get; private set; }

    public int CommitCallCount { get; private set; }

    public int AbortCallCount { get; private set; }

    /// <summary>StartAsync に渡された options の履歴（DeferredCommit の検証に使う）。</summary>
    public IReadOnlyList<RecordingOptions> StartedOptions => _startedOptions;

    public RecordingOptions? LastStartOptions => _startedOptions.Count == 0 ? null : _startedOptions[^1];

    /// <summary>StopAsync が返す結果を作る。未設定なら <see cref="NotSupportedException"/>。</summary>
    public Func<RecordingResult>? StopResultFactory { get; set; }

    /// <summary>Commit 時の副作用（canonical への書き込みを模す）。</summary>
    public Action? OnCommit { get; set; }

    /// <summary>Commit を失敗させる（Move 失敗の模擬）。</summary>
    public Exception? CommitException { get; set; }

    /// <summary>Commit が成功したときに返す canonical path。</summary>
    public string? CanonicalPath { get; set; }

    public Task StartAsync(RecordingOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (HasPending)
        {
            // 実 engine と同じ: 確定待ちを放置したまま再 Start はできない。
            throw new InvalidOperationException("確定待ちの録画があります。");
        }

        _startedOptions.Add(options);

        // 実 engine と同じ: StartAsync 内で State = Recording になり StateChanged を通知する
        // （Coordinator は state を mirror し、IsSessionActive の判定に使う）。
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs { State = RecordingState.Recording });

        return Task.CompletedTask;
    }

    public Task<RecordingResult> StopAsync(CancellationToken cancellationToken = default)
    {
        var result = StopResultFactory?.Invoke()
            ?? throw new NotSupportedException("StopResultFactory が設定されていません。");

        if (result.PendingCommit)
        {
            HasPending = true;
        }

        // 実 engine と同じ: Stop 完了で Idle へ戻り StateChanged を通知する。
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs { State = RecordingState.Idle });

        return Task.FromResult(result);
    }

    public RecordingResult CommitPendingRecording()
    {
        CommitCallCount++;

        if (CommitException is not null)
        {
            throw CommitException;
        }

        OnCommit?.Invoke();
        HasPending = false;

        return new RecordingResult
        {
            FilePath = CanonicalPath ?? string.Empty,
            Duration = TimeSpan.FromSeconds(5),
            StartedAtUtc = DateTimeOffset.UtcNow,
        };
    }

    public void AbortPendingRecording()
    {
        AbortCallCount++;

        // 実 engine と同じ: 確定待ちが無ければ拒否する（呼出側は「解消済み」として扱う）。
        if (!HasPending)
        {
            throw new InvalidOperationException("確定待ちの録画がありません。");
        }

        HasPending = false;
    }

#pragma warning disable CS0067 // test fake: CaptureStarted はこの test では発火させない
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;

    public event EventHandler? CaptureStarted;
#pragma warning restore CS0067

    public IReadOnlyList<DisplayDevice> GetDisplays() => [];

    public IReadOnlyList<AudioDevice> GetMicrophones() => [];

    public IReadOnlyList<AudioDevice> GetSystemAudioDevices() => [];

    public Task PauseAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task ResumeAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
