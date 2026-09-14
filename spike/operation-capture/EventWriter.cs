using System.IO;
using System.Text.Json;

namespace OperationCaptureSpike;

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

/// <summary>
/// events.jsonl ライター（Phase 0 契約 §8 / §20 / §21）。
/// 1 Event = 1 Line、append-only、seq は 1 始まりで単調増加。
/// JSON は camelCase・pretty print しない。
/// </summary>
public sealed class EventWriter
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly object _sync = new();
    private long _seq;

    public EventWriter(string path)
    {
        _path = path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    }

    /// <summary>1 行追加する。戻り値は採番された seq と Event Id。</summary>
    public (long Seq, Guid Id) Append(string type, long timestampMs, object? payload)
    {
        lock (_sync)
        {
            _seq++;
            var id = Guid.NewGuid();
            var line = JsonSerializer.Serialize(
                new TimelineEventLine(1, id, _seq, timestampMs, type, payload ?? new { }),
                Options);
            File.AppendAllText(_path, line + Environment.NewLine);
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
