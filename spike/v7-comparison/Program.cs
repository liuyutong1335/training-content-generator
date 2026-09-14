using System.Diagnostics;
using GateACheck;
using ScreenRecorderLib;

// Spike A: ScreenRecorderLib v7.0.1 実録比較（v6.6.0 との対比用）。
// 同一シナリオ（12 秒 Mic + System + Pause/Resume）を v7 API で実行し、
// faststart（v6.6.0 では効かなかった）とフリーズ/FPS（§4.1 の懸念）を確認する。

Console.OutputEncoding = System.Text.Encoding.UTF8;


Console.WriteLine("== ScreenRecorderLib v7.0.1 実録比較 ==");
var displays = Recorder.GetDisplays();
foreach (var d in displays)
{
    Console.WriteLine($"  display: {d.DeviceName} / {d.FriendlyName}");
}
var mics = Recorder.GetSystemAudioCaptureDevices();
var loops = Recorder.GetSystemAudioLoopbackDevices();
Console.WriteLine($"  mic: {mics.FirstOrDefault()?.DeviceName} ({mics.Count} 件)");
Console.WriteLine($"  sys: {loops.FirstOrDefault()?.DeviceName} ({loops.Count} 件)");

var outPath = Path.GetFullPath("v7-comparison.mp4");
if (File.Exists(outPath))
{
    File.Delete(outPath);
}

var sourceOptions = new SourceOptions();
foreach (var d in displays)
{
    sourceOptions.RecordingSources.Add(d);
}
var audioOptions = new AudioOptions
{
    IsAudioEnabled = true,
    AudioSources =
    {
        new CaptureAudioSource(mics.FirstOrDefault()?.DeviceName ?? ""),
        new LoopbackAudioSource(loops.FirstOrDefault()?.DeviceName ?? ""),
    },
};
var recorderOptions = new RecorderOptions
{
    SourceOptions = sourceOptions,
    AudioOptions = audioOptions,
    VideoEncoderOptions = new VideoEncoderOptions
    {
        Framerate = 30,
        IsHardwareEncodingEnabled = true,
        IsMp4FastStartEnabled = true, // v7 で直っているかを確認
        IsFragmentedMp4Enabled = false,
    },
};

var recorder = Recorder.CreateRecorder(recorderOptions);
var startedAt = DateTimeOffset.UtcNow;
var clock = Stopwatch.StartNew();
var completion = new TaskCompletionSource<RecordingCompleteEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
var failure = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
recorder.OnRecordingComplete += (_, e) => completion.TrySetResult(e);
recorder.OnRecordingFailed += (_, e) => failure.TrySetResult(e.Error);

Console.WriteLine("\n== 録画開始: 12 秒（中央で Pause 3 秒） ==");
recorder.Record(outPath);

var captureAnchored = false;
var statusWatcher = new List<string>();
recorder.OnStatusChanged += (_, e) =>
{
    statusWatcher.Add($"{DateTime.Now:HH:mm:ss.fff} {e.Status}");
    if (e.Status == RecorderStatus.Recording && !captureAnchored)
    {
        captureAnchored = true;
        clock.Restart(); // Canonical Timeline は実撮影開始にアンカー（v6.6.0 と同じ方式）
    }
};

await Task.Delay(5000);
Console.WriteLine("== Pause ==");
recorder.Pause();
var pauseStart = clock.Elapsed;
await Task.Delay(3000);
Console.WriteLine("== Resume ==");
recorder.Resume();
var pauseEnd = clock.Elapsed;
await Task.Delay(5000);
Console.WriteLine("== Stop ==");
recorder.Stop();

var finished = await Task.WhenAny(completion.Task, failure.Task);
if (finished == failure.Task)
{
    Console.WriteLine($"録画失敗 ❌: {await failure.Task}");
    return 1;
}
var e2 = await completion.Task;
// 論理 Duration = 実撮影時間から Pause を除く（契約 §5.2・v6.6.0 の CanonicalDuration と同じ式）
var logicalSeconds = clock.Elapsed.TotalSeconds - (pauseEnd - pauseStart).TotalSeconds;
var wallSeconds = (DateTime.UtcNow - startedAt).TotalSeconds;

var mp4 = Mp4Inspector.Inspect(e2.FilePath);
Console.WriteLine("\n======== v7.0.1 結果 ========");
Console.WriteLine($"  file        : {e2.FilePath} ({new FileInfo(e2.FilePath).Length:N0} bytes)");
Console.WriteLine($"  論理 Duration: {logicalSeconds:F1}s / mp4: {mp4.DurationSeconds:F1}s / 実経過: {wallSeconds:F1}s");
Console.WriteLine($"  Pause       : {pauseStart.TotalSeconds:F1}-{pauseEnd.TotalSeconds:F1}s（論理時間から除外）");
Console.WriteLine($"  tracks      : video={mp4.VideoTrackCount} audio={mp4.AudioTrackCount}");
Console.WriteLine($"  faststart   : {(mp4.IsFastStart ? "OK ✅（v6.6.0 からの改善！）" : "NG — moov 末尾のまま（v6.6.0 と同じ）")}");
Console.WriteLine($"  状態遷移    : {string.Join(" / ", statusWatcher)}");
Console.WriteLine("\n比較用メモ: FPS/フリーズ確認はこの mp4 を 6.6.0 の gate-a-*.mp4 と並べて再生してください");
return 0;
