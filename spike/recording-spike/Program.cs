using TrainingContent.Capture;

// Spike A（開発計画書 §14）: Gate A の実機確認用。
//
// 使い方:
//   RecordingSpike.exe <出力.mp4> [録画秒数] [nopause]
//
// 例:
//   RecordingSpike.exe test.mp4            → 10 秒 + Pause/Resume テスト
//   RecordingSpike.exe test10min.mp4 600   → 10 分録画（Gate A）
//   RecordingSpike.exe test.mp4 60 nopause → 1 分、Pause 無し
//
// Gate A の確認観点: 録画成功 / システム音声あり・なし / Mic+System / Pause・Resume /
// アプリ切替（録画中に他アプリを操作する） / MP4 seek（生成物をプレーヤーでシーク） / 音ズレ無し

Console.OutputEncoding = System.Text.Encoding.UTF8;

var outputPath = args.Length > 0 ? args[0] : "spike-recording.mp4";
var recordSeconds = args.Length > 1 && int.TryParse(args[1], out var sec) && sec > 0 ? sec : 10;
var pauseTest = !(args.Length > 2 && args[2] == "nopause");

var engine = new ScreenRecorderRecordingEngine();

Console.WriteLine("== デバイス列挙 ==");
foreach (var d in engine.GetDisplays())
{
    Console.WriteLine($"  display: id={d.DeviceId} name={d.DeviceName} primary={d.IsPrimary}");
}
foreach (var m in engine.GetMicrophones())
{
    Console.WriteLine($"  mic:     id={m.DeviceId} name={m.DeviceName}");
}
foreach (var s in engine.GetSystemAudioDevices())
{
    Console.WriteLine($"  sys-audio: id={s.DeviceId} name={s.DeviceName}");
}

var mic = engine.GetMicrophones().FirstOrDefault();
var sys = engine.GetSystemAudioDevices().FirstOrDefault();
var options = new RecordingOptions
{
    OutputFilePath = Path.GetFullPath(outputPath),
    FrameRate = 30,
    MicrophoneDevice = mic,
    SystemAudioDevice = sys,
};

Console.WriteLine($"\n== 録画開始: {recordSeconds} 秒 / Pause テスト: {(pauseTest ? "あり" : "なし")} ==");
Console.WriteLine("   （録画中に他アプリを操作してアプリ切替を確認してください）");
await engine.StartAsync(options);

// 中央で一度 Pause → Resume（短い録画時のみ。10 分録画などは nopause 推奨）
if (pauseTest && recordSeconds >= 20)
{
    var half = recordSeconds * 1000 / 2;
    await Task.Delay(half);
    Console.WriteLine("== Pause ==");
    await engine.PauseAsync();
    await Task.Delay(2000);
    Console.WriteLine("== Resume ==");
    await engine.ResumeAsync();
    var rest = half - 2000;
    if (rest > 0)
    {
        await Task.Delay(rest);
    }
}
else
{
    await Task.Delay(recordSeconds * 1000);
}

Console.WriteLine("== Stop ==");
var result = await engine.StopAsync();

Console.WriteLine("\n結果:");
Console.WriteLine($"  file        : {result.FilePath}");
Console.WriteLine($"  duration    : {result.Duration.TotalMilliseconds:F0} ms（Pause を含まない論理時間）");
Console.WriteLine($"  startedAtUtc: {result.StartedAtUtc:O}");
Console.WriteLine($"  pauseIntervals: {string.Join(", ", result.PauseIntervals.Select(p => $"{p.Start.TotalMilliseconds:F0}-{p.End.TotalMilliseconds:F0}ms"))}");

if (File.Exists(result.FilePath))
{
    var size = new FileInfo(result.FilePath).Length;
    Console.WriteLine($"  file size   : {size:N0} bytes");
    Console.WriteLine("\n次の確認をしてください:");
    Console.WriteLine("  1. 生成物をプレーヤーで開き、映像・音声・シーク（ドラッグ）を確認");
    Console.WriteLine("  2. 録画中の操作（アプリ切替）が記録されているか確認");
    Console.WriteLine("  3. 音ズレが無いか確認");
    Console.WriteLine(size > 10_000 ? "\n録画: 成功 ✅" : "\n録画: ファイルが異常に小さい — 要確認 ⚠");
}
else
{
    Console.WriteLine("\n録画: ファイルが生成されず ❌");
    return 1;
}
return 0;
