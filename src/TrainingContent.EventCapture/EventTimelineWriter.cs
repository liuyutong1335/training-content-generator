using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TrainingContent.EventCapture;

// ---- Phase 0 契約に対応する payload 型（§10 / §11） ----

public sealed record MousePayload(
    int X,
    int Y,
    string Button,
    int ClickCount,
    string? ProcessName,
    string? WindowTitle,
    UiElementPayload? UiElement,
    string? ScreenshotPath);

public sealed record UiElementPayload(
    string? Name,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    bool IsEditable,
    bool IsPassword,
    BoundsPayload? Bounds);

public sealed record BoundsPayload(int X, int Y, int Width, int Height);

public sealed record TextEntryPayload(
    int? KeyCount,
    string? ProcessName,
    string? WindowTitle,
    TargetPayload? Target,
    bool IsSensitive);

public sealed record TargetPayload(string? Name, string? AutomationId, string? ControlType);

public sealed record SpecialKeyPayload(string Key);

public sealed record ShortcutPayload(string Shortcut);

/// <summary>
/// events.jsonl ライター（Phase 0 契約 §8 / §20 / §21）。
/// 1 Event = 1 Line、append-only、seq は 1 始まりで単調増加。
/// JSON は camelCase・pretty print しない。非 ASCII はエスケープしない。
/// </summary>
public sealed class EventTimelineWriter
{
    // UnsafeRelaxedJsonEscaping: 日本語などの非 ASCII を \uXXXX にエスケープせず
    // そのまま出力する（JSONL を人間が読めるようにするため。JSON 的にはどちらも正当）。
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _path;
    private readonly object _sync = new();
    private long _seq;

    /// <summary>書き込み失敗（共有違反など一時的 IOException）時の再試行回数。</summary>
    private const int MaxWriteAttempts = 12;

    /// <summary>再試行の待ち時間。10ms から倍々で 500ms に頭打ちする（合計最長約 4 秒）。</summary>
    private static int RetryDelayMs(int attempt) => Math.Min(500, 10 * (1 << attempt));

    public EventTimelineWriter(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // 既存 events.jsonl への追加開始（re-record 等）では seq を前回の最大値から
        // 続けて採番する（0 に戻すと契約 §8.1 の Seq start 1 / 単調増加に違反する）。
        _seq = ReadMaxSeq(path);
        EnsureTrailingNewline(path);
    }

    /// <summary>
    /// 既存ファイルの全行から読み取れる seq の最大値を返す（監査 MIN-1）。
    /// 最終行だけを見る方式だと、クラッシュで最終行が半壊した events.jsonl の追記時に
    /// seq が 1 から再開（重複）していた。読めない行（破損行）はスキップする。
    /// </summary>
    private static long ReadMaxSeq(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return 0;
            }

            long max = 0;
            using var reader = new StreamReader(path);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("seq", out var seq) && seq.TryGetInt64(out var value))
                    {
                        max = Math.Max(max, value);
                    }
                }
                catch (JsonException)
                {
                    // 破損行: seq 不明。最大値の計算から除外するだけにする。
                }
            }

            return max;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    /// <summary>
    /// クラッシュ等で改行なしに途切れた最終行がある場合、追記がその行に連結されて
    /// 1 行が完全破損する（監査 MIN-1）。開始時に改行で正規化しておく。
    /// </summary>
    private static void EnsureTrailingNewline(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                return;
            }

            using var stream = info.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
            stream.Seek(-1, SeekOrigin.End);
            var last = stream.ReadByte();
            if (last != '\n')
            {
                stream.Seek(0, SeekOrigin.End);
                var newline = System.Text.Encoding.UTF8.GetBytes(Environment.NewLine);
                stream.Write(newline);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 正規化できなくても追記自体は試みる（次の書き込みで失敗するならそれも従来どおり）。
        }
    }

    public string FilePath => _path;

    /// <summary>1 行追加する。戻り値は採番された seq と Event Id。</summary>
    /// <remarks>
    /// seq は書き込みが成功したときだけ進む。(events.jsonl は別プロセス / Defender 等の
    ///  FileShare.Read ハンドルによって一時的に書き込み不能になり得る。その間は
    ///  短いバックオフで再試行し、それでも失敗したら例外として伝播させる。このとき
    ///  seq を先に進めてしまうと、ファイル上の行と seq が永続的にズレるため)。
    /// </remarks>
    public (long Seq, Guid Id) Append(string type, long timestampMs, object? payload)
    {
        lock (_sync)
        {
            var candidateSeq = _seq + 1;
            var id = Guid.NewGuid();
            var line = JsonSerializer.Serialize(
                new TimelineEventLine(1, id, candidateSeq, timestampMs, type, payload ?? new { }),
                Options);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.AppendAllText(_path, line + Environment.NewLine);
                    break;
                }
                catch (IOException) when (attempt < MaxWriteAttempts - 1)
                {
                    // 共有違反（一時的）: 少し待って再試行する。
                    Thread.Sleep(RetryDelayMs(attempt));
                }
            }

            _seq = candidateSeq;
            return (_seq, id);
        }
    }

    public long Count
    {
        get { lock (_sync) { return _seq; } }
    }

    private sealed record TimelineEventLine(
        int SchemaVersion,
        Guid Id,
        long Seq,
        long TimestampMs,
        string Type,
        object Payload);
}
