using System.Text.Json;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Core.Tests;

/// <summary>
/// fixture 自体が契約 §11.1 / §15 / §30 の禁止事項（実入力文字・Password・認証情報・
/// sensitive 入力の文字数）を持ち込まないことを固定する。
/// </summary>
public class FixtureSecurityTests
{
    /// <summary>内容そのものを保持しうる Field 名（値ではなく Field 名で判定する）。</summary>
    private static readonly IReadOnlySet<string> ContentBearingFieldNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text", "value", "input", "keys", "rawText", "typedText",
        "password", "passwd", "pwd", "credential", "credentials", "secret", "token", "clipboard",
    };

    [Fact]
    public void FixtureFile_ExistsAndIsNotEmpty()
    {
        Assert.True(File.Exists(StepBuilderFixture.EventsJsonlPath), StepBuilderFixture.EventsJsonlPath);
        Assert.NotEmpty(StepBuilderFixture.EventsJsonlLines());
    }

    [Fact]
    public void Fixture_HasNoContentBearingFieldNames()
    {
        var names = AllPropertyNames().Distinct().ToList();

        Assert.NotEmpty(names);
        Assert.DoesNotContain(names, name => ContentBearingFieldNames.Contains(name));
    }

    [Fact]
    public void Fixture_SensitiveTextEntry_HasNoKeyCount()
    {
        var payloads = Payloads(EventTypes.KeyboardTextEntry);

        Assert.NotEmpty(payloads);
        Assert.All(payloads, payload =>
        {
            if (payload.TryGetProperty("isSensitive", out var sensitive) && sensitive.GetBoolean())
            {
                Assert.True(payload.TryGetProperty("keyCount", out var keyCount));
                Assert.Equal(JsonValueKind.Null, keyCount.ValueKind);
            }
        });
    }

    [Fact]
    public void Fixture_KeepsExercisingSensitiveAndUnknownCases()
    {
        // fixture から sensitive / 未知 Event が消えると、禁止事項の検証が空洞化するため固定する。
        Assert.Contains(Payloads(EventTypes.KeyboardTextEntry), p => p.GetProperty("isSensitive").GetBoolean());
        Assert.Contains(AllEvents(), e => e.GetProperty("type").GetString() == "future.newEvent");
        // 未知 Event 以外は契約の MVP Event Type であること（fixture の陳腐化検出）。
        Assert.All(
            AllEvents().Where(e => e.GetProperty("type").GetString() != "future.newEvent"),
            e => Assert.True(EventTypes.IsKnown(e.GetProperty("type").GetString()!)));
    }

    [Fact]
    public void BuilderOutput_DoesNotLeakSensitiveTarget()
    {
        var result = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        var sensitiveTarget = Payloads(EventTypes.KeyboardTextEntry)
            .Where(p => p.GetProperty("isSensitive").GetBoolean())
            .Select(p => p.GetProperty("target").GetProperty("name").GetString())
            .ToList();

        Assert.NotEmpty(sensitiveTarget);
        Assert.All(result.Steps, step =>
        {
            Assert.DoesNotContain(sensitiveTarget, target => step.Title.Contains(target!, StringComparison.Ordinal));
            Assert.DoesNotContain(sensitiveTarget, target => step.Target == target);
        });
    }

    private static IEnumerable<JsonElement> AllEvents() =>
        StepBuilderFixture.EventsJsonlLines().Select(line => JsonSerializer.Deserialize<JsonElement>(line));

    private static IEnumerable<JsonElement> Payloads(string type) =>
        AllEvents()
            .Where(e => e.GetProperty("type").GetString() == type)
            .Select(e => e.GetProperty("payload"));

    private static IEnumerable<string> AllPropertyNames() =>
        AllEvents().SelectMany(PropertyNames);

    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }
}
