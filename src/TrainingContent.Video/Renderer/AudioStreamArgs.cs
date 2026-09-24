namespace TrainingContent.Video.Renderer;

/// <summary>
/// 録画の音声ストリーム有無に応じた ffmpeg の音声引数を決める（監査 m-4 対応）。
/// 音声なし録画（契約 §7 では正当）のままだと Title / Ending（anullsrc 付き AAC）と
/// concat demuxer でストリーム構成が不一致になり壊れ得るため、無音声トラックを補う。
/// </summary>
public static class AudioStreamArgs
{
    /// <summary>
    /// 録画の音声有無に応じ、字幕焼き込みステップの入力オプション（録画入力の後に置く）
    /// と出力音声コーデック引数を返す。無音声トラックの仕様（stereo / 44100Hz / AAC）は
    /// Title / Ending カード（<see cref="FfmpegVideoRenderer"/> の anullsrc）と同一にする。
    /// </summary>
    public static (string InputOptions, string OutputOptions) Build(bool recordingHasAudio) => recordingHasAudio
        ? ("", "-c:a copy")
        : ("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -shortest", "-c:a aac");
}
