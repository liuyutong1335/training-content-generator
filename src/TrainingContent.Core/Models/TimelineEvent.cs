using System.Text.Json;

namespace TrainingContent.Core.Models;

/// <summary>
/// TimelineEvent 契約（phase0-contract.md §8）。録画中に発生した操作事実（Raw Event）。
/// events.jsonl に append-only で保存し、原則記録後に編集しない。
/// </summary>
public sealed class TimelineEvent
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public long Seq { get; set; }
    public long TimestampMs { get; set; }
    public string Type { get; set; } = "";
    public JsonElement Payload { get; set; }
}

/// <summary>MVP Event Type 定義（契約 §9）。未知 Type は Warning + Continue（§26）。</summary>
public static class EventTypes
{
    // User Operation
    public const string MouseClick = "mouse.click";
    public const string MouseDoubleClick = "mouse.doubleClick";
    public const string MouseRightClick = "mouse.rightClick";
    public const string KeyboardTextEntry = "keyboard.textEntry";
    public const string KeyboardSpecialKey = "keyboard.specialKey";
    public const string KeyboardShortcut = "keyboard.shortcut";

    // Recording Lifecycle（TrainingStep に変換しない）
    public const string RecordingStarted = "recording.started";
    public const string RecordingPaused = "recording.paused";
    public const string RecordingResumed = "recording.resumed";
    public const string RecordingStopped = "recording.stopped";

    public static readonly IReadOnlySet<string> KnownTypes = new HashSet<string>
    {
        MouseClick, MouseDoubleClick, MouseRightClick,
        KeyboardTextEntry, KeyboardSpecialKey, KeyboardShortcut,
        RecordingStarted, RecordingPaused, RecordingResumed, RecordingStopped,
    };

    public static bool IsKnown(string type) => KnownTypes.Contains(type);

    /// <summary>Lifecycle Event は TrainingStep に変換しない（契約 §9.2）。</summary>
    public static bool IsLifecycle(string type) =>
        type is RecordingStarted or RecordingPaused or RecordingResumed or RecordingStopped;
}
