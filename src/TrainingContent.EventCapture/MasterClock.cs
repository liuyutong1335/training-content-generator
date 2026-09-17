using System.Diagnostics;

namespace TrainingContent.EventCapture;

/// <summary>
/// Canonical Timeline の実装（Phase 0 契約 §5）。
/// 録画開始 = 0 ms、単位 = millisecond、Pause 中の実時間は含まない。
/// テスト用に経過時間の取得関数を差し替え可能。
///
/// さらに QPC タイムスタンプ（Stopwatch.GetTimestamp()）から Canonical ms への変換を
/// 提供する。ダブルクリック判定待ちで保留されたマウス クリックのように、
/// 「発生した瞬間」と「イベントとして受領する瞬間」が乖離する Raw Event を、
/// 物理発生時刻のまま Canonical Timeline へ載せるため（受領時刻で採番すると
/// ダブルクリック待ち時間ぶんの系統誤差が生じる）。
/// </summary>
public sealed class MasterClock
{
    private readonly Func<long> _elapsedMs;
    private readonly Stopwatch? _ownedStopwatch;
    private readonly bool _ownsClock;
    private readonly List<(long StartRawMs, long? EndRawMs)> _pauseIntervals = [];
    private long _originShiftMs;
    private long _startQpc;
    private bool _started;

    /// <summary>実運用: Stopwatch が時間源。</summary>
    public MasterClock()
    {
        _ownsClock = true;
        _ownedStopwatch = new Stopwatch();
        _elapsedMs = () => _ownedStopwatch.ElapsedMilliseconds;
    }

    /// <summary>テスト用: 経過ミリ秒の供給関数を注入する。</summary>
    public MasterClock(Func<long> elapsedMsProvider)
    {
        _elapsedMs = elapsedMsProvider;
    }

    public bool IsPaused => _started && _pauseIntervals is [.., (_, null)];

    public void Start()
    {
        _started = true;
        _ownedStopwatch?.Start();
        _startQpc = Stopwatch.GetTimestamp();
    }

    public void Pause()
    {
        if (!_started || _pauseIntervals is [.., (_, null)])
        {
            return;
        }

        _pauseIntervals.Add((_elapsedMs(), null));
    }

    public void Resume()
    {
        if (_pauseIntervals is [.., (var start, null)])
        {
            _pauseIntervals[^1] = (start, _elapsedMs());
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

        return CanonicalOfRaw(_elapsedMs());
    }

    /// <summary>
    /// QPC タイムスタンプ（Stopwatch.GetTimestamp() の戻り値）を Canonical ms に変換する。
    /// 注入モード（テスト用コンストラクタ）では QPC と注入時間源の対応が取れないため、
    /// 現在時刻にフォールバックする。
    /// </summary>
    public long ToCanonicalMs(long qpcTimestamp)
    {
        if (!_started)
        {
            return 0;
        }

        if (!_ownsClock)
        {
            return NowMs();
        }

        var raw = (long)((qpcTimestamp - _startQpc) * 1000.0 / Stopwatch.Frequency);
        return Math.Max(0, CanonicalOfRaw(raw));
    }

    /// <summary>QPC タイムスタンプから現在までの経過 ms（処理遅延の検出用）。注入モードでは 0。</summary>
    public long ElapsedSinceMs(long qpcTimestamp)
    {
        if (!_ownsClock)
        {
            return 0;
        }

        return (long)((Stopwatch.GetTimestamp() - qpcTimestamp) * 1000.0 / Stopwatch.Frequency);
    }

    /// <summary>raw 経過 ms（時間源の生値）を Canonical ms へ変換する。Pause 区間を除外する。</summary>
    private long CanonicalOfRaw(long rawMs)
    {
        // Pause 区間のうち rawMs より前の部分だけを除外する:
        //   完了区間は全長を、進行中区間は rawMs までの長さを差し引くことで、
        //   Pause 中は区間開始時刻の凍結値に一致し（例: 1000ms で Pause → Pause 中は常に 1000ms を返す）、
        //   Resume 後は Pause 全体が除外される。
        // これにより Pause 中に発生した Raw Event（凍結値 = 区間開始時刻の timestampMs を持つ）は
        // Resume 後のフィルタ（timestampMs <= boundary）で確実に弾かれる。
        var pausedMs = 0L;
        foreach (var (start, end) in _pauseIntervals)
        {
            if (start >= rawMs)
            {
                continue;
            }

            pausedMs += Math.Min(end ?? rawMs, rawMs) - start;
        }

        return rawMs - pausedMs - _originShiftMs;
    }
}
