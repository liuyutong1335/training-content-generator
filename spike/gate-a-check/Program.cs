using GateACheck;
using TrainingContent.Capture;

// Gate A 確認ツール（開発計画書 §14 / Spike A 担当 A）。
//
// 使い方:
//   GateACheck.exe            → 標準モード（各 12〜20 秒のシナリオ + 手動確認への案内）
//   GateACheck.exe --full     → 10 分録画シナリオを追加（Gate A の 10 分項目）
//
// 自動判定する項目:
//   - 録画成功（ファイル生成・サイズ）
//   - MP4 構成（動画トラック / 音声トラックの存在）— moov/mvhd/hdlr を直接解析
//   - 論理 Duration = 実経過 - Pause（契約 §5.2）
//   - faststart（moov が mdat より前 = seek に強い MP4）
// 人間が確認する項目（最後に対話で答える）:
//   - 音ズレなし / アプリ切替の記録 / シーク操作性 / 各シナリオの音声内容

Console.OutputEncoding = System.Text.Encoding.UTF8;
var fullMode = args.Contains("--full");

var checks = new List<CheckResult>();

// ============================================================
// シナリオ 1: システム音声のみ（マイク OFF）
// ============================================================
await RunScenario(
    name: "1. システム音声のみ",
    seconds: 12,
    pauseTest: false,
    useMic: false,
    useSys: true,
    instruction: "この間にシステムで音（動画や音楽）を再生してください。",
    checks);
if (checks[^1].Fatal) { PrintSummary(checks); return 1; }

// ============================================================
// シナリオ 2: マイクのみ（システム音声 OFF）
// ============================================================
await RunScenario(
    name: "2. マイクのみ",
    seconds: 12,
    pauseTest: false,
    useMic: true,
    useSys: false,
    instruction: "この間にマイクに向かって話してください。",
    checks);
if (checks[^1].Fatal) { PrintSummary(checks); return 1; }

// ============================================================
// シナリオ 3: 両方 + Pause/Resume（アプリ切替もこの間に実施）
// ============================================================
await RunScenario(
    name: "3. Mic + System + Pause/Resume",
    seconds: 24,
    pauseTest: true,
    useMic: true,
    useSys: true,
    instruction: "この間に（1）システムで音を再生（2）マイクで話す（3）他アプリへの切替 を行ってください。",
    checks);
if (checks[^1].Fatal) { PrintSummary(checks); return 1; }

// ============================================================
// シナリオ 4: 10 分録画（--full のときのみ）
// ============================================================
if (fullMode)
{
    await RunScenario(
        name: "4. 10 分録画",
        seconds: 600,
        pauseTest: true,
        useMic: true,
        useSys: true,
        instruction: "10 分間の間に複数アプリを操作してください。",
        checks);
}

PrintSummary(checks);

// 手動確認（対話。パイプ実行では「要確認」扱いになる）
Console.WriteLine();
Console.WriteLine("== 手動確認（生成物をプレーヤーで再生してから答えてください） ==");
var manualChecks = new List<CheckResult>();
AskManual("音ズレはありませんか？（シーンの切り替わり・口の動きと音声の一致）", manualChecks, interactive: !Console.IsInputRedirected);
AskManual("MP4 のシーク（進行状況バーのドラッグ）は問題ありませんか？", manualChecks, interactive: !Console.IsInputRedirected);
AskManual("シナリオ 1 の動画からシステム音声が聞こえますか？", manualChecks, interactive: !Console.IsInputRedirected);
AskManual("シナリオ 2 の動画からマイク音声が聞こえますか？", manualChecks, interactive: !Console.IsInputRedirected);
AskManual("シナリオ 3 の動画でアプリ切替が記録されていますか？", manualChecks, interactive: !Console.IsInputRedirected);

Console.WriteLine();
Console.WriteLine("======== GATE A 判定 ========");
var autoOk = checks.Where(c => !c.Warning).All(c => c.Passed == true);
var manualOk = manualChecks.All(c => c.Passed == true);
var pending = manualChecks.Any(c => c.Passed is null);
Console.WriteLine($"自動チェック : {(autoOk ? "PASS ✅" : "FAIL ❌")}");
Console.WriteLine($"手動チェック : {(pending ? "要確認 ⚠（対話で回答すると確定します）" : manualOk ? "PASS ✅" : "FAIL ❌")}");
if (autoOk && manualOk && fullMode)
{
    Console.WriteLine();
    Console.WriteLine("GATE A: PASS ✅ — Recording Spike の完了条件を満たしました（10 分録画を含む全項目）");
}
else if (autoOk && manualOk)
{
    Console.WriteLine();
    // 監査 SP-1 対応: --full（10 分録画）なしでは「GATE A: PASS」「完了条件を満たした」と表示しない
    Console.WriteLine("自動・手動項目は PASS — ただし 10 分録画シナリオを未実施のため Gate A 完了判定は行いません（--full で再実行してください）");
}
return autoOk && manualOk ? 0 : 1;

