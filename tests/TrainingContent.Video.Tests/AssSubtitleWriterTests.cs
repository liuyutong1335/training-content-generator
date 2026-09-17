using TrainingContent.Core.Models;
using TrainingContent.Video.Subtitle;
using TrainingContent.Video.Timeline;
using Xunit;

namespace TrainingContent.Video.Tests;

public class AssSubtitleWriterTests
{
    private static readonly IReadOnlyList<StepDisplayInterval> Intervals =
    [
        new(new TrainingStep { Order = 1, StartMs = 1_000, EndMs = 4_000, Title = "請求書を開く", Description = "メニューから選択" }, 1_000, 4_000),
        new(new TrainingStep { Order = 2, StartMs = 4_500, EndMs = 7_500, Title = "金額を入力", Caution = "税抜金額を入力" }, 4_500, 7_500),
    ];

    [Fact]
    public void Format行はDialogueの10フィールドを宣言する()
    {
        // 監査で発見したバグ（フィールド数不足で余剰フィールドがテキスト描画される）の回帰防止
        var ass = AssSubtitleWriter.Write(Intervals);
        var format = ass.Split('\n').Single(l => l.StartsWith("Format: Layer"));
        Assert.Equal(
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
            format.TrimEnd('\r'));
    }

    [Fact]
    public void Dialogue行数は区間数と一致する()
    {
        var ass = AssSubtitleWriter.Write(Intervals);
        Assert.Equal(2, ass.Split('\n').Count(l => l.StartsWith("Dialogue:")));
    }

    [Theory]
    [InlineData(1_000L, "0:00:01.000")]
    [InlineData(61_500L, "0:01:01.500")]
    [InlineData(3_723_456L, "1:02:03.456")]
    public void 時刻はh_mm_ss_mmm形式(long ms, string expected)
    {
        Assert.Equal(expected, AssSubtitleWriter.FormatTime(ms));
    }

    [Fact]
    public void Caution_ありのStepは強調スタイルに切り替える()
    {
        var ass = AssSubtitleWriter.Write(Intervals);
        var line2 = ass.Split('\n').Single(l => l.StartsWith("Dialogue:") && l.Contains("金額を入力"));
        Assert.Contains("{\\rStepCaution}", line2);
        Assert.Contains("税抜金額を入力", line2);
    }

    [Fact]
    public void テキスト中の波括弧はエスケープされて制御文字にならない()
    {
        var escaped = AssSubtitleWriter.EscapeText("合計 {0} 円");
        Assert.Equal("合計 ｛0｝ 円", escaped); // 全角化で置き換わる
        // xunit の DoesNotContain は文化依存比較（ja では全角/半角の幅違いを同値扱いする）ため
        // 序数比較（ordinal）で直接見る
        Assert.False(escaped.Contains('{', StringComparison.Ordinal));
        Assert.False(escaped.Contains('}', StringComparison.Ordinal));
    }

    [Fact]
    public void テキスト中の改行はNに変換される()
    {
        Assert.Equal("1行目\\N2行目", AssSubtitleWriter.EscapeText("1行目\n2行目"));
    }

    [Fact]
    public void Card_は中央寄せの1Dialogue()
    {
        var card = AssSubtitleWriter.WriteCard("タイトル", 3.0);
        var dialogue = card.Split('\n').Single(l => l.StartsWith("Dialogue:"));
        Assert.StartsWith("Dialogue: 0,0:00:00.000,0:00:03.000,Card", dialogue);
    }

    [Fact]
    public void Card_はフェードインアウトを持つ()
    {
        var card = AssSubtitleWriter.WriteCard("タイトル", 3.0);
        Assert.Contains("{\\fad(300,300)}", card, StringComparison.Ordinal);
    }
}
