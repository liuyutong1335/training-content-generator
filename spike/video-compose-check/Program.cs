using System.Diagnostics;
using System.Drawing;

namespace VideoComposeCheck;

/// <summary>
/// R-05（動画生成）Spike 検証ツール。
/// 合成ソース MP4 + TrainingStep fixture から ASS 字幕を生成して ffmpeg で焼き込み、
/// 出力 MP4 を自動判定する。契約 §12/§16/§17 の fixture を使用。
/// </summary>
internal static class Program
{
    private record StepFixture(
        int Order, long StartMs, long? EndMs, string Action,
        string Title, string? Description = null, string? Caution = null, string? ExpectedResult = null);

    private static int _failCount;

    public static int Main(string[] args)
    {
        Console.WriteLine("=== R-05 Video Compose Spike Check ===");
        var ffmpeg = LocateFfmpeg(args);
        if (ffmpeg is null)
        {
            Console.WriteLine("NG: ffmpeg.exe が見つかりません。tools\\get-ffmpeg.ps1 を実行してください。");
            return 1;
        }

        var workDir = Path.Combine(AppContext.BaseDirectory, "video-compose-work");
        Directory.CreateDirectory(workDir);

        // ---- fixture 準備: 合成ソース 10 秒（契約 §17 raw/recording.mp4 相当）----
        var source = Path.Combine(workDir, "source.mp4");
        Run(ffmpeg, $"-y -f lavfi -i testsrc=duration=10:size=1280x720:rate=30 -f lavfi -i sine=frequency=440:duration=10 -c:v libopenh264 -pix_fmt yuv420p -c:a aac \"{source}\"");

        // ---- fixture: TrainingStep（契約 §12/§13/§14）----
        // 単発操作は EndMs=null（契約 §14）→ 表示区間は次 Step の開始まで。最終 Step は +4s フォールバック
        List<StepFixture> steps =
        [
            new(1, 1_000, null, "click", "請求書を開く", Description: "メニューから「請求書一覧」を選択"),
            new(2, 4_500, 7_500, "textEntry", "金額を入力", Caution: "税抜金額を入力すること", ExpectedResult: "入力欄に金額が表示される"),
            new(3, 8_000, null, "manual", "内容を確認して保存", Description: "確認ダイアログで「OK」を押す"),
        ];

        // ---- Step 1: ASS 生成 ----
        var assPath = Path.Combine(workDir, "steps.ass");
        var dialogueCount = WriteAss(steps, assPath);
        Check(1, $"ASS 生成（Dialogue {dialogueCount} 行 / Step {steps.Count} 件）", dialogueCount == steps.Count);

        // ---- Step 2: ffmpeg 合成（ASS 焼き込み・音声 copy）----
        // ass フィルタの引数に絶対パスを渡すとドライブ文字の「:」がオプション区切りとして
        // 解析されるため、作業ディレクトリを workDir にして相対パスで渡す
        var output = Path.Combine(workDir, "training_video.mp4");
        Run(ffmpeg, $"-y -i \"{source}\" -vf \"ass=steps.ass\" -c:v libopenh264 -pix_fmt yuv420p -c:a copy \"{output}\"", workDir);
        Check(2, "ffmpeg 合成がエラーなく完了", File.Exists(output) && new FileInfo(output).Length > 0);

        // ---- Step 3: Duration 一致（±0.5s）----
        var srcDur = ProbeDuration(ffmpeg, source);
        var outDur = ProbeDuration(ffmpeg, output);
        Check(3, $"Duration 一致（source {srcDur:F2}s / output {outDur:F2}s）", Math.Abs(srcDur - outDur) <= 0.5);

        // ---- Step 4: Step 区間に字幕が焼き込まれている（ソース同一時刻フレームとの画素差）----
        var srcFrame = Path.Combine(workDir, "src_t2.png");
        var outFrame = Path.Combine(workDir, "out_t2.png");
        Run(ffmpeg, $"-y -ss 2.0 -i \"{source}\"  -update 1 -frames:v 1 \"{srcFrame}\"");
        Run(ffmpeg, $"-y -ss 2.0 -i \"{output}\"  -update 1 -frames:v 1 \"{outFrame}\"");
        var diffRatio = FrameDiffRatio(srcFrame, outFrame);
        Check(4, $"Step 区間フレームの画素差 {diffRatio:P2}（字幕焼き込み確認）", diffRatio > 0.01);

        Console.WriteLine(_failCount == 0
            ? "\n全 4 項目 PASS — R-05 MVP（ASS 字幕焼き込み方式）は成立"
            : $"\n{_failCount} 項目 NG");
        return _failCount == 0 ? 0 : 1;
    }

