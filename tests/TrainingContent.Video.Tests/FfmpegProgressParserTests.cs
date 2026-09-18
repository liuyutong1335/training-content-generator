using TrainingContent.Video.Renderer;
using Xunit;

namespace TrainingContent.Video.Tests;

public class FfmpegProgressParserTests
{
    [Fact]
    public void out_time_usから経過秒を算出する()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=120");
        parser.Feed("out_time_us=1500000");

        Assert.Equal(1.5, parser.OutTimeSeconds);
        Assert.False(parser.Finished);
    }

    [Fact]
    public void out_time_msはマイクロ秒として解釈する()
    {
        // ffmpeg の out_time_ms は歴史的経緯でマイクロ秒値が出る（out_time_us と同値）
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_ms=2000000");

        Assert.Equal(2.0, parser.OutTimeSeconds);
    }

    [Fact]
    public void out_time_usをout_time_msより優先する()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_ms=5000000");
        parser.Feed("out_time_us=1000000");

        Assert.Equal(1.0, parser.OutTimeSeconds);
    }

    [Fact]
    public void progress_end行で完了と判定する()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("out_time_us=3000000");
        parser.Feed("progress=continue");
        Assert.False(parser.Finished);

        parser.Feed("progress=end");
        Assert.True(parser.Finished);
    }

    [Theory]
    [InlineData("frame=120")]
    [InlineData("fps=32.4")]
    [InlineData("bitrate= 1024.5kbits/s")]
    [InlineData("")]
    [InlineData("out_time=00:00:05.000000")] // 時刻表記行は解析しない（out_time_us/ms のみ）
    public void 進捗秒に関係しない行は無視する(string line)
    {
        var parser = new FfmpegProgressParser();
        parser.Feed(line);

        Assert.Null(parser.OutTimeSeconds);
        Assert.False(parser.Finished);
    }
}