// ============================================================

static async Task RunScenario(string name, int seconds, bool pauseTest, bool useMic, bool useSys,
    string instruction, List<CheckResult> checks)
{
    Console.WriteLine();
    Console.WriteLine($"==============================================");
    Console.WriteLine($"シナリオ {name}（{seconds} 秒）");
    Console.WriteLine($"  ▶ {instruction}");
    Console.WriteLine($"==============================================");

    var engine = new ScreenRecorderRecordingEngine();
    var outPath = Path.GetFullPath($"gate-a-{name[0]}.mp4");
    if (File.Exists(outPath))
    {
        File.Delete(outPath);
    }

    // 実撮影開始（Canonical 0ms）と停止完了の実時刻を取り、Duration 判定を wall 実測と突き合わせる（監査 SP-2 対応）
    DateTime? captureStartedAt = null;
    engine.CaptureStarted += (_, _) => captureStartedAt = DateTime.Now;

    var mic = useMic ? engine.GetMicrophones().FirstOrDefault() : null;
    var sys = useSys ? engine.GetSystemAudioDevices().FirstOrDefault() : null;
    if (useMic && mic is null)
    {
        checks.Add(new CheckResult($"{name}: マイク検出", false, "マイクが見つかりません"));
        return;
    }
    if (useSys && sys is null)
    {
        checks.Add(new CheckResult($"{name}: システム音声デバイス検出", false, "出力デバイスが見つかりません"));
        return;
    }

    var options = new RecordingOptions
    {
        OutputFilePath = outPath,
        FrameRate = 30,
        MicrophoneDevice = mic,
        SystemAudioDevice = sys,
    };

    var wallStart = DateTime.Now;
    RecordingResult result;
    try
    {
        await engine.StartAsync(options);

        if (pauseTest)
        {
            var half = seconds * 1000 / 2;
            await Task.Delay(half);
            await engine.PauseAsync();
            Console.WriteLine("   … Pause（3 秒・この間の操作は記録されません）");
            await Task.Delay(3000);
            await engine.ResumeAsync();
            var rest = half - 3000;
            if (rest > 0) await Task.Delay(rest);
        }
        else
        {
            await Task.Delay(seconds * 1000);
        }

        result = await engine.StopAsync();
    }
    catch (Exception ex)
    {
        checks.Add(new CheckResult($"{name}: 録画実行", false, ex.Message));
        return;
    }
    var wallSeconds = (DateTime.Now - wallStart).TotalSeconds;

    // ---- 自動判定 ----
    if (!File.Exists(outPath) || new FileInfo(outPath).Length < 10_000)
    {
        checks.Add(new CheckResult($"{name}: 録画成功", false, "ファイルが無いか、異常に小さい"));
        return;
    }
    checks.Add(new CheckResult($"{name}: 録画成功", true, $"{new FileInfo(outPath).Length:N0} bytes"));

    var mp4 = Mp4Inspector.Inspect(outPath);
    checks.Add(new CheckResult($"{name}: 動画トラックあり", mp4.HasVideo, $"video={mp4.VideoTrackCount} / audio={mp4.AudioTrackCount}"));
    checks.Add(new CheckResult($"{name}: 音声トラックあり", mp4.HasAudio, $"video={mp4.VideoTrackCount} / audio={mp4.AudioTrackCount}"));
    // faststart は必須ではなく（moov 末尾でもプレーヤーの seek は可能）、
    // v6.6.0 では IsMp4FastStartEnabled が効かない既知事象のため警告扱い（v7.0.1 比較時に再確認）
    checks.Add(new CheckResult($"{name}: faststart (seek 向き)", mp4.IsFastStart, $"moov@{mp4.MoovOffset} mdat@{mp4.MdatOffset}", true));

    // 論理 Duration（Pause 除外）が MP4 の実時間と一致するか（許容 ±2 秒）
    var diff = Math.Abs(mp4.DurationSeconds - result.Duration.TotalSeconds);
    checks.Add(new CheckResult(
        $"{name}: 論理 Duration = Pause 除外（契約 §5.2）",
        diff <= 2.0,
        $"engine={result.Duration.TotalSeconds:F1}s / mp4={mp4.DurationSeconds:F1}s / 実経過={wallSeconds:F1}s (差 {diff:F1}s)"));

    // 実撮影経過（CaptureStarted → Stop 完了から Pause を除いた値）と MP4 を突き合わせる。
    // engine 論理 Duration との一致判定だけでは、両者が同方向にずれても検出できないため（監査 SP-2）
    if (captureStartedAt is { } cs)
    {
        var wallCapture = (DateTime.Now - cs).TotalSeconds - (pauseTest ? 3.0 : 0);
        var drift = Math.Abs(mp4.DurationSeconds - wallCapture);
        checks.Add(new CheckResult(
            $"{name}: 実撮影経過との整合（±1.5 秒）",
            drift <= 1.5,
            $"mp4={mp4.DurationSeconds:F1}s / 実撮影経過={wallCapture:F1}s (差 {drift:F1}s)"));
    }
    else
    {
        checks.Add(new CheckResult($"{name}: 実撮影経過との整合", false, "CaptureStarted が発火していません"));
    }

    // 音声の無音検査。トラックの存在だけでは無音故障を検出できないため（監査 SP-3）、
    // ffmpeg（tools/get-ffmpeg.ps1 で取得）があれば mean_volume で判定する。無ければ警告扱い
    if (mp4.HasAudio)
    {
        var ffmpeg = LocateFfmpeg();
        if (ffmpeg is null)
        {
            checks.Add(new CheckResult($"{name}: 音声無音検査", null, "ffmpeg が見つからず音量検査をスキップ（tools\\get-ffmpeg.ps1 を実行）", true));
        }
        else
        {
            var meanVolume = ProbeMeanVolume(ffmpeg, outPath);
            checks.Add(meanVolume is { } mv
                ? new CheckResult(
                    $"{name}: 音声が無音でない",
                    mv > -50.0,
                    // 無音はおおむね -90dB 前後。実音があれば -50dB より十分高くなる
                    $"mean_volume={mv:F1} dB")
                : new CheckResult($"{name}: 音声無音検査", null, "音量解析に失敗（ffmpeg の出力を確認）", true));
        }
    }

    Console.WriteLine($"   → 完了: engine={result.Duration.TotalSeconds:F1}s, mp4={mp4.DurationSeconds:F1}s, 実経過={wallSeconds:F1}s");
}

