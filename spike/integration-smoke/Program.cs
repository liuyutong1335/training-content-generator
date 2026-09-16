using System.Diagnostics;
using System.Text.Json;
using GateACheck; // Mp4Inspector（csproj でソースリンク）
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
var events = InspectEvents(Path.Combine(projectDir, "events.jsonl"));

Check("1. CaptureStarted 契機で Session を開始した", pass1,
    $"{sessionStartedAt} ms に開始（Engine の StateChanged(Recording) より後）");
Check("2. Engine と Session の論理時間が一致（±500ms）", pass2,
    $"差 {diff:F0} ms");
Check("3. events.jsonl が契約 §20/§8.1 どおり", events.Valid, events.Summary);

// MP4 を実際に開いて確認する（監査 SP-5 対応: 従来は MP4 を一度も開いていなかった）。
// 判定: MP4 の Duration が Engine 論理 Duration と ±1s で一致し、かつ最終イベント時刻を確実に覆う
var mp4 = Mp4Inspector.Inspect(Path.Combine(projectDir, "raw", "recording.mp4"));
var mp4CoversEvents = mp4.DurationSeconds * 1000 >= events.LastTimestampMs - 500;
bool pass4 = Math.Abs(mp4.DurationSeconds - result.Duration.TotalSeconds) <= 1.0 && mp4CoversEvents;
Check("4. MP4 がイベントタイムラインを覆う（実 MP4 検査）", pass4,
    $"mp4={mp4.DurationSeconds:F2}s / engine={result.Duration.TotalSeconds:F2}s / 最終イベント={events.LastTimestampMs} ms");

var allPass = pass1 && pass2 && events.Valid && pass4;
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

static EventInspection InspectEvents(string eventsPath)
{
    if (!File.Exists(eventsPath))
    {
        return new EventInspection(false, "events.jsonl なし", -1);
    }

    var lines = File.ReadAllLines(eventsPath).Where(l => l.Length > 0).ToArray();
    if (lines.Length == 0)
    {
        return new EventInspection(false, "空ファイル", -1);
    }

    string? startedLine = null;
    string? stoppedLine = null;
    long lastSeq = 0;
    long lastTs = -1;
    var seqOk = true;
    var startedTsOk = false;
    var violations = new List<string>();

    foreach (var line in lines)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        var seq = root.GetProperty("seq").GetInt64();
        var ts = root.GetProperty("timestampMs").GetInt64();

        if (seq <= lastSeq)
        {
            seqOk = false; // 契約 §8.1: Seq は 1 開始・単調増加
            violations.Add($"seq 非単調 ({lastSeq} → {seq})");
        }
        lastSeq = seq;
        lastTs = ts;

        if (type == "recording.started")
        {
            startedLine = line;
            startedTsOk = ts == 0; // 契約 §20: started の timestampMs は 0
            if (ts != 0) { violations.Add($"recording.started の timestampMs={ts}"); }
        }
        if (type == "recording.stopped")
        {
            stoppedLine = line;
        }
    }

    // 契約 §20: recording.stopped は終端イベント（最終行）
    var stoppedLast = stoppedLine is not null && lines[^1] == stoppedLine;
    if (stoppedLine is not null && !stoppedLast)
    {
        violations.Add("recording.stopped が最終行ではない");
    }

    var valid = startedLine is not null && stoppedLine is not null && seqOk && startedTsOk && stoppedLast;
    var summary = $"recording.started: {Truncate(startedLine ?? "なし")} / recording.stopped: {Truncate(stoppedLine ?? "なし")}"
                  + (violations.Count > 0 ? $" / 違反: {string.Join(", ", violations.Take(3))}" : " / seq 単調・started ts=0・stopped 最終行 ✓");
    return new EventInspection(valid, summary, lastTs);

    static string Truncate(string s) => s.Length <= 120 ? s : s[..120] + "…";
}

/// <summary>events.jsonl の契約適合検査結果（監査 SP-4 対応: type の存在確認から検証強化）。</summary>
sealed record EventInspection(bool Valid, string Summary, long LastTimestampMs);
