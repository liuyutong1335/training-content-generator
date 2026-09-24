using TrainingContent.Core.Models;

namespace TrainingContent.Video;

/// <summary>
/// 動画合成の窓口（契約 §28: Video の Input = Recording + TrainingProject / Output = MP4）。
/// D の UI（ContentsView の生成ボタン）から呼ばれることを想定。
/// </summary>
public interface IVideoComposer
{
    /// <summary>
    /// 録画 MP4 に TrainingStep の字幕を焼き込み、Title Screen / Ending を付けて
    /// <c>output/training_video.mp4</c> を生成する（契約 §17・開発計画書 §20）。
    /// </summary>
    Task<VideoCompositionResult> ComposeAsync(
        VideoCompositionRequest request,
        VideoCompositionOptions? options = null,
        CancellationToken cancellationToken = default);
}

/// <summary>合成リクエスト。パスは実パスを渡す（project.json 内には契約 §18 により相対パスのみ保存）。</summary>
public sealed record VideoCompositionRequest
{
    /// <summary>入力録画（契約 §17 の <c>raw/recording.mp4</c>）の実パス。</summary>
    public required string RecordingPath { get; init; }

    /// <summary>出力先（契約 §17 の <c>output/training_video.mp4</c>）の実パス。</summary>
    public required string OutputPath { get; init; }

    /// <summary>入力 Project（Manual と同一 Source of Truth・契約 §0-6）。Steps と Recording.DurationMs を使用する。</summary>
    public required TrainingProject Project { get; init; }
}

/// <summary>合成オプション。未指定値は既定で動く（MVP では UI から触らない）。</summary>
public sealed record VideoCompositionOptions
{
    /// <summary>Title Screen の表示文。空なら Project.Title を使う。</summary>
    public string? TitleText { get; init; }

    /// <summary>Ending の表示文。</summary>
    public string EndingText { get; init; } = "ご視聴ありがとうございました";

    /// <summary>Title Screen の長さ（秒）。</summary>
    public double TitleSeconds { get; init; } = 3.0;

    /// <summary>Ending の長さ（秒）。</summary>
    public double EndingSeconds { get; init; } = 2.0;

    /// <summary>単発操作（EndMs=null）Step の既定表示秒数（契約 §14 の補完）。</summary>
    public double FallbackStepSeconds { get; init; } = 4.0;

    /// <summary>エンコーダ。既定は LGPL 版 ffmpeg に含まれる libopenh264。</summary>
    public string Encoder { get; init; } = "libopenh264";

    /// <summary>合成進捗の報告先。未指定なら報告しない。OverallProgress は 0.0〜1.0（単調増加）。</summary>
    public IProgress<VideoCompositionProgress>? Progress { get; init; }
}

/// <summary>合成処理の段階。</summary>
public enum VideoCompositionStage
{
    /// <summary>入力録画の解析（ffprobe）。</summary>
    AnalyzingInput,

    /// <summary>Step 字幕の焼き込み（録画を再エンコード）。</summary>
    BurningSubtitles,

    /// <summary>Title Screen の生成。</summary>
    RenderingTitle,

    /// <summary>Ending の生成。</summary>
    RenderingEnding,

    /// <summary>Title / 本編 / Ending の結合（再エンコード）。</summary>
    Concatenating,

    /// <summary>出力 MP4 の検証（ffprobe で Duration 実測）。</summary>
    Finalizing,
}

/// <summary>合成進捗。OverallProgress は 0.0〜1.0 で単調増加する（D の UI 表示用）。</summary>
public sealed record VideoCompositionProgress(
    VideoCompositionStage Stage,
    string StageDetail,
    double OverallProgress);

/// <summary>合成結果。DurationSeconds は出力 MP4 の実測値（ffprobe）。</summary>
public sealed record VideoCompositionResult(string OutputPath, double DurationSeconds);
