namespace TrainingContent.EventCapture;

/// <summary>集約済みの 1 keyboard.textEntry Event。Raw Event の timestampMs はバースト先頭。</summary>
public sealed record TextEntryEvent(long StartTimestampMs, TextEntryPayload Payload);

/// <summary>
/// 連続するテキスト入力キーを 1 つの keyboard.textEntry Event に集約する（契約 §11.1 / §20）。
/// 実入力文字は一切保持しない（Keylogger ではない / 契約 §27）。
///
/// Password 保護（契約 §11.1 の禁止事項: Password 入力文字数の保存をしない）:
/// バースト内に 1 つでも IsPassword のキーが含まれれば、バースト全体を
/// keyCount = null + isSensitive = true として出力する。
/// 各キーごとに呼び出し側が IsPassword を判定して渡す前提（バースト途中で
/// パスワード欄へ移った場合も取りこぼさない）。
/// </summary>
public sealed class TextEntryAggregator
{
    private readonly long _gapMs;
    private readonly List<(long TimestampMs, bool IsPassword)> _keys = [];
    private (TargetPayload? Target, string? ProcessName, string? WindowTitle)? _context;

    public TextEntryAggregator(long gapMs = 2000)
    {
        _gapMs = gapMs;
    }

    /// <summary>現在バーストを保持中か（新規バーストの先頭判定に使う）。</summary>
    public bool IsEmpty => _keys.Count == 0;

    /// <summary>
    /// 1 キーを追加する。直前のキーから <c>gapMs</c> 超の空白があった場合は
    /// 締め切った分を戻り値で返す（呼び出し側が events.jsonl に出力する）。
    /// </summary>
    public TextEntryEvent? Add(long timestampMs, bool isPassword, TargetPayload? target, string? processName, string? windowTitle)
    {
        TextEntryEvent? flushed = null;
        if (_keys.Count > 0 && timestampMs - _keys[^1].TimestampMs > _gapMs)
        {
            flushed = Flush();
        }

        if (_keys.Count == 0)
        {
            _context = (target, processName, windowTitle);
        }

        _keys.Add((timestampMs, isPassword));
        return flushed;
    }

    /// <summary>バーストを強制的に締め切る。保持していなければ null。</summary>
    public TextEntryEvent? Flush()
    {
        if (_keys.Count == 0)
        {
            return null;
        }

        var isSensitive = _keys.Any(k => k.IsPassword);
        var (target, processName, windowTitle) = _context ?? (null, null, null);
        var payload = new TextEntryPayload(
            isSensitive ? null : _keys.Count,
            processName,
            windowTitle,
            isSensitive ? null : target,
            isSensitive);
        var start = _keys[0].TimestampMs;
        _keys.Clear();
        _context = null;
        return new TextEntryEvent(start, payload);
    }
}
