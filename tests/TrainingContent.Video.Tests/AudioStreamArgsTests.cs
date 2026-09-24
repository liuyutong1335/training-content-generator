using TrainingContent.Video.Renderer;
using Xunit;

namespace TrainingContent.Video.Tests;

/// <summary>監査 m-4（音声なし録画と Title / Ending の concat 不一致 guard）の引数決定ロジック。</summary>
public class AudioStreamArgsTests
{
    [Fact]
    public void 音声あり録画はcopyで無追加入力()
    {
        var (input, output) = AudioStreamArgs.Build(recordingHasAudio: true);

        Assert.Equal("", input);
        Assert.Equal("-c:a copy", output);
    }

    [Fact]
    public void 音声なし録画はanullsrcの無音声トラックを補う()
    {
        var (input, output) = AudioStreamArgs.Build(recordingHasAudio: false);

        // Title / Ending カードと同一仕様（stereo / 44100Hz）の無音声トラックを追加し、
        // 出力は AAC にエンコードする（copy だと音声ストリーム自体が無いままになる）
        Assert.Contains("anullsrc=channel_layout=stereo:sample_rate=44100", input);
        Assert.Contains("-shortest", input);
        Assert.Equal("-c:a aac", output);
    }
}
