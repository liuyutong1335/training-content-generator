using System.Diagnostics;
using TrainingContent.Capture;

// CaptureStarted イベントの実機検証（duty-a-progress §5 / integration-notes §1）。
// B README「A との時間同期」の実装が実機で期待通り動くかを自動判定する。
//
// タイムライン（録画時間は合計 ~7.5 秒 + WGC 初期化 ~2 秒）:
//   StartAsync → StateChanged(Recording)（直後）
//              → CaptureStarted（撮影開始瞬間・~2 秒後）
//   → 3 秒 → Pause 1.5 秒 → Resume → 3 秒 → Stop
//
// 自動判定:
//   1. StateChanged(Recording) は StartAsync 直後（< 500ms）に発火
//   2. CaptureStarted は StateChanged(Recording) より後に発火（WGC 初期化を挟む）
//   3. CaptureStarted は Pause/Resume を挟んでも合計 1 回
//   4. RecordingResult.Duration は Pause を除外した論理時間（≈ 6 秒 ± 1.5 秒）

Console.OutputEncoding = System.Text.Encoding.UTF8;

var outDir = Path.Combine(AppContext.BaseDirectory, "capture-started-test");
Directory.CreateDirectory(outDir);
var outFile = Path.Combine(outDir, "test.mp4");
if (File.Exists(outFile)) { File.Delete(outFile); }

using var engine = new ScreenRecorderRecordingEngine();

var sw = Stopwatch.StartNew();
long startAsyncReturnedAt = -1;
long stateRecordingAt = -1;
long captureStartedAt = -1;
int captureStartedCount = 0;

var stateMarks = new List<string>();

engine.StateChanged += (_, e) =>
{
    lock (stateMarks) { stateMarks.Add($"StateChanged({e.State}) = {sw.ElapsedMilliseconds} ms"); }
    if (e.State == RecordingState.Recording)
    {
        // StartAsync 直後の 1 回だけ計測（Resume でも発火するが CaptureStarted は無関係）
        if (stateRecordingAt < 0) { stateRecordingAt = sw.ElapsedMilliseconds; }
    }
};
engine.CaptureStarted += (_, _) =>
{
    captureStartedCount++;
    captureStartedAt = sw.ElapsedMilliseconds;
};

var options = new RecordingOptions { OutputFilePath = outFile };
await engine.StartAsync(options);
startAsyncReturnedAt = sw.ElapsedMilliseconds;

await Task.Delay(3000);
await engine.PauseAsync();
Console.WriteLine($"  Pause  : {sw.ElapsedMilliseconds,6} ms");
await Task.Delay(1500);
await engine.ResumeAsync();
await Task.Delay(3000);

var result = await engine.StopAsync();
sw.Stop();

Console.WriteLine("  --- 計測結果 ---");
Console.WriteLine($"  StateChanged(Recording) : {stateRecordingAt,6} ms");
Console.WriteLine($"  CaptureStarted          : {captureStartedAt,6} ms（発火回数 {captureStartedCount}）");
Console.WriteLine($"  論理 Duration           : {result.Duration.TotalSeconds:F1} 秒");
lock (stateMarks)
{
    foreach (var m in stateMarks)
    {
        Console.WriteLine($"    {m}");
    }
}

var gap = captureStartedAt - stateRecordingAt;
bool pass1 = stateRecordingAt is >= 0 and < 500;
bool pass2 = captureStartedAt > 0 && gap > 0 && gap < 10_000;
bool pass3 = captureStartedCount == 1;
bool pass4 = result.Duration is { TotalSeconds: > 4.5 and < 7.5 };

Console.WriteLine();
Check("1. StateChanged(Recording) が StartAsync 直後に発火", pass1,
    $"状態イベント {stateRecordingAt} ms / StartAsync 復帰 {startAsyncReturnedAt} ms");
Check("2. CaptureStarted が撮影開始瞬間に発火", pass2,
    $"撮影開始まで {(double)gap / 1000:F1} 秒（WGC 初期化 ~2 秒の想定・10 秒超は要確認）");
Check("3. CaptureStarted は Pause/Resume を挟んでも 1 回", pass3,
    $"発火回数 = {captureStartedCount}");
Check("4. 論理 Duration が Pause を除外", pass4,
    $"{result.Duration.TotalSeconds:F1} 秒（想定 ≈ 6 秒 + 許容誤差）");

var allPass = pass1 && pass2 && pass3 && pass4;
Console.WriteLine();
Console.WriteLine(allPass
    ? "=== 結論: PASS — CaptureStarted は統合手順（B README）どおりに使える ==="
    : "=== 結論: 要確認 — 上記 FAIL 項目を duty-a-progress に記録すること ===");

return allPass ? 0 : 1;

static void Check(string name, bool pass, string detail)
{
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} — {detail}");
}
