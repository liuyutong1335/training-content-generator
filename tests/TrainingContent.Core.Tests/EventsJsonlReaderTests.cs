using Xunit;

namespace TrainingContent.Core.Tests;

/// <summary>
/// events.jsonl Reader（契約 §20 / §21）。Reader は「行の構文」だけを担当し、
/// seq 規則・Payload など契約の意味論は StepBuilder が担当する。
/// </summary>
public class EventsJsonlReaderTests
{
    private const string ClickLine =
        """{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111101","seq":1,"timestampMs":1500,"type":"mouse.click","payload":{"x":1240,"y":716}}""";

    [Fact]
    public void EmptyContent_ReturnsNoEventsAndNoErrors()
    {
        var result = EventsJsonlReader.Read("");

        Assert.Empty(result.Events);
        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
    }

    [Fact]
    public void BlankLines_AreSkipped()
    {
        var result = EventsJsonlReader.Read("\n\n   \n");

        Assert.Empty(result.Events);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ParsesCamelCaseFields()
    {
        var result = EventsJsonlReader.Read(ClickLine);

        var timelineEvent = Assert.Single(result.Events);
        Assert.Empty(result.Errors);
        Assert.Equal(1, timelineEvent.SchemaVersion);
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111101"), timelineEvent.Id);
        Assert.Equal(1L, timelineEvent.Seq);
        Assert.Equal(1500L, timelineEvent.TimestampMs);
        Assert.Equal("mouse.click", timelineEvent.Type);
        Assert.Equal("""{"x":1240,"y":716}""", timelineEvent.Payload.GetRawText());
    }

    [Fact]
    public void CrlfAndTrailingNewline_AreHandled()
    {
        var result = EventsJsonlReader.Read(ClickLine + "\r\n" + ClickLine.Replace("111111111101", "111111111102") + "\r\n");

        Assert.Equal(2, result.Events.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void MalformedLine_ReportsLineNumberAndContinues()
    {
        var result = EventsJsonlReader.Read(string.Join('\n',
            ClickLine,
            "not json",
            ClickLine.Replace("111111111101", "111111111102")));

        Assert.Equal(2, result.Events.Count);
        var error = Assert.Single(result.Errors);
        Assert.Contains("2 行目", error);
        Assert.Contains("JSON", error);
    }

    [Fact]
    public void SchemaVersionMissing_IsErrorAndOtherLinesContinue()
    {
        // Raw JSON に schemaVersion が無い場合、TimelineEvent の初期値 1 で補完しない（契約 §23）。
        var result = EventsJsonlReader.Read(string.Join('\n',
            """{"id":"11111111-1111-1111-1111-111111111101","seq":1,"timestampMs":100,"type":"mouse.click","payload":{}}""",
            ClickLine));

        Assert.Single(result.Events);
        var error = Assert.Single(result.Errors);
        Assert.Contains("1 行目", error);
        Assert.Contains("schemaVersion", error);
    }

    [Fact]
    public void SchemaVersionOtherThanSupported_IsError()
    {
        var result = EventsJsonlReader.Read(ClickLine.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal));

        Assert.Empty(result.Events);
        Assert.Contains(result.Errors, error => error.Contains("schemaVersion 2", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownEventType_IsReturnedUnfiltered()
    {
        var result = EventsJsonlReader.Read(
            """{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111109","seq":1,"timestampMs":100,"type":"future.newEvent","payload":{}}""");

        Assert.Empty(result.Errors);
        Assert.Equal("future.newEvent", Assert.Single(result.Events).Type);
    }

    [Fact]
    public void BlankType_IsNotJudgedByReader()
    {
        // blank Type の Error 判定は StepBuilder 側の責務。
        var result = EventsJsonlReader.Read(
            """{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111110","seq":1,"timestampMs":100,"type":"","payload":{}}""");

        Assert.Empty(result.Errors);
        Assert.Equal("", Assert.Single(result.Events).Type);
    }

    [Fact]
    public void DuplicateSeq_IsNotJudgedByReader()
    {
        // seq 規則の Error 判定は StepBuilder 側の責務。
        var result = EventsJsonlReader.Read(string.Join('\n',
            ClickLine,
            ClickLine.Replace("111111111101", "111111111102")));

        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Events.Count);
    }

    [Fact]
    public void ReadFile_ReadsFixture()
    {
        var result = EventsJsonlReader.ReadFile(StepBuilderFixture.EventsJsonlPath);

        Assert.Empty(result.Errors);
        Assert.Equal(StepBuilderFixture.EventsJsonlLines().Count, result.Events.Count);
        Assert.Equal(13, result.Events.Count);
    }

    [Fact]
    public void ReaderResult_CanBePassedDirectlyToStepBuilder()
    {
        var read = EventsJsonlReader.ReadFile(StepBuilderFixture.EventsJsonlPath);

        var fromEvents = StepBuilder.Build(read.Events);
        var fromJsonl = StepBuilder.BuildFromJsonl(StepBuilderFixture.EventsJsonlText());

        Assert.Equal(fromJsonl.Errors, fromEvents.Errors);
        Assert.Equal(
            fromJsonl.Steps.Select(s => (s.Order, s.Action, s.Title)),
            fromEvents.Steps.Select(s => (s.Order, s.Action, s.Title)));
    }
}
