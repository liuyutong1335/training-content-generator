using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Core.Tests;

/// <summary>
/// StepBuilder 正常系（契約 §9 / §10 / §11 / §12 / §13 / §14 / §15 / §26）。
/// </summary>
public class StepBuilderTests
{
    // --- fixture 全体（Event Type 混在・lifecycle・未知 Event・sensitive を含む） ---

    [Fact]
    public void Fixture_ConvertsToStepsWithoutErrors()
    {
        var result = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        Assert.Empty(result.Errors);
        Assert.Equal(7, result.Steps.Count);
        Assert.Equal(Enumerable.Range(1, 7), result.Steps.Select(s => s.Order));
    }

    [Fact]
    public void Fixture_ProducesExpectedActionsAndTargets()
    {
        var result = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        Assert.Equal(
            [
                StepActions.Click,
                StepActions.TextEntry,
                StepActions.SpecialKey,
                StepActions.Shortcut,
                StepActions.DoubleClick,
                StepActions.RightClick,
                StepActions.Click,
            ],
            result.Steps.Select(s => s.Action));

        // specialKey / shortcut は Target を持たない。uiElement が null の click も Target なし。
        Assert.Equal(
            ["新規申請", "社員番号", null, null, "申請行", "申請行", null],
            result.Steps.Select(s => s.Target));
    }

    [Fact]
    public void Fixture_CarriesScreenshotPath()
    {
        var result = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        Assert.Equal(
            [
                "screenshots/original/event-000002.png",
                null,
                null,
                null,
                "screenshots/original/event-000006.png",
                "screenshots/original/event-000007.png",
                null,
            ],
            result.Steps.Select(s => s.ScreenshotPath));
    }

    [Fact]
    public void Fixture_WarnsOnlyForUnknownAndSensitiveEvents()
    {
        var result = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        Assert.Equal(2, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("future.newEvent"));
        Assert.Contains(result.Warnings, w => w.Contains("sensitive", StringComparison.OrdinalIgnoreCase));
    }

    // --- 6種類の操作 Event 変換 ---

