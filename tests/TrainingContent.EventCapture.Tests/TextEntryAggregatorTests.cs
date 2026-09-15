using Xunit;
using TrainingContent.EventCapture;

namespace TrainingContent.EventCapture.Tests;

/// <summary>
/// textEntry バースト集約（契約 §11.1 / §20）の検証。
/// 特に §11.1 禁止事項「Password 入力文字数の保存をしない」を最優先で守ること。
/// </summary>
public class TextEntryAggregatorTests
{
    [Fact]
    public void 連続キーは1イベントに集約される()
    {
        var aggregator = new TextEntryAggregator();
        Assert.Null(aggregator.Add(1000, isPassword: false, Target(), "Notepad", "メモ帳"));
        Assert.Null(aggregator.Add(1100, isPassword: false, Target(), "Notepad", "メモ帳"));

        var flushed = aggregator.Flush();
        Assert.NotNull(flushed);
        Assert.Equal(1000, flushed!.StartTimestampMs); // timestampMs はバースト先頭
        Assert.Equal(2, flushed.Payload.KeyCount);
        Assert.False(flushed.Payload.IsSensitive);
        Assert.Equal("Notepad", flushed.Payload.ProcessName);
        Assert.Equal("メモ帳", flushed.Payload.WindowTitle);
        Assert.Equal("社員番号", flushed.Payload.Target?.Name);
    }

    [Fact]
    public void 単発キーはkeyCount1で出力される()
    {
        var aggregator = new TextEntryAggregator();
        aggregator.Add(500, isPassword: false, Target(), "Notepad", "メモ帳");
        var flushed = aggregator.Flush();
        Assert.NotNull(flushed);
        Assert.Equal(1, flushed!.Payload.KeyCount);
    }

    [Fact]
    public void 隙間が2秒超ならバーストが分割される()
    {
        var aggregator = new TextEntryAggregator(gapMs: 2000);
        Assert.Null(aggregator.Add(1000, isPassword: false, Target(), "Notepad", "メモ帳"));

        // 1000 から 3001 は 2001ms 隙間 → 締め切られる
        var flushed = aggregator.Add(3001, isPassword: false, Target(), "Notepad", "メモ帳");
        Assert.NotNull(flushed);
        Assert.Equal(1000, flushed!.StartTimestampMs);
        Assert.Equal(1, flushed.Payload.KeyCount);

        var second = aggregator.Flush();
        Assert.NotNull(second);
        Assert.Equal(3001, second!.StartTimestampMs);
    }

    [Fact]
    public void ピッタリ2000msの隙間は分割しない()
    {
        var aggregator = new TextEntryAggregator(gapMs: 2000);
        Assert.Null(aggregator.Add(1000, isPassword: false, Target(), "Notepad", "メモ帳"));
        Assert.Null(aggregator.Add(3000, isPassword: false, Target(), "Notepad", "メモ帳")); // ギリギリ許容

        var flushed = aggregator.Flush();
        Assert.NotNull(flushed);
        Assert.Equal(2, flushed!.Payload.KeyCount);
    }

    [Fact]
    public void Passwordキーが含まれるバーストはkeyCountを出さない()
    {
        // 契約 §11.1 禁止事項: Password入力文字数の保存を禁止。
        // バースト途中でパスワード欄へ移ったケースも取りこぼさない（毎キー判定前提）。
        var aggregator = new TextEntryAggregator();
        aggregator.Add(1000, isPassword: false, Target(), "sample.exe", "ログイン");
        aggregator.Add(1100, isPassword: true, Target(), "sample.exe", "ログイン"); // 途中でパスワード欄へ
        aggregator.Add(1200, isPassword: false, Target(), "sample.exe", "ログイン");

        var flushed = aggregator.Flush();
        Assert.NotNull(flushed);
        Assert.Null(flushed!.Payload.KeyCount); // 文字数すら保存しない
        Assert.True(flushed.Payload.IsSensitive);
        Assert.Null(flushed.Payload.Target); // 認証画面の要素も出さない
    }

    [Fact]
    public void 全キーがPasswordならkeyCountnullで出力される()
    {
        var aggregator = new TextEntryAggregator();
        aggregator.Add(100, isPassword: true, Target(), "sample.exe", "ログイン");
        aggregator.Add(200, isPassword: true, Target(), "sample.exe", "ログイン");

        var flushed = aggregator.Flush();
        Assert.NotNull(flushed);
        Assert.Null(flushed!.Payload.KeyCount);
        Assert.True(flushed.Payload.IsSensitive);
    }

    [Fact]
    public void 空のFlushはnullを返す()
    {
        var aggregator = new TextEntryAggregator();
        Assert.Null(aggregator.Flush());
    }

    [Fact]
    public void Flush後は新しいバーストを開始できる()
    {
        var aggregator = new TextEntryAggregator();
        aggregator.Add(100, isPassword: false, Target(), "Notepad", "メモ帳");
        Assert.NotNull(aggregator.Flush());

        Assert.True(aggregator.IsEmpty);
        Assert.Null(aggregator.Add(9000, isPassword: false, Target(), "Notepad", "メモ帳"));
        var flushed = aggregator.Flush();
        Assert.NotNull(flushed);
        Assert.Equal(9000, flushed!.StartTimestampMs);
        Assert.Equal(1, flushed.Payload.KeyCount);
    }

    private static TargetPayload Target() => new("社員番号", "EMPLOYEE_ID", "Edit");
}
