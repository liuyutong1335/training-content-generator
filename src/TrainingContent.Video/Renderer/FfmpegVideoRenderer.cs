using System.Diagnostics;
using TrainingContent.Core.Models;
using TrainingContent.Video.Subtitle;
using TrainingContent.Video.Timeline;

namespace TrainingContent.Video.Renderer;

/// <summary>
/// FFmpeg プロセスで動画を合成する IVideoComposer 実装（開発計画書 §12 Renderer 相当）。
/// 構成（契約 §20）: Title Screen → 録画映像（Step 字幕焼き込み）→ Ending。
/// エンコーダは既定で libopenh264（LGPL 版 ffmpeg に含まれる。libx264 は GPL なので無い）。
/// </summary>
public sealed class FfmpegVideoRenderer : IVideoComposer
{
    private readonly string _ffmpegPath;

    /// <summary>使用する ffmpeg.exe のパス（検証ツールが fixture 生成に使う）。</summary>
    public string FfmpegPath => _ffmpegPath;

    /// <param name="ffmpegPath">ffmpeg.exe のパス。null なら環境変数 TCS_FFMPEG、次にリポジトリの tools/ffmpeg/bin を探す。</param>
    public FfmpegVideoRenderer(string? ffmpegPath = null)
    {
        _ffmpegPath = ffmpegPath
                      ?? Environment.GetEnvironmentVariable("TCS_FFMPEG")
                      ?? LocateFfmpeg()
                      ?? throw new FileNotFoundException(
                          "ffmpeg.exe が見つかりません。tools/get-ffmpeg.ps1 を実行するか、ffmpegPath / 環境変数 TCS_FFMPEG で指定してください。");
    }

    public async Task<VideoCompositionResult> ComposeAsync(
        VideoCompositionRequest request,
        VideoCompositionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new VideoCompositionOptions();
        if (!File.Exists(request.RecordingPath))
        {
            throw new FileNotFoundException($"録画ファイルがありません: {request.RecordingPath}");
        }

        var workDir = Directory.CreateTempSubdirectory("tcs-video-").FullName;
        try
        {
            // ---- 0. 入力解析: 出力サイズ/fps は録画に合わせる（concat の一致要件）----
            Report(options, VideoCompositionStage.AnalyzingInput, "録画を解析しています…", 0.0);
            var source = ProbeVideoInfo(request.RecordingPath);
            var (w, h, fps) = source;
            var sourceSeconds = ProbeDurationSeconds(request.RecordingPath);

            // ---- 1. メイン: Step 字幕の焼き込み（Canonical Timeline のまま = シフト不要）----
            var intervals = StepTimelineBuilder.Build(
                request.Project.Steps,
                request.Project.Recording?.DurationMs ?? 0,
                options.FallbackStepSeconds);
            var mainAss = Path.Combine(workDir, "steps.ass");
            File.WriteAllText(mainAss, AssSubtitleWriter.Write(intervals, w, h), new System.Text.UTF8Encoding(false));

            var main = Path.Combine(workDir, "main.mp4");
            await RunAsync(
                $"-y -i \"{request.RecordingPath}\" -vf \"ass=steps.ass\" -c:v {options.Encoder} -pix_fmt yuv420p -c:a copy \"{main}\"",
                workDir,
                new RunProgressContext(VideoCompositionStage.BurningSubtitles, "Step 字幕を焼き込んでいます…", BaseProgress.BurningSubtitles, BaseProgress.RenderingTitle - BaseProgress.BurningSubtitles, sourceSeconds),
                options,
                cancellationToken);

            // ---- 2. Title Screen / Ending（契約 §20）----
            var titleText = string.IsNullOrWhiteSpace(options.TitleText) ? request.Project.Title : options.TitleText;
            var title = await RenderCardAsync(titleText, options.TitleSeconds, w, h, fps, options, workDir, "title", cancellationToken);
            var ending = await RenderCardAsync(options.EndingText, options.EndingSeconds, w, h, fps, options, workDir, "ending", cancellationToken);

            // ---- 3. 結合（録画側の音声パラメータに合わせて再エンコードして繋ぐ。copy は音声仕様不一致で壊れ得る）----
            var listPath = Path.Combine(workDir, "concat.txt");
            File.WriteAllLines(listPath, new[] { title, main, ending }.Select(p => $"file '{p.Replace("\\", "/")}'"));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(request.OutputPath))!);
            await RunAsync(
                $"-y -f concat -safe 0 -i \"{listPath}\" -c:v {options.Encoder} -pix_fmt yuv420p -c:a aac \"{request.OutputPath}\"",
                workDir,
                new RunProgressContext(VideoCompositionStage.Concatenating, "動画を結合しています…", BaseProgress.Concatenating, BaseProgress.Finalizing - BaseProgress.Concatenating, sourceSeconds + options.TitleSeconds + options.EndingSeconds),
                options,
                cancellationToken);

