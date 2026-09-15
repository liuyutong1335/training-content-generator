using System.Runtime.CompilerServices;
using System.Text.Json;
using TrainingContent.Core.Models;

namespace TrainingContent.Core.Tests;

/// <summary>
/// StepBuilder テスト専用の fixture 解決。csproj を変更しないため、このファイルの配置
/// ディレクトリ（<c>[CallerFilePath]</c>）を基準に Fixtures/StepBuilder/ を直接結合する。
/// 汎用 TestData ではなく、StepBuilder テスト以外からは参照しない。
/// </summary>
internal static class StepBuilderFixture
{
    private static readonly string Directory = ResolveDirectory();

    /// <summary>tests/TrainingContent.Core.Tests/Fixtures/StepBuilder/events.jsonl の絶対パス。</summary>
    public static string EventsJsonlPath { get; } = Path.Combine(Directory, "Fixtures", "StepBuilder", "events.jsonl");

    public static string EventsJsonlText() => File.ReadAllText(EventsJsonlPath);

    public static IReadOnlyList<string> EventsJsonlLines() => File.ReadAllLines(EventsJsonlPath);

    // 呼び出し元はこのファイル自身のフィールド初期化子なので、このファイルの配置ディレクトリが返る。
    private static string ResolveDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(Path.GetFullPath(callerFilePath))
        ?? throw new InvalidOperationException("StepBuilderTestData.cs の配置ディレクトリを解決できません。");
}

/// <summary>StepBuilder テスト専用の TimelineEvent 組み立てヘルパー。</summary>
internal static class TimelineEventFactory
{
    public static TimelineEvent Create(string type, long seq, long timestampMs, string payloadJson = "{}", Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Seq = seq,
        TimestampMs = timestampMs,
        Type = type,
        Payload = JsonSerializer.Deserialize<JsonElement>(payloadJson),
    };

    /// <summary>seq を 1..N で採番した Event 列を作る。</summary>
    public static List<TimelineEvent> Sequence(params (string Type, long TimestampMs, string PayloadJson)[] items)
    {
        var events = new List<TimelineEvent>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            events.Add(Create(items[i].Type, seq: i + 1, timestampMs: items[i].TimestampMs, payloadJson: items[i].PayloadJson));
        }

        return events;
    }

    /// <summary>操作 Event 1 件のみの入力。</summary>
    public static List<TimelineEvent> Single(string type, string payloadJson = "{}", long timestampMs = 1000) =>
        Sequence((type, timestampMs, payloadJson));
}