    [Theory]
    [InlineData(EventTypes.MouseClick, """{"x":10,"y":20,"uiElement":{"name":"登録"}}""", StepActions.Click, "登録", "「登録」をクリックします")]
    [InlineData(EventTypes.MouseDoubleClick, """{"x":10,"y":20,"uiElement":{"name":"登録"}}""", StepActions.DoubleClick, "登録", "「登録」をダブルクリックします")]
    [InlineData(EventTypes.MouseRightClick, """{"x":10,"y":20,"uiElement":{"name":"登録"}}""", StepActions.RightClick, "登録", "「登録」を右クリックします")]
    [InlineData(EventTypes.KeyboardTextEntry, """{"keyCount":8,"isSensitive":false,"target":{"name":"社員番号"}}""", StepActions.TextEntry, "社員番号", "「社員番号」に入力します")]
    [InlineData(EventTypes.KeyboardSpecialKey, """{"key":"Enter"}""", StepActions.SpecialKey, null, "Enter キーを押します")]
    [InlineData(EventTypes.KeyboardShortcut, """{"shortcut":"Ctrl+A"}""", StepActions.Shortcut, null, "Ctrl+A を実行します")]
    public void UserOperation_ConvertsToStep(string type, string payload, string expectedAction, string? expectedTarget, string expectedTitle)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(type, payload));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal(expectedAction, step.Action);
        Assert.Equal(expectedTarget, step.Target);
        Assert.Equal(expectedTitle, step.Title);
        Assert.Equal(1000L, step.StartMs);
    }

    // --- Target あり / なし の Title ---

    [Theory]
    [InlineData("""{"x":10,"y":20,"uiElement":null}""")]
    [InlineData("""{"x":10,"y":20}""")]
    [InlineData("""{"x":10,"y":20,"uiElement":{"automationId":"BTN_REGISTER"}}""")]
    [InlineData("""{"x":10,"y":20,"uiElement":{"name":"   "}}""")]
    public void MouseClick_WithoutTarget_UsesFixedTitle(string payload)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        var step = Assert.Single(result.Steps);
        Assert.Null(step.Target);
        Assert.Equal("クリックします", step.Title);
    }

    [Fact]
    public void MouseClick_WindowTitleIsNotUsedAsTarget()
    {
        var result = StepBuilder.Build(
            TimelineEventFactory.Single(EventTypes.MouseClick, """{"x":10,"y":20,"windowTitle":"申請登録","uiElement":null}"""));

        var step = Assert.Single(result.Steps);
        Assert.Null(step.Target);
        Assert.DoesNotContain("申請登録", step.Title);
    }

    [Fact]
    public void TextEntry_WithoutTarget_UsesFixedTitle()
    {
        var result = StepBuilder.Build(
            TimelineEventFactory.Single(EventTypes.KeyboardTextEntry, """{"keyCount":3,"isSensitive":false}"""));

        var step = Assert.Single(result.Steps);
        Assert.Null(step.Target);
        Assert.Equal("テキストを入力します", step.Title);
    }

    [Fact]
    public void TextEntry_DoesNotUseWindowTitleAsTarget()
    {
        var result = StepBuilder.Build(
            TimelineEventFactory.Single(EventTypes.KeyboardTextEntry, """{"keyCount":3,"isSensitive":false,"windowTitle":"ログイン"}"""));

        var step = Assert.Single(result.Steps);
        Assert.Null(step.Target);
        Assert.DoesNotContain("ログイン", step.Title);
    }

    // --- lifecycle Event は Warning なしで変換対象外 ---

    [Theory]
    [InlineData(EventTypes.RecordingStarted)]
    [InlineData(EventTypes.RecordingPaused)]
    [InlineData(EventTypes.RecordingResumed)]
    [InlineData(EventTypes.RecordingStopped)]
    public void LifecycleEvent_IsExcludedWithoutWarning(string type)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(type, "{}"));

        Assert.Empty(result.Steps);
        Assert.Empty(result.Warnings);
        Assert.Empty(result.Errors);
    }

    // --- Unknown Event は Warning + Continue、Step には変換しない ---

    [Fact]
    public void UnknownEvent_WarnsAndIsNotConverted()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Sequence(
            (EventTypes.MouseClick, 1000, """{"x":1,"y":2,"uiElement":{"name":"登録"}}"""),
            ("future.newEvent", 2000, """{"note":"unknown"}""")));

        Assert.Empty(result.Errors);
        Assert.Single(result.Steps);
        Assert.Contains("future.newEvent", Assert.Single(result.Warnings));
    }

    // --- Event Type は camelCase（大文字小文字を区別する） ---

    [Fact]
    public void EventType_IsCaseSensitive()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single("Mouse.Click", """{"x":1,"y":2}"""));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Steps);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public void KnownEventTypes_AreCamelCase()
    {
        Assert.NotEmpty(EventTypes.KnownTypes);
        Assert.All(EventTypes.KnownTypes, type =>
        {
            Assert.False(char.IsUpper(type[0]), $"Event Type の先頭は小文字（camelCase）であること: {type}");
            Assert.DoesNotContain('_', type);
        });
    }

    // --- sensitive textEntry は Warning + Continue、Step と Title を生成しない ---

    [Fact]
    public void SensitiveTextEntry_WarnsAndGeneratesNoStep()
    {
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(
                EventTypes.KeyboardTextEntry,
                seq: 1,
                timestampMs: 1000,
                """{"keyCount":null,"isSensitive":true,"target":{"name":"パスワード"}}"""),
        };

        var result = StepBuilder.Build(events);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Steps);
        Assert.Contains("sensitive", Assert.Single(result.Warnings), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SensitiveTextEntry_IsNotAnErrorWhenKeyCountIsNull()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(
            EventTypes.KeyboardTextEntry, """{"keyCount":null,"isSensitive":true}"""));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Steps);
    }

    // --- MVP リスト外の key / shortcut は Warning + Continue ---

    [Theory]
    [InlineData(EventTypes.KeyboardSpecialKey, """{"key":"F5"}""", "F5 キーを押します", "F5")]
    [InlineData(EventTypes.KeyboardShortcut, """{"shortcut":"Ctrl+P"}""", "Ctrl+P を実行します", "Ctrl+P")]
    public void OutOfMvpKeyOrShortcut_WarnsButStillConverts(string type, string payload, string expectedTitle, string warnedValue)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(type, payload));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Equal(expectedTitle, step.Title);
        Assert.Contains(warnedValue, Assert.Single(result.Warnings));
    }

    [Fact]
    public void MvpSpecialKeyList_MatchesContract()
    {
        Assert.Equal(9, StepBuilderTitles.MvpSpecialKeys.Count);
        foreach (var key in new[] { "Enter", "Tab", "Escape", "Backspace", "Delete", "Left", "Right", "Up", "Down" })
        {
            Assert.Contains(key, StepBuilderTitles.MvpSpecialKeys);
        }
    }

    [Fact]
    public void MvpShortcutList_MatchesContract()
    {
        Assert.Equal(5, StepBuilderTitles.MvpShortcuts.Count);
        foreach (var shortcut in new[] { "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+S", "Ctrl+Z" })
        {
            Assert.Contains(shortcut, StepBuilderTitles.MvpShortcuts);
        }
    }

    // --- SourceEventIds ---

    [Fact]
    public void SourceEventIds_KeepOriginalEventIds()
    {
        var id = Guid.NewGuid();
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 500, """{"x":1,"y":2}""", id),
        };

        var step = Assert.Single(StepBuilder.Build(events).Steps);

        Assert.Equal([id], step.SourceEventIds);
    }

    // --- Order は 1..N / 同一 timestampMs は seq 順 / 並べ替えしない ---

    [Fact]
    public void Order_IsSequentialFromOne()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Sequence(
            (EventTypes.MouseClick, 1000, """{"x":1,"y":2,"uiElement":{"name":"A"}}"""),
            (EventTypes.MouseClick, 2000, """{"x":1,"y":2,"uiElement":{"name":"B"}}"""),
            (EventTypes.MouseClick, 3000, """{"x":1,"y":2,"uiElement":{"name":"C"}}""")));

        Assert.Equal([1, 2, 3], result.Steps.Select(s => s.Order));
        Assert.Equal(["A", "B", "C"], result.Steps.Select(s => s.Target));
    }

    [Fact]
    public void SameTimestampMs_KeepsSeqOrder()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Sequence(
            (EventTypes.MouseDoubleClick, 12480, """{"x":1,"y":2,"uiElement":{"name":"申請行"}}"""),
            (EventTypes.MouseRightClick, 12480, """{"x":1,"y":2,"uiElement":{"name":"申請行"}}""")));

        Assert.Equal([StepActions.DoubleClick, StepActions.RightClick], result.Steps.Select(s => s.Action));
        Assert.Equal([12480L, 12480L], result.Steps.Select(s => s.StartMs));
    }

    [Fact]
    public void InputOrder_IsNotReorderedByTimestamp()
    {
        // seq 順（入力順）をそのまま Order にする。timestamp による並べ替えは行わない。
        var result = StepBuilder.Build(TimelineEventFactory.Sequence(
            (EventTypes.MouseClick, 5000, """{"x":1,"y":2,"uiElement":{"name":"先"}}"""),
            (EventTypes.MouseClick, 1000, """{"x":1,"y":2,"uiElement":{"name":"後"}}""")));

        Assert.Equal(["先", "後"], result.Steps.Select(s => s.Target));
        Assert.Equal([1, 2], result.Steps.Select(s => s.Order));
    }

    // --- EndMs は推測せず null ---

    [Fact]
    public void EndMs_IsAlwaysNull()
    {
        var result = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        Assert.NotEmpty(result.Steps);
        Assert.All(result.Steps, step => Assert.Null(step.EndMs));
    }

    // --- screenshotPath は Project-relative のまま TrainingStep へ引き継ぐ（契約 §10 / §18） ---

    [Fact]
    public void ScreenshotPath_IsCarriedToStep()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(
            EventTypes.MouseClick, """{"uiElement":{"name":"登録"},"screenshotPath":"screenshots/original/event-000012.png"}"""));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Equal("screenshots/original/event-000012.png", step.ScreenshotPath);
    }

    [Theory]
    [InlineData("""{"x":1,"y":2}""")]
    [InlineData("""{"x":1,"y":2,"screenshotPath":null}""")]
    public void ScreenshotPath_IsNullWhenUnset(string payload)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Null(step.ScreenshotPath);
    }

    // --- screenshotPath は mouse payload 専用。非 mouse Event では未知の追加フィールドとして無視する ---

    [Theory]
    [InlineData("""{"isSensitive":false,"screenshotPath":"screenshots/original/a.png"}""")]
    [InlineData("""{"isSensitive":false,"screenshotPath":"../secret.png"}""")]
    [InlineData("""{"isSensitive":false,"screenshotPath":"C:\\temp\\event.png"}""")]
    [InlineData("""{"isSensitive":false,"screenshotPath":"screenshots\\original\\a.png"}""")]
    public void TextEntryWithScreenshotPath_IsIgnoredWithoutErrorOrWarning(string payload)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardTextEntry, payload));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Null(step.ScreenshotPath);
    }

    [Theory]
    [InlineData(EventTypes.KeyboardSpecialKey, """{"key":"Enter","screenshotPath":"../secret.png"}""")]
    [InlineData(EventTypes.KeyboardShortcut, """{"shortcut":"Ctrl+A","screenshotPath":"C:\\temp\\event.png"}""")]
    public void NonMouseScreenshotPath_IsNeverCarriedToStep(string type, string payload)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(type, payload));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Null(step.ScreenshotPath);
    }

    [Fact]
    public void MousePayload_WithoutStepBuilderRequiredFields_Converts()
    {
        // x / y / button / clickCount / processName / windowTitle / uiElement は StepBuilder の必須項目ではない。
        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, "{}"));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Equal(StepActions.Click, step.Action);
        Assert.Null(step.Target);
    }

    [Fact]
    public void TextEntry_WithoutKeyCount_Converts()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(
            EventTypes.KeyboardTextEntry, """{"isSensitive":false,"target":{"name":"社員番号"}}"""));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Equal("「社員番号」に入力します", step.Title);
    }

    // --- 未知の追加 Payload フィールドは安全に無視 ---

    [Fact]
    public void UnknownPayloadFields_AreIgnored()
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(
            EventTypes.MouseClick,
            """{"x":1,"y":2,"futureField":{"a":1},"uiElement":{"name":"登録","futureProp":123}}"""));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Equal("登録", step.Target);
    }

    // --- Raw Event を書き換えない ---

    [Fact]
    public void RawEvents_AreNotModified()
    {
        var events = TimelineEventFactory.Sequence(
            (EventTypes.MouseClick, 1000, """{"x":1,"y":2,"uiElement":{"name":"登録"},"screenshotPath":"screenshots/original/event-000002.png"}"""),
            ("future.newEvent", 2000, """{"note":"unknown"}"""));

        static List<(Guid Id, long Seq, long TimestampMs, string Type, string Payload)> Snapshot(List<TimelineEvent> source) =>
            source.Select(e => (e.Id, e.Seq, e.TimestampMs, e.Type, e.Payload.GetRawText())).ToList();

        var before = Snapshot(events);

        _ = StepBuilder.Build(events);

        Assert.Equal(before, Snapshot(events));
    }
}
