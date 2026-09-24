using System.Text.Json;
using Xunit;
using TrainingContent.EventCapture;

namespace TrainingContent.EventCapture.Tests;

/// <summary>events.jsonl の形式（契約 §8 / §20 / §21）の検証。</summary>
public class EventTimelineWriterTests : IDisposable
{
    private readonly string _directory;
    private readonly EventTimelineWriter _writer;

    public EventTimelineWriterTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "event-capture-tests", Guid.NewGuid().ToString());
        _writer = new EventTimelineWriter(Path.Combine(_directory, "events.jsonl"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void seqは1始まりで単調増加する()
    {
        var (seq1, _) = _writer.Append("recording.started", 0, new { });
        var (seq2, _) = _writer.Append("mouse.click", 100, new MousePayload(1, 2, "left", 1, null, null, null, null));

        Assert.Equal(1, seq1);
        Assert.Equal(2, seq2);
        Assert.Equal(2, _writer.Count);
    }

    [Fact]
    public void JSON行はcamelCaseかつ1イベント1行()
    {
        _writer.Append("recording.started", 0, new { });
        _writer.Append("keyboard.specialKey", 1200, new SpecialKeyPayload("Enter"));

        var lines = File.ReadAllLines(_writer.FilePath);
        Assert.Equal(2, lines.Length); // 1 Event = 1 Line

        using var doc = JsonDocument.Parse(lines[1]);
        var root = doc.RootElement;
        Assert.True(root.TryGetProperty("schemaVersion", out _));
        Assert.True(root.TryGetProperty("timestampMs", out _)); // camelCase（TimestampMs でない）
        Assert.True(root.TryGetProperty("type", out _));
        Assert.True(root.TryGetProperty("payload", out _));
        Assert.True(root.TryGetProperty("id", out _));
        Assert.True(root.TryGetProperty("seq", out _));
        Assert.Equal("Enter", root.GetProperty("payload").GetProperty("key").GetString());
        Assert.Equal(1200, root.GetProperty("timestampMs").GetInt64());
    }

    [Fact]
    public void 日本語はエスケープされずそのまま出力される()
    {
        _writer.Append("mouse.click", 500, new MousePayload(
            10, 20, "left", 1, "Notepad", "メモ帳",
            new UiElementPayload("テキスト エディター", null, "Document", "RichEditD2DPT", true, false,
                new BoundsPayload(0, 0, 100, 100)),
            "screenshots/original/event-000001.png"));

        var line = File.ReadLines(_writer.FilePath).Single();
        Assert.Contains("メモ帳", line);
        Assert.DoesNotContain("\\u30E1", line);
    }

    [Fact]
    public void payloadがnullの場合は空オブジェクトになる()
    {
        _writer.Append("recording.stopped", 5000, null);
        var line = File.ReadLines(_writer.FilePath).Single();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("payload").ValueKind);
        Assert.False(doc.RootElement.GetProperty("payload").EnumerateObject().Any());
    }

    [Fact]
    public void idはGUID形式()
    {
        _writer.Append("recording.started", 0, new { });
        var line = File.ReadLines(_writer.FilePath).Single();
        using var doc = JsonDocument.Parse(line);
        Assert.True(Guid.TryParse(doc.RootElement.GetProperty("id").GetString(), out _));
    }

    [Fact]
    public void screenshotPathは相対パスのまま出力される()
    {
        // 契約 §18: JSON 内に絶対パスを保存しない（ScreenshotCapture が相対パスを返す前提）。
        _writer.Append("mouse.click", 500, new MousePayload(
            10, 20, "left", 1, "Notepad", "メモ帳", null,
            "screenshots/original/event-000001.png"));

        var line = File.ReadLines(_writer.FilePath).Single();
        using var doc = JsonDocument.Parse(line);
        var path = doc.RootElement.GetProperty("payload").GetProperty("screenshotPath").GetString();
        Assert.Equal("screenshots/original/event-000001.png", path);
    }

    [Fact]
    public void 既存ファイルへの追加開始ではseqを前回の最終値から続ける()
    {
        // 監査 NEW-1: re-record で既存 events.jsonl に新 session を開始すると
        // seq が 1 から再開し、契約 §8.1（Seq start 1 / 単調増加）に違反する。
        _writer.Append("recording.started", 0, new { });
        _writer.Append("mouse.click", 100, new MousePayload(1, 2, "left", 1, null, null, null, null));
        _writer.Append("recording.stopped", 900, new { });

        var secondWriter = new EventTimelineWriter(_writer.FilePath);
        var (seq, _) = secondWriter.Append("recording.started", 1000, new { });

        Assert.Equal(4, seq); // 前回の最終 seq 3 の次
        Assert.Equal(4, secondWriter.Count);

        var lines = File.ReadAllLines(_writer.FilePath);
        var seqs = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("seq").GetInt64()).ToArray();
        Assert.Equal(new[] { 1L, 2L, 3L, 4L }, seqs); // ファイル全体で単調増加
    }

    [Fact]
    public void seq読み取り時に壊れた最終行があっても新規開始として動作する()
    {
        // 破損行からの復旧より契約違反（seq 重複）を避けるのは難しいため、
        // 読めなければ 0 から再開する（防御側の仕様を固定するテスト）。
        File.WriteAllLines(_writer.FilePath, ["not json"]);

        var writer = new EventTimelineWriter(_writer.FilePath);
        var (seq, _) = writer.Append("recording.started", 0, new { });

        Assert.Equal(1, seq);
    }

    [Fact]
    public void クラッシュで改行なしに途切れた最終行のあとでもseqは最大値から続き壊れた行は分離される()
    {
        // 監査 MIN-1: 最終行だけを見る方式だと、有効行の後に半壊行（改行なし）が
        // 残った events.jsonl の追記で seq が 1 から再開（重複）し、追記内容が
        // 半壊行に連結されて 1 行が完全破損していた。
        var valid1 = """{"schemaVersion":1,"id":"00000000-0000-0000-0000-000000000001","seq":1,"timestampMs":0,"type":"recording.started","payload":{}}""";
        var valid2 = """{"schemaVersion":1,"id":"00000000-0000-0000-0000-000000000002","seq":2,"timestampMs":100,"type":"mouse.click","payload":{}}""";
        var torn = """{"schemaVersion":1,"id":"00000000-0000-0000-0000-000000000003","seq":3,"timestampMs":200,"type":"mo""";
        File.WriteAllLines(_writer.FilePath, [valid1, valid2]);
        File.AppendAllText(_writer.FilePath, torn); // 改行なしで途切れた状態を再現

        var secondWriter = new EventTimelineWriter(_writer.FilePath);
        var (seq, _) = secondWriter.Append("recording.stopped", 900, new { });

        // 読み取れた最大 seq 2 の次 = 3（半壊行の「seq:3」は読めないため再使用になる。
        // 1 からやり直して 1・2 と重複するより、ベストエフォートで最大可読値を継続する方が良い）。
        Assert.Equal(3, seq);
        Assert.Equal(3, secondWriter.Count);

        var lines = File.ReadAllLines(_writer.FilePath);
        Assert.Equal(4, lines.Length); // 半壊行は 1 行として分離され、追記と連結されない

        // 追記された行は JSON として完全に解析できること（壊れた行に連結されていないこと）。
        using var doc = JsonDocument.Parse(lines[^1]);
        Assert.Equal(3, doc.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal("recording.stopped", doc.RootElement.GetProperty("type").GetString());
    }
}
