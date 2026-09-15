namespace OperationCaptureSpike;

/// <summary>
/// Canonical Timeline の実装（Phase 0 契約 §5）。
/// 録画開始 = 0 ms、単位 = millisecond、Pause 中の実時間は含まない。
/// </summary>
public sealed class MasterClock
{
    private readonly System.Diagnostics.Stopwatch _stopwatch = new();
    private long _pausedAccumulatedMs;
    private long? _pauseStartedAtMs;

    public bool IsPaused => _pauseStartedAtMs.HasValue;

    public void Start()
    {
        _stopwatch.Start();
    }

    public void Pause()
    {
        if (_stopwatch.IsRunning && !_pauseStartedAtMs.HasValue)
        {
            _pauseStartedAtMs = _stopwatch.ElapsedMilliseconds;
        }
    }

    public void Resume()
    {
        if (_pauseStartedAtMs is { } pauseStart)
        {
            _pausedAccumulatedMs += _stopwatch.ElapsedMilliseconds - pauseStart;
            _pauseStartedAtMs = null;
        }
    }

    /// <summary>Canonical Timeline 上の現在時刻 (ms)。</summary>
    public long NowMs()
    {
        var pauseNow = _pauseStartedAtMs.HasValue ? _stopwatch.ElapsedMilliseconds - _pauseStartedAtMs.Value : 0;
        return _stopwatch.ElapsedMilliseconds - _pausedAccumulatedMs - pauseNow;
    }
}