            Report(options, VideoCompositionStage.Finalizing, "出力を確認しています…", BaseProgress.Finalizing);
            var duration = ProbeDurationSeconds(request.OutputPath);
            Report(options, VideoCompositionStage.Finalizing, "完了", 1.0);
            return new VideoCompositionResult(request.OutputPath, duration);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { /* 一時領域のため失敗は無視 */ }
        }
    }

    private async Task<string> RenderCardAsync(
        string text, double seconds, int w, int h, string fps, VideoCompositionOptions options, string workDir, string name, CancellationToken ct)
    {
        var stage = name == "title" ? VideoCompositionStage.RenderingTitle : VideoCompositionStage.RenderingEnding;
        var detail = stage == VideoCompositionStage.RenderingTitle ? "Title Screen を生成しています…" : "Ending を生成しています…";
        var baseProgress = stage == VideoCompositionStage.RenderingTitle ? BaseProgress.RenderingTitle : BaseProgress.RenderingEnding;
        var weight = stage == VideoCompositionStage.RenderingTitle
            ? BaseProgress.RenderingEnding - BaseProgress.RenderingTitle
            : BaseProgress.Concatenating - BaseProgress.RenderingEnding;

        var ass = Path.Combine(workDir, $"{name}.ass");
        File.WriteAllText(ass, AssSubtitleWriter.WriteCard(text, seconds, w, h), new System.Text.UTF8Encoding(false));
        var output = Path.Combine(workDir, $"{name}.mp4");
        // ass フィルタの引数は相対パスで渡す（「C:」のコロンがフィルタオプション区切りとして解析されるため）
        await RunAsync(
            $"-y -f lavfi -i color=c=0x1B2E4E:size={w}x{h}:rate={fps}:duration={seconds:F2} " +
            $"-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -shortest " +
            $"-vf \"ass={name}.ass\" -c:v {options.Encoder} -pix_fmt yuv420p -c:a aac \"{output}\"",
            workDir,
            new RunProgressContext(stage, detail, baseProgress, weight, seconds),
            options,
            ct);
        return output;
    }

    /// <summary>ffprobe で入力の解像度・fps を取得する（Title/Ending を録画に合わせるため）。</summary>
    private (int Width, int Height, string Fps) ProbeVideoInfo(string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpegPath)!, "ffprobe.exe");
        var width = Probe(ffprobe, $"-v error -select_streams v:0 -show_entries stream=width -of csv=p=0 \"{path}\"");
        var height = Probe(ffprobe, $"-v error -select_streams v:0 -show_entries stream=height -of csv=p=0 \"{path}\"");
        var rate = Probe(ffprobe, $"-v error -select_streams v:0 -show_entries stream=r_frame_rate -of csv=p=0 \"{path}\"");
        return (int.Parse(width), int.Parse(height), rate.Contains('/') ? rate : $"{rate}/1");

        static string Probe(string ffprobe, string args)
        {
            var psi = new ProcessStartInfo(ffprobe, args) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();
            return text;
        }
    }

    private double ProbeDurationSeconds(string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(_ffmpegPath)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(
            ffprobe, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return double.Parse(text);
    }

    /// <summary>ffmpeg 実行 1 回分の進捗定義（段階の全体進捗での位置とウェイト・想定処理秒）。</summary>
    private sealed record RunProgressContext(
        VideoCompositionStage Stage,
        string Detail,
        double BaseProgress,
        double Weight,
        double? ExpectedSeconds);

    /// <summary>全体進捗での段階境界（0.0〜1.0）。合計 = 1.0。Subtitle 焼き込みと結合が時間の大半を占める。</summary>
    private static class BaseProgress
    {
        public const double AnalyzingInput = 0.00;
        public const double BurningSubtitles = 0.02;
        public const double RenderingTitle = 0.47;
        public const double RenderingEnding = 0.51;
        public const double Concatenating = 0.54;
        public const double Finalizing = 0.99;
    }

    private static void Report(VideoCompositionOptions options, VideoCompositionStage stage, string detail, double overall)
    {
        options.Progress?.Report(new VideoCompositionProgress(stage, detail, Math.Clamp(overall, 0.0, 1.0)));
    }

    private async Task RunAsync(string arguments, string workingDir, RunProgressContext progress, VideoCompositionOptions options, CancellationToken ct)
    {
        // -progress pipe:1 で stdout に進捗（key=value 行）を出させ、全体進捗へ写像する
        var psi = new ProcessStartInfo(_ffmpegPath, $"-progress pipe:1 -nostats {arguments}")
        {
            UseShellExecute = false,
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi)!;

        var parser = new FfmpegProgressParser();
        var lastReportedPercent = -1;
        var readTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync() is { } line)
            {
                parser.Feed(line);
                if (progress.ExpectedSeconds is { } expected && expected > 0 && parser.OutTimeSeconds is { } seconds && options.Progress is not null)
                {
                    var overall = progress.BaseProgress + progress.Weight * Math.Clamp(seconds / expected, 0.0, 1.0);
                    var percent = (int)(overall * 100);
                    if (percent > lastReportedPercent)
                    {
                        lastReportedPercent = percent;
                        Report(options, progress.Stage, progress.Detail, overall);
                    }
                }
            }
        });

        try
        {
            await p.WaitForExitAsync(ct);
            await readTask;
        }
        catch (OperationCanceledException)
        {
            // キャンセル時は ffmpeg を残さず終了させる（ゾンビプロセスが temp を握り続けるのを防ぐ）
            try { p.Kill(entireProcessTree: true); } catch { /* 既に終了している場合 */ }
            throw;
        }

        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg が失敗しました (exit {p.ExitCode}): {arguments[..Math.Min(120, arguments.Length)]}…");
        }
    }

    private static string? LocateFfmpeg()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 12 && dir is not null; i++, dir = dir.Parent!)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
