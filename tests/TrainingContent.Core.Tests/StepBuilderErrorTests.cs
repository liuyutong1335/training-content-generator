using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Core.Tests;

/// <summary>
/// StepBuilder 異常系（契約 §8.1 / §15 / §22 / §26）。
/// Error が 1 件でもある場合は部分的な TrainingStep を返さない。
/// </summary>
public class StepBuilderErrorTests
{
    private const string ValidClick = """{"x":1,"y":2,"uiElement":{"name":"登録"}}""";

    private static void AssertFatal(StepBuildResult result, string expectedFragment)
    {
        Assert.Empty(result.Steps);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, error => error.Contains(expectedFragment, StringComparison.Ordinal));
    }

    // --- 入力行の構文エラー（Reader が検出） ---

    [Fact]
    public void MalformedJson_IsError()
    {
        var result = StepBuilder.BuildFromJsonl(
            """{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111101","seq":1,"timestampMs":0,"type":"mouse.click""");

        AssertFatal(result, "JSON");
    }

    [Fact]
    public void NonObjectLine_IsError()
    {
        var result = StepBuilder.BuildFromJsonl("[]");

        AssertFatal(result, "object");
    }

    [Fact]
    public void InvalidGuidString_IsError()
    {
        var result = StepBuilder.BuildFromJsonl(
            """{"schemaVersion":1,"id":"not-a-guid","seq":1,"timestampMs":0,"type":"mouse.click","payload":{"x":1,"y":2}}""");

        AssertFatal(result, "GUID");
    }

    [Fact]
    public void MissingRequiredLineField_IsError()
    {
        var result = StepBuilder.BuildFromJsonl(
            """{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111101","timestampMs":0,"type":"mouse.click","payload":{"x":1,"y":2}}""");

        AssertFatal(result, "seq");
    }

    // --- Event 構造の契約違反（StepBuilder が検出） ---

    [Fact]
    public void EmptyGuid_IsError()
    {
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 1000, ValidClick, Guid.Empty),
        };

        AssertFatal(StepBuilder.Build(events), "Id");
    }

    [Fact]
    public void DuplicateId_IsError()
    {
        var id = Guid.NewGuid();
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 1000, ValidClick, id),
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 2, timestampMs: 2000, ValidClick, id),
        };

        AssertFatal(StepBuilder.Build(events), "重複");
    }

    [Fact]
    public void SeqZero_IsError()
    {
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 0, timestampMs: 1000, ValidClick),
        };

        AssertFatal(StepBuilder.Build(events), "Seq は 1 以上");
    }

    [Fact]
    public void DuplicateSeq_IsError()
    {
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 1000, ValidClick),
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 2000, ValidClick),
        };

        var result = StepBuilder.Build(events);

        Assert.Empty(result.Steps);
        Assert.Contains(result.Errors, error => error.Contains("Seq") && error.Contains("重複"));
    }

    [Fact]
    public void NonMonotonicSeq_IsError()
    {
        // seq を並べ替えて不正入力を修正しない（1 → 3 → 2 はそのまま静的にエラー）
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 1000, ValidClick),
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 3, timestampMs: 2000, ValidClick),
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 2, timestampMs: 3000, ValidClick),
        };

        AssertFatal(StepBuilder.Build(events), "単調増加");
    }

    [Fact]
    public void OutOfOrderSeqFile_IsNotSorted_AndYieldsNoSteps()
    {
        var result = StepBuilder.BuildFromJsonl(string.Join('\n',
            """
            {"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111101","seq":3,"timestampMs":3000,"type":"mouse.click","payload":{"x":1,"y":2}}
            """,
            """
            {"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111102","seq":1,"timestampMs":1000,"type":"mouse.click","payload":{"x":1,"y":2}}
            """,
            """
            {"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111103","seq":2,"timestampMs":2000,"type":"mouse.click","payload":{"x":1,"y":2}}
            """));

        AssertFatal(result, "単調増加");
    }

    [Fact]
    public void NegativeTimestampMs_IsError()
    {
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: -1, ValidClick),
        };

        AssertFatal(StepBuilder.Build(events), "timestampMs");
    }

    [Fact]
    public void BlankType_IsError()
    {
        var events = new List<TimelineEvent>
        {
            TimelineEventFactory.Create("", seq: 1, timestampMs: 1000, ValidClick),
        };

        AssertFatal(StepBuilder.Build(events), "Type");
    }

    // --- Payload の必須欠落 / 型不一致 ---

    [Fact]
    public void MissingPayload_IsError()
    {
        var events = new List<TimelineEvent>
        {
            new() { Id = Guid.NewGuid(), Seq = 1, TimestampMs = 1000, Type = EventTypes.MouseClick },
        };

        AssertFatal(StepBuilder.Build(events), "payload");
    }

    [Fact]
    public void PayloadNotObject_IsError()
    {
        AssertFatal(StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, "5")), "object");
    }

    [Fact]
    public void MissingRequiredPayloadField_IsError()
    {
        // x / y などは必須ではないが、uiElement が object でも null でもなければ明確な型不一致。
        AssertFatal(StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, """{"uiElement":"登録"}""")), "uiElement");
    }

    [Fact]
    public void PayloadFieldTypeMismatch_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, """{"x":"10","y":20}""")),
            "number");
    }

    [Fact]
    public void TextEntryWithoutIsSensitive_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardTextEntry, """{"keyCount":3}""")),
            "isSensitive");
    }

    [Fact]
    public void TextEntryWithNonBooleanIsSensitive_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardTextEntry, """{"keyCount":3,"isSensitive":"no"}""")),
            "isSensitive");
    }

    [Fact]
    public void TextEntryWithNegativeKeyCount_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardTextEntry, """{"keyCount":-1,"isSensitive":false}""")),
            "keyCount");
    }

    [Fact]
    public void SpecialKeyWithoutKey_IsError()
    {
        AssertFatal(StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardSpecialKey, "{}")), "'key'");
    }

    [Fact]
    public void SpecialKeyWithBlankKey_IsError()
    {
        AssertFatal(StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardSpecialKey, """{"key":"   "}""")), "'key'");
    }

    [Fact]
    public void ShortcutWithoutShortcut_IsError()
    {
        AssertFatal(StepBuilder.Build(TimelineEventFactory.Single(EventTypes.KeyboardShortcut, "{}")), "shortcut");
    }

    // --- sensitive textEntry の keyCount が null 以外なら Error（文字数も保存禁止） ---

    [Fact]
    public void SensitiveTextEntryWithKeyCount_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(
                EventTypes.KeyboardTextEntry, """{"keyCount":8,"isSensitive":true}""")),
            "keyCount");
    }

    // --- screenshotPath の Path Rule（契約 §18）。自動修正しない ---

    [Theory]
    [InlineData("""{"x":1,"y":2,"screenshotPath":""}""")]
    [InlineData("""{"x":1,"y":2,"screenshotPath":"   "}""")]
    public void ScreenshotPathBlank_IsError(string payload)
    {
        AssertFatal(StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload)), "screenshotPath");
    }

    [Theory]
    [InlineData("""{"x":1,"y":2,"screenshotPath":"C:\\temp\\event.png"}""")]
    [InlineData("""{"x":1,"y":2,"screenshotPath":"\\\\server\\share\\event.png"}""")]
    public void ScreenshotPathAbsolute_IsError(string payload)
    {
        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        AssertFatal(result, "絶対パス");
    }

    [Fact]
    public void ScreenshotPathBackslash_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(
                EventTypes.MouseClick, """{"x":1,"y":2,"screenshotPath":"screenshots\\original\\event.png"}""")),
            "パス区切り");
    }

    [Fact]
    public void ScreenshotPath_IsNotAutoCorrected()
    {
        const string invalid = "screenshots\\original\\event.png";
        var payload = "{\"x\":1,\"y\":2,\"screenshotPath\":\"" + invalid.Replace("\\", "\\\\") + "\"}";

        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        // '/' 形式へ自動修正した Step を作らない（Step も生成しない）。理由は Error として報告する。
        Assert.Empty(result.Steps);
        Assert.Contains(result.Errors, error => error.Contains("screenshotPath", StringComparison.Ordinal)
            && error.Contains("パス区切り", StringComparison.Ordinal));

        // 受け取った path 全文は Error に含めない（drive letter・ユーザー名・server/share・
        // ローカルディレクトリ・ファイル名を漏らさないため）。
        Assert.DoesNotContain(result.Errors, error => error.Contains(invalid, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("screenshots/../secret.png")]
    [InlineData("./screenshots/a.png")]
    [InlineData("screenshots/./a.png")]
    [InlineData("/screenshots/a.png")]
    public void ScreenshotPathWithTraversalOrRootedSegment_IsError(string path)
    {
        var payload = $"{{\"x\":1,\"y\":2,\"screenshotPath\":\"{path}\"}}";

        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        Assert.Empty(result.Steps);
        Assert.Contains(result.Errors, error => error.Contains("screenshotPath", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains(path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("screenshots/original/a.png")]
    [InlineData("screenshots/original/event-000012.png")]
    [InlineData("screenshots/v1.2/a.png")]
    [InlineData("screenshots/.hidden/a.png")]
    public void ScreenshotPathRelativeWithoutDotSegment_IsAccepted(string path)
    {
        var payload = $"{{\"x\":1,\"y\":2,\"screenshotPath\":\"{path}\"}}";

        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        var step = Assert.Single(result.Steps);
        Assert.Empty(result.Errors);
        Assert.Equal(path, step.ScreenshotPath);
    }

    [Fact]
    public void ScreenshotPathWithTraversal_IsNotNormalized()
    {
        const string traversal = "screenshots/../secret.png";
        var payload = $"{{\"x\":1,\"y\":2,\"screenshotPath\":\"{traversal}\"}}";

        var result = StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, payload));

        // 正規化して "secret.png" に直したり、Project 外を指す値をそのまま採用したりしない。
        Assert.Empty(result.Steps);
        Assert.Contains(result.Errors, error => error.Contains("セグメント", StringComparison.Ordinal));

        // 元の path も、正規化後の名前も Error に出さない。
        Assert.DoesNotContain(result.Errors, error => error.Contains(traversal, StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("secret.png", StringComparison.Ordinal));
    }

    // --- path 非漏洩: screenshotPath の Error に path 実値を含めない ---
    // 断片はテスト専用の dummy 名であり、実在の人物名・server 名・share 名ではない。

    private static readonly string[] ForbiddenPathFragments =
    [
        "yamada", "fileserver", "share01", "localuser", "private",
        "secret.png", "event.png", @"C:\", @"\\", "/home/",
    ];

    [Theory]
    [InlineData(@"C:\Users\yamada\private\event.png", "絶対パス")]
    [InlineData(@"\\fileserver\share01\private\event.png", "絶対パス")]
    [InlineData(@"screenshots\original\event.png", "パス区切り")]
    [InlineData("../secret.png", "セグメント")]
    [InlineData("screenshots/../secret.png", "セグメント")]
    [InlineData("/home/localuser/private/event.png", "絶対パス")]
    public void ScreenshotPathError_DoesNotLeakPathValue(string path, string expectedReason)
    {
        var payload = "{\"x\":1,\"y\":2,\"screenshotPath\":\"" + path.Replace("\\", "\\\\") + "\"}";
        var timelineEvent = TimelineEventFactory.Create(EventTypes.MouseClick, seq: 1, timestampMs: 1000, payloadJson: payload);
        var payloadBefore = timelineEvent.Payload.GetRawText();

        var result = StepBuilder.Build([timelineEvent]);

        Assert.True(result.HasErrors);
        Assert.Empty(result.Steps);

        // field 名と理由（および event type / seq の label）は残し、利用者が修正箇所を特定できるようにする。
        Assert.Contains(result.Errors, error => error.Contains("screenshotPath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains(expectedReason, StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains(EventTypes.MouseClick, StringComparison.Ordinal));

        // path 実値（drive letter・ユーザー名・server/share・ディレクトリ・ファイル名）は含めない。
        Assert.DoesNotContain(result.Errors, error => error.Contains(path, StringComparison.Ordinal));
        Assert.All(ForbiddenPathFragments, fragment =>
            Assert.DoesNotContain(result.Errors, error => error.Contains(fragment, StringComparison.Ordinal)));

        // 入力 Event / payload は変更しない（Raw Event を書き換えない）。
        Assert.Equal(payloadBefore, timelineEvent.Payload.GetRawText());
        Assert.Equal(EventTypes.MouseClick, timelineEvent.Type);
        Assert.Equal(1, timelineEvent.Seq);
        Assert.Equal(1000, timelineEvent.TimestampMs);
    }

    [Fact]
    public void ScreenshotPathTypeMismatch_IsError()
    {
        AssertFatal(
            StepBuilder.Build(TimelineEventFactory.Single(EventTypes.MouseClick, """{"x":1,"y":2,"screenshotPath":123}""")),
            "screenshotPath");
    }

    // --- schemaVersion（契約 §23）。Raw JSON で確認し、自動補完・Migration しない ---

    [Fact]
    public void SchemaVersionMissing_IsError()
    {
        var result = StepBuilder.BuildFromJsonl(
            """{"id":"11111111-1111-1111-1111-111111111101","seq":1,"timestampMs":1000,"type":"mouse.click","payload":{"x":1,"y":2}}""");

        AssertFatal(result, "schemaVersion");
    }

    [Fact]
    public void SchemaVersionNotInteger_IsError()
    {
        var result = StepBuilder.BuildFromJsonl(
            """{"schemaVersion":"1","id":"11111111-1111-1111-1111-111111111101","seq":1,"timestampMs":1000,"type":"mouse.click","payload":{"x":1,"y":2}}""");

        AssertFatal(result, "schemaVersion");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void SchemaVersionUnsupported_IsError(int schemaVersion)
    {
        var result = StepBuilder.BuildFromJsonl(
            $"{{\"schemaVersion\":{schemaVersion},\"id\":\"11111111-1111-1111-1111-111111111101\",\"seq\":1,\"timestampMs\":1000,\"type\":\"mouse.click\",\"payload\":{{\"x\":1,\"y\":2}}}}");

        AssertFatal(result, $"schemaVersion {schemaVersion}");
        Assert.Contains(result.Errors, error => error.Contains("Migration"));
    }

    [Fact]
    public void SchemaVersionOne_IsAccepted()
    {
        var result = StepBuilder.BuildFromJsonl(
            """{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111101","seq":1,"timestampMs":1000,"type":"mouse.click","payload":{"x":1,"y":2}}""");

        Assert.Empty(result.Errors);
        Assert.Single(result.Steps);
    }

    // --- Error がある場合は部分的な TrainingStep を返さない ---

    [Fact]
    public void AnyError_ReturnsNoPartialSteps()
    {
        // fixture の正常な複数行のうち 1 行だけを壊し、Step が 1 件も返らないことを確認する。
        var malformed = StepBuilderFixture.EventsJsonlText().Replace("\"x\":1240", "\"x\":", StringComparison.Ordinal);

        var result = StepBuilder.BuildFromJsonl(malformed);

        Assert.Empty(result.Steps);
        Assert.NotEmpty(result.Errors);
    }
}
