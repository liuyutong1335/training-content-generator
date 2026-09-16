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
            // 出力サイズ/fps は録画に合わせる（concat の一致要件）
            var source = ProbeVideoInfo(request.RecordingPath);
            var (w, h, fps) = source;

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
                cancellationToken);

            var duration = ProbeDurationSeconds(request.OutputPath);
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
        var ass = Path.Combine(workDir, $"{name}.ass");
        File.WriteAllText(ass, AssSubtitleWriter.WriteCard(text, seconds, w, h), new System.Text.UTF8Encoding(false));
        var output = Path.Combine(workDir, $"{name}.mp4");
        // ass フィルタの引数は相対パスで渡す（「C:」のコロンがフィルタオプション区切りとして解析されるため）
        await RunAsync(
            $"-y -f lavfi -i color=c=0x1B2E4E:size={w}x{h}:rate={fps}:duration={seconds:F2} " +
            $"-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=44100 -shortest " +
            $"-vf \"ass={name}.ass\" -c:v {options.Encoder} -pix_fmt yuv420p -c:a aac \"{output}\"",
            workDir,
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

    private async Task RunAsync(string arguments, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_ffmpegPath, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDir,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
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