static void AskManual(string question, List<CheckResult> results, bool interactive)
{
    if (!interactive)
    {
        results.Add(new CheckResult(question, null, "対話実行で回答してください"));
        return;
    }
    Console.Write($"  {question} (y/n) > ");
    var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
    results.Add(new CheckResult(question, answer is "y" or "yes", answer));
}

/// <summary>tools/ffmpeg/bin/ffmpeg.exe を探す（bin 出力から上位へ）。無ければ null。</summary>
static string? LocateFfmpeg()
{
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

/// <summary>ffmpeg volumedetect で mean_volume (dB) を取得。解析に失敗したら null。</summary>
static double? ProbeMeanVolume(string ffmpeg, string file)
{
    try
    {
        var psi = new ProcessStartInfo(ffmpeg, $"-i \"{file}\" -map 0:a:0 -af volumedetect -f null NUL")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        var match = System.Text.RegularExpressions.Regex.Match(stderr, @"mean_volume:\s*(-?[\d.]+)\s*dB");
        return match.Success ? double.Parse(match.Groups[1].Value) : null;
    }
    catch
    {
        return null;
    }
}

static void PrintSummary(List<CheckResult> checks)
{
    Console.WriteLine();
    Console.WriteLine("======== 自動チェック結果 ========");
    foreach (var c in checks)
    {
        var mark = c.Passed is true ? (c.Warning ? "WARN" : "PASS") : c.Passed is false ? (c.Warning ? "WARN" : "FAIL") : "PEND";
        Console.WriteLine($"  [{mark}] {c.Name}: {c.Note}");
    }
}

/// <summary>1 項目分の判定。Passed=null は未確認（手動待ち）。Warning=true は Gate 判定に影響しない。</summary>
public sealed record CheckResult(string Name, bool? Passed, string Note, bool Warning = false)
{
    public bool Fatal => Passed is false && !Warning;
}