    /// <summary>
    /// TrainingStep 列 → ASS ファイル。表示区間は契約 §14 に基づき決定:
    /// EndMs あり = [StartMs, EndMs]、EndMs=null = [StartMs, 次 Step の StartMs]（最終は +4s）。
    /// Canonical Timeline 0ms == MP4 0 秒（CaptureStarted 同期）なので StartMs をそのまま ASS 時刻に使える。
    /// </summary>
    private static int WriteAss(List<StepFixture> steps, string path)
    {
        const double fallbackSecs = 4.0;
        var lines = new List<string>
        {
            "[Script Info]",
            "ScriptType: v4.00+",
            "PlayResX: 1280",
            "PlayResY: 720",
            "",
            "[V4+ Styles]",
            "Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, Outline, Shadow, Alignment, MarginL, MarginR, MarginV",
            "Style: StepTitle,Yu Gothic UI,40,&H00FFFFFF,&H00000000,&H80000000,1,2,0,2,60,60,60",
            "Style: StepDetail,Yu Gothic UI,26,&H00FFFFFF,&H00000000,&H80000000,0,1,0,2,60,60,20",
            "Style: StepCaution,Yu Gothic UI,26,&H0000D7FF,&H00000000,&H80000000,1,1,0,2,60,60,20",
            "",
            "[Events]",
            "Format: Layer, Start, End, Style, Text",
        };

        var dialogues = new List<string>();
        foreach (var (step, index) in steps.Select((s, i) => (s, i)))
        {
            var nextStart = index + 1 < steps.Count ? steps[index + 1].StartMs : step.StartMs + (long)(fallbackSecs * 1000);
            var endMs = step.EndMs ?? nextStart;
            var title = $"{step.Order}. {step.Title}";
            var detail = string.Join("\\N", new[] { step.Description, step.ExpectedResult }.Where(s => !string.IsNullOrEmpty(s)));
            var text = string.IsNullOrEmpty(step.Caution)
                ? $"{{\\rStepTitle}}{title}{{\\rStepDetail}}\\N{detail}"
                : $"{{\\rStepTitle}}{title}{{\\rStepCaution}}\\N{step.Caution}{{\\rStepDetail}}\\N{detail}";

            dialogues.Add($"Dialogue: 0,{MsToAss(step.StartMs)},{MsToAss(endMs)},StepTitle,,0,0,0,,{text}");
        }

        // Caution 有無でスタイルが分かれるため、Dialogue ごとに先頭スタイルを解決する
        // （上の text 内 \r で上書きするため先頭は StepTitle 固定で可）
        File.WriteAllLines(path, lines.Concat(dialogues), new System.Text.UTF8Encoding(false));
        return dialogues.Count;
    }

    private static string MsToAss(long ms) => $"{ms / 3600_000:D1}:{ms / 60_000 % 60:D2}:{ms / 1000 % 60:D2}.{ms % 1000:D3}";

    private static string LocateFfmpeg(string[] args)
    {
        if (args.Length > 0 && File.Exists(args[0]))
        {
            return args[0];
        }

        // リポジトリルートの tools/ffmpeg/bin/ を探す（bin 出力から 8 階層上）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent!)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "ffmpeg", "bin", "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static double ProbeDuration(string ffmpeg, string file)
    {
        var ffprobe = Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe");
        var psi = new ProcessStartInfo(ffprobe, $"-v error -show_entries format=duration -of csv=p=0 \"{file}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return double.Parse(text);
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
        for (var y = 0; y < a.Height; y += 2) // 全像素でなく間引きで高速化
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
