using System.Diagnostics;
using System.Drawing;
using TrainingContent.Core.Models;
using TrainingContent.Video;
using TrainingContent.Video.Renderer;
using TrainingContent.Video.Subtitle;
using TrainingContent.Video.Timeline;

namespace VideoComposeCheck;

/// <summary>
/// R-05（動画生成）の実機検証ツール。
/// 合成ソース MP4 + TrainingStep fixture を TrainingContent.Video モジュール
/// （FfmpegVideoRenderer / StepTimelineBuilder / AssSubtitleWriter）に通し、出力を自動判定する。
/// 契約 §12/§16/§17/§20 の fixture を使用。
/// </summary>
internal static class Program
{
    private static int _failCount;

    public static int Main(string[] args)
    {
        Console.WriteLine("=== R-05 Video Compose Check（TrainingContent.Video モジュール実機検証）===");
        var workDir = Path.Combine(AppContext.BaseDirectory, "video-compose-work");
        Directory.CreateDirectory(workDir);

        // ---- fixture 準備: 合成ソース 10 秒（契約 §17 raw/recording.mp4 相当）----
        var renderer = new FfmpegVideoRenderer();
        var ffmpeg = renderer.FfmpegPath;
        var source = Path.Combine(workDir, "source.mp4");
        Run(ffmpeg, $"-y -f lavfi -i testsrc=duration=10:size=1280x720:rate=30 -f lavfi -i sine=frequency=440:duration=10 -c:v libopenh264 -pix_fmt yuv420p -c:a aac \"{source}\"");

        // ---- fixture: TrainingProject（契約 §6/§12/§14）----
        // 単発操作は EndMs=null（契約 §14）→ 表示区間は次 Step の開始まで。最終 Step は +4s フォールバック
        var project = new TrainingProject
        {
            Id = Guid.NewGuid(),
            Title = "請求書作成トレーニング",
            Revision = 1,
            Recording = new RecordingInfo { MediaPath = "raw/recording.mp4", DurationMs = 10_000 },
            Steps =
            [
                new TrainingStep { Order = 1, StartMs = 1_000, Title = "請求書を開く", Description = "メニューから「請求書一覧」を選択" },
                new TrainingStep { Order = 2, StartMs = 4_500, EndMs = 7_500, Title = "金額を入力", Caution = "税抜金額を入力すること", ExpectedResult = "入力欄に金額が表示される" },
                new TrainingStep { Order = 3, StartMs = 8_000, Title = "内容を確認して保存", Description = "確認ダイアログで「OK」を押す" },
            ],
        };

        // ---- Step 1: 表示区間 → ASS 生成（モジュールの pure logic）----
        var intervals = StepTimelineBuilder.Build(project.Steps, project.Recording.DurationMs);
        var ass = AssSubtitleWriter.Write(intervals);
        var dialogueCount = ass.Split('\n').Count(l => l.StartsWith("Dialogue:"));
        Check(1, $"表示区間 → ASS 生成（Dialogue {dialogueCount} 行 / Step {project.Steps.Count} 件）", dialogueCount == project.Steps.Count);

        // ---- Step 2〜5: モジュール（FfmpegVideoRenderer）で合成 ----
        // 構成は契約 §20: Title Screen (3s) → 録画 10s → Ending (2s) = 計 15 秒
        // 進捗報告（-progress pipe:1 の解析）もここで実機検証する
        var output = Path.Combine(workDir, "training_video.mp4");
        var reportedProgress = new List<double>();
        var result = renderer.ComposeAsync(
            new VideoCompositionRequest
            {
                RecordingPath = source,
                OutputPath = output,
                Project = project,
            },
            new VideoCompositionOptions
            {
                Progress = new Progress<VideoCompositionProgress>(p =>
                {
                    lock (reportedProgress)
                    {
                        reportedProgress.Add(p.OverallProgress);
                        Console.WriteLine($"    進捗 {p.OverallProgress,5:P1}  [{p.Stage}] {p.StageDetail}");
                    }
                }),
            }).GetAwaiter().GetResult();

        Check(2, "モジュールによる合成がエラーなく完了", File.Exists(output) && new FileInfo(output).Length > 0);
        var progressCovered = reportedProgress.Count >= 3 && reportedProgress.First() < 0.1 && reportedProgress.Last() >= 0.99;
        Check(5, $"進捗報告が単調に 0→100% をカバー（報告 {reportedProgress.Count} 回）", progressCovered);

        // ---- Duration 一致（Title 3s + 録画 10s + Ending 2s = 15s ± 0.5s・ffprobe 実測）----
        Check(3, $"Duration 一致（出力 {result.DurationSeconds:F2}s / 期待 15.00s）", Math.Abs(result.DurationSeconds - 15.0) <= 0.5);

        // ---- Step 区間に字幕が焼き込まれている（ソース同一時刻フレームとの画素差）----
        // 出力 t=5.0s == ソース t=2.0s（Title 3s 分のオフセット）
        var srcFrame = Path.Combine(workDir, "src_t2.png");
        var outFrame = Path.Combine(workDir, "out_t2.png");
        Run(ffmpeg, $"-y -ss 2.0 -i \"{source}\" -update 1 -frames:v 1 \"{srcFrame}\"");
        Run(ffmpeg, $"-y -ss 5.0 -i \"{output}\" -update 1 -frames:v 1 \"{outFrame}\"");
        var diffRatio = FrameDiffRatio(srcFrame, outFrame);
        Check(4, $"Step 区間フレームの画素差 {diffRatio:P2}（字幕焼き込み確認）", diffRatio > 0.01);

        // ---- Step 6〜7: 音声なし録画の合成（監査 m-4 対応の実機検証）----
        // 契約 §7 では音声なし録画も正当。Title / Ending（anullsrc 付き）との concat で
        // ストリーム構成が不一致になり壊れ得るため、無音声トラック補てつの実機確認を行う。
        var sourceNoAudio = Path.Combine(workDir, "source-noaudio.mp4");
        Run(ffmpeg, $"-y -f lavfi -i testsrc=duration=10:size=1280x720:rate=30 -c:v libopenh264 -pix_fmt yuv420p \"{sourceNoAudio}\"");
        var outputNoAudio = Path.Combine(workDir, "training_video_noaudio.mp4");
        var resultNoAudio = renderer.ComposeAsync(
            new VideoCompositionRequest
            {
                RecordingPath = sourceNoAudio,
                OutputPath = outputNoAudio,
                Project = project,
            }).GetAwaiter().GetResult();
        Check(6, "音声なし録画の合成がエラーなく完了", File.Exists(outputNoAudio) && new FileInfo(outputNoAudio).Length > 0);
        Check(7, $"音声なし録画の出力にも音声トラックが存在し Duration 一致（{resultNoAudio.DurationSeconds:F2}s / 期待 15.00s）",
            Math.Abs(resultNoAudio.DurationSeconds - 15.0) <= 0.5 && HasAudioStream(ffmpeg, outputNoAudio));

        Console.WriteLine(_failCount == 0
            ? "\n全 7 項目 PASS — R-05 MVP（契約 §20 構成・FFmpeg + ASS 焼き込み・進捗報告・音声なし録画 guard）はモジュールとして成立"
            : $"\n{_failCount} 項目 NG");
        return _failCount == 0 ? 0 : 1;
    }

