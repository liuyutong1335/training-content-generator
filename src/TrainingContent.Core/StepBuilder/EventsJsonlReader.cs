using System.Text.Json;
using TrainingContent.Core.Models;

namespace TrainingContent.Core;

/// <summary>events.jsonl の読み取り結果。Errors は「行の構文」レベルのエラー。</summary>
public sealed class EventsJsonlReadResult
{
    public IReadOnlyList<TimelineEvent> Events { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// events.jsonl Reader（契約 §20 / §21）。StepBuilder 本体から分離した「行の読み取り」専用。
/// 担当するのは構文レベル（malformed JSON / object 以外 / 必須フィールド欠落 / id が GUID でない）。
/// seq 規則・Payload・Event Type の意味論は StepBuilder 側の責務であり、ここでは判定しない。
/// 1 Event = 1 Line、pretty print しない。Raw Event は書き換えず、新しい TimelineEvent を組み立てる。
/// </summary>
public static class EventsJsonlReader
{
    /// <summary>対応する schemaVersion（契約 §23）。</summary>
    public const int SupportedSchemaVersion = 1;

    /// <summary>空行・末尾改行は読み飛ばす。1 行でも解析できない場合はその行を Error とし、残りの行の処理を継続する。</summary>
    public static EventsJsonlReadResult Read(string jsonl)
    {
        ArgumentNullException.ThrowIfNull(jsonl);

        var events = new List<TimelineEvent>();
        var errors = new List<string>();
        var lines = jsonl.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var lineNumber = i + 1;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException ex)
            {
                errors.Add($"{lineNumber} 行目: JSON を解析できません: {ex.Message}");
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{lineNumber} 行目: JSON object が必要です（実際は {root.ValueKind}）。");
                    continue;
                }

                var missing = new List<string>();
                if (!root.TryGetProperty("schemaVersion", out var schemaElement)) missing.Add("schemaVersion");
                if (!root.TryGetProperty("id", out var idElement)) missing.Add("id");
                if (!root.TryGetProperty("seq", out var seqElement)) missing.Add("seq");
                if (!root.TryGetProperty("timestampMs", out var timestampElement)) missing.Add("timestampMs");
                if (!root.TryGetProperty("type", out var typeElement)) missing.Add("type");
                if (missing.Count > 0)
                {
                    errors.Add($"{lineNumber} 行目: 必須フィールドがありません: {string.Join(", ", missing)}");
                    continue;
                }

                // schemaVersion は Raw JSON で確認する。TimelineEvent の初期値 1 による
                // 「欠落を 1 と誤認」を避け、自動補完・Migration は行わない（契約 §23 / §25）。
                if (schemaElement.ValueKind != JsonValueKind.Number || !schemaElement.TryGetInt32(out var schemaVersion))
                {
                    errors.Add($"{lineNumber} 行目: 'schemaVersion' は整数であること: 実際は {schemaElement.ValueKind}。");
                    continue;
                }

                if (schemaVersion != SupportedSchemaVersion)
                {
                    errors.Add($"{lineNumber} 行目: schemaVersion {schemaVersion} は未対応です（対応: {SupportedSchemaVersion}）。Migration は行いません。");
                    continue;
                }

                if (idElement.ValueKind != JsonValueKind.String || !Guid.TryParse(idElement.GetString(), out var id))
                {
                    errors.Add($"{lineNumber} 行目: 'id' は GUID 文字列であること。");
                    continue;
                }

                if (!TryGetInt64(seqElement, out var seq))
                {
                    errors.Add($"{lineNumber} 行目: 'seq' は整数であること。");
                    continue;
                }

                if (!TryGetInt64(timestampElement, out var timestampMs))
                {
                    errors.Add($"{lineNumber} 行目: 'timestampMs' は整数であること。");
                    continue;
                }

                if (typeElement.ValueKind != JsonValueKind.String)
                {
                    errors.Add($"{lineNumber} 行目: 'type' は文字列であること。");
                    continue;
                }

                events.Add(new TimelineEvent
                {
                    SchemaVersion = schemaVersion,
                    Id = id,
                    Seq = seq,
                    TimestampMs = timestampMs,
                    Type = typeElement.GetString()!,
                    // JsonDocument を破棄するため Clone して保持する（payload 未定義は PayloadValidator が Error にする）。
                    Payload = root.TryGetProperty("payload", out var payload) ? payload.Clone() : default,
                });
            }
        }

        return new EventsJsonlReadResult { Events = events, Errors = errors };
    }

    public static EventsJsonlReadResult ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Read(File.ReadAllText(path));
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
        {
            return true;
        }

        value = 0;
        return false;
    }
}
