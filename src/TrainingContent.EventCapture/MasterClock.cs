using System.Diagnostics;

namespace TrainingContent.EventCapture;

/// <summary>
/// Canonical Timeline の実装（Phase 0 契約 §5）。
/// 録画開始 = 0 ms、単位 = millisecond、Pause 中の実時間は含まない。
/// テスト用に経過時間の取得関数を差し替え可能。
/// </summary>
public sealed class MasterClock
{
    private readonly Func<long> _elapsedMs;
    private readonly Stopwatch? _ownedStopwatch;
    private long _pausedAccumulatedMs;
    private long? _pauseStartedAtMs;
    private long _originShiftMs;
    private bool _started;

    /// <summary>実運用: Stopwatch が時間源。</summary>
    public MasterClock()
    {
        _ownedStopwatch = new Stopwatch();
        _elapsedMs = () => _ownedStopwatch.ElapsedMilliseconds;
    }

    /// <summary>テスト用: 経過ミリ秒の供給関数を注入する。</summary>
    public MasterClock(Func<long> elapsedMsProvider)
    {
        _elapsedMs = elapsedMsProvider;
    }

    public bool IsPaused => _pauseStartedAtMs.HasValue;

    public void Start()
    {
        _started = true;
        _ownedStopwatch?.Start();
    }

    public void Pause()
    {
        if (!_started || _pauseStartedAtMs.HasValue)
        {
            return;
        }

        _pauseStartedAtMs = _elapsedMs();
    }

    public void Resume()
    {
        if (_pauseStartedAtMs is { } pauseStart)
        {
            _pausedAccumulatedMs += _elapsedMs() - pauseStart;
            _pauseStartedAtMs = null;
        }
    }

    /// <summary>
    /// 現在時刻を新しい原点（0ms）に張り直す。
    /// 用途: 録画エンジンの実際の撮影開始瞬間に Canonical Timeline を合わせる
    /// （ScreenRecorderLib の WGC 初期化 ~2 秒問題。docs/integration-notes.md §1 参照）。
    /// Pause 累計は保持されるため、録画途中の再基準にも耐える。
    /// </summary>
    public void RebaseOriginToNow()
    {
        _originShiftMs += NowMs();
    }

    /// <summary>Canonical Timeline 上の現在時刻 (ms)。開始前は 0。</summary>
    public long NowMs()
    {
        if (!_started)
        {
            return 0;
        }

        var raw = _elapsedMs();
        var pauseNow = _pauseStartedAtMs.HasValue ? raw - _pauseStartedAtMs.Value : 0;
        return raw - _pausedAccumulatedMs - pauseNow - _originShiftMs;
    }
}