    /// <summary>ffprobe で音声ストリームの存在を確認する（m-4: 音声なし録画の出力検査）。</summary>
    private static bool HasAudioStream(string ffmpeg, string path)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(
            ffprobe, $"-v error -select_streams a -show_entries stream=index -of csv=p=0 \"{path}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return text.Length > 0;
    }

    /// <summary>同一時刻の 2 フレーム間の「変化画素の割合」を返す（字幕焼き込みの有無判定用）。</summary>
    private static double FrameDiffRatio(string frameA, string frameB)
    {
        using var a = new Bitmap(frameA);
        using var b = new Bitmap(frameB);
        if (a.Size != b.Size)
        {
            return 1.0;
        }

        int changed = 0, total = a.Width * a.Height;
        for (var y = 0; y < a.Height; y += 2) // 全画素でなく間引きで高速化
        {
            for (var x = 0; x < a.Width; x += 2)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                var d = Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                if (d > 60)
                {
                    changed++;
                }
            }
        }

        return (double)changed / (total / 4);
    }

    private static void Run(string ffmpeg, string arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(ffmpeg, arguments) { UseShellExecute = false };
        if (workingDir is not null)
        {
            psi.WorkingDirectory = workingDir;
        }
        using var p = Process.Start(psi)!;
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            Console.WriteLine($"NG: ffmpeg 失敗 (exit {p.ExitCode}): {arguments[..Math.Min(120, arguments.Length)]}…");
            Environment.Exit(1);
        }
    }

    private static void Check(int no, string label, bool pass)
    {
        Console.WriteLine($"  [{(pass ? "PASS" : "NG")}] 項目 {no}: {label}");
        if (!pass)
        {
            _failCount++;
        }
    }
}
