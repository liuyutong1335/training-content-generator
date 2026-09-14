using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrainingContent.Core;

/// <summary>
/// JSON Serialization Rule（契約 §21）: C# PascalCase ↔ JSON camelCase。
/// project.json は WriteIndented、events.jsonl は 1 イベント 1 行（compact）。
/// </summary>
public static class TrainingJson
{
    /// <summary>project.json 等の整形出力用。</summary>
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>events.jsonl（1 Event = 1 Line）用。pretty print しない。</summary>
    public static readonly JsonSerializerOptions Compact = new(JsonSerializerDefaults.Web);
}
