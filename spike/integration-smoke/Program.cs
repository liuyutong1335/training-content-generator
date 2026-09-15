using System.Diagnostics;
using System.Text.Json;
using TrainingContent.Capture;
using TrainingContent.EventCapture;

// A (RecordingEngine) + B (OperationCaptureSession) の統合スモークテスト。
// B README「A との時間同期」推奨手順をそのまま実機で実行し、
// 停止時に両者の論理時間が一致すること（B README の統合テスト条件）を確認する。
//
// 手順:
//   1. engine.CaptureStarted を待って session.Start()（Canonical 0ms == MP4 の 0 秒）
//   2. 3 秒 → Pause（両方）1.5 秒 → Resume（両方）→ 3 秒 → Stop（両方）
//   3. RecordingResult.Duration と session.Stop() の戻り値を比較
//   4. events.jsonl のライフサイクル Event を検査（契約 §20 の形式）

Console.OutputEncoding = System.Text.Encoding.UTF8;

var projectDir = Path.Combine(Path.GetTempPath(), "tcs-integration-smoke", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
Directory.CreateDirectory(projectDir);
Console.WriteLine($"  プロジェクト: {projectDir}");

using var engine = new ScreenRecorderRecordingEngine();
using var session = new OperationCaptureSession(projectDir);

long sessionStartedAt = -1;
var sw = Stopwatch.StartNew();

engine.CaptureStarted += (_, _) =>
{
    // B README 推奨手順: 撮影開始瞬間まで Session を開始しない
    session.Start();
    sessionStartedAt = sw.ElapsedMilliseconds;
};

await engine.StartAsync(new RecordingOptions
{
    OutputFilePath = Path.Combine(projectDir, "raw", "recording.mp4"),
});

await Task.Delay(3000);
engine.PauseAsync().Wait();
session.Pause();
Console.WriteLine($"  Pause  : {sw.ElapsedMilliseconds,6} ms");
await Task.Delay(1500);
engine.ResumeAsync().Wait();
session.Resume();
await Task.Delay(3000);

var result = await engine.StopAsync();
var eventDurationMs = session.Stop();
sw.Stop();

Console.WriteLine("  --- 計測結果 ---");
Console.WriteLine($"  CaptureStarted（= session.Start 契機）: {sessionStartedAt,6} ms");
Console.WriteLine($"  Engine 論理 Duration   : {result.Duration.TotalMilliseconds,6:F0} ms");
Console.WriteLine($"  Session 論理 Duration  : {eventDurationMs,6} ms");
Console.WriteLine($"  Event 数               : {session.EventCount}");

var diff = Math.Abs(result.Duration.TotalMilliseconds - eventDurationMs);
bool pass1 = sessionStartedAt > 0;
bool pass2 = diff <= 500;
var (pass3, startedLine, stoppedLine) = CheckEvents(Path.Combine(projectDir, "events.jsonl"));

Check("1. CaptureStarted 契機で Session を開始した", pass1,
    $"{sessionStartedAt} ms に開始（Engine の StateChanged(Recording) より後）");
Check("2. Engine と Session の論理時間が一致（±500ms）", pass2,
    $"差 {diff:F0} ms");
Check("3. events.jsonl が契約 §20 どおり", pass3,
    $"recording.started: {startedLine} / recording.stopped: {stoppedLine}");

var allPass = pass1 && pass2 && pass3;
Console.WriteLine();
Console.WriteLine(allPass
    ? "=== 結論: PASS — RecordingEngine + OperationCaptureSession の時間統合は成立 ==="
    : "=== 結論: 要確認 — 上記 FAIL 項目を duty-a-progress / integration-notes に記録すること ===");
Console.WriteLine($"  （生成物は残しています: {projectDir}）");

return allPass ? 0 : 1;

static void Check(string name, bool pass, string detail)
{
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} — {detail}");
}

static (bool, string, string) CheckEvents(string eventsPath)
{
    if (!File.Exists(eventsPath)) { return (false, "ファイルなし", ""); }

    var lines = File.ReadAllLines(eventsPath).Where(l => l.Length > 0).ToArray();
    string? started = null;
    string? stopped = null;
    foreach (var line in lines)
    {
        using var doc = JsonDocument.Parse(line);
        var type = doc.RootElement.GetProperty("type").GetString();
        if (type == "recording.started") { started = line; }
        if (type == "recording.stopped") { stopped = line; }
    }

    if (started is null || stopped is null) { return (false, started ?? "なし", stopped ?? "なし"); }

    return (true, Truncate(started), Truncate(stopped));

    static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "…";
}
