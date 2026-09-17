namespace TrainingContent.Video.Renderer;

/// <summary>
/// ffmpeg の <c>-progress pipe:1</c> 出力（<c>key=value</c> 行列）を解析し、経過秒を算出する（pure logic）。
/// 出力例: <c>frame=120</c> / <c>out_time_us=1500000</c> / <c>progress=continue</c> / <c>progress=end</c>
/// </summary>
public sealed class FfmpegProgressParser
{
    // out_time_ms は ffmpeg の歴史的経緯でマイクロ秒単位の値が出る（out_time_us と同値）。
    // 両方来た場合は out_time_us を優先する。
    private long? _outTimeUs;
    private long? _outTimeMs;

    /// <summary>解析済みの出力到達時刻（秒）。まだ出ていなければ null。</summary>
    public double? OutTimeSeconds =>
        _outTimeUs is { } us ? us / 1_000_000.0
        : _outTimeMs is { } ms ? ms / 1_000_000.0
        : null;

    /// <summary>progress:end 行を受信したか（処理が最終行まで到達した）。</summary>
    public bool Finished { get; private set; }

    /// <summary>1 行を取り込む。進捗に関係ない行は無視される。</summary>
    public void Feed(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return;
        }

        var key = line[..separator].Trim();
        var value = line[(separator + 1)..].Trim();

        switch (key)
        {
            case "out_time_us":
                if (long.TryParse(value, out var us))
                {
                    _outTimeUs = us;
                }
                break;

            case "out_time_ms":
                if (long.TryParse(value, out var ms))
                {
                    _outTimeMs = ms;
                }
                break;

            case "progress":
                Finished = value == "end";
                break;
        }
    }
}
