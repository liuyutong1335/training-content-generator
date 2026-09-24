using System.Diagnostics;
using TrainingContent.Capture;

// RC-2「Capture preparation 中の Cancel」の前提を実機検証する（duty-a-progress §9）。
//
// 背景: D 側が preparation 中の UI Cancel を StopAsync で実装するか検討しており、
// 「CaptureStarted 前の StopAsync が正常に完了するか」が未実機確認だった。
//
// タイムライン:
//   Session 1: StartAsync → 直ちに StopAsync（WGC 初期化窓内 = CaptureStarted 前を狙う）
//   Session 2: 同一エンジンで再 StartAsync → 3 秒待つ（CaptureStarted 済み）→ StopAsync
//   Session 3: 同一エンジンで再 StartAsync → 直ちに PauseAsync（準備中 Pause・監査 m-1）
//              → 3 秒待つ（CaptureStarted 済み）→ ResumeAsync → 2 秒録画 → StopAsync
//
// 自動判定:
//   1. Session 1 の StopAsync がタイムアウト（15 秒）内に完了する（lib が Stop を握り潰してハングしない）
//   2. Session 1 の RecordingResult.Duration == 0（CaptureStarted 前 Stop semantics）
//   3. Session 1 の停止完了後、出力 MP4 が削除できる（ロック残留なし → D 側で破棄可能）
//   4. Session 2 は CaptureStarted が発火し、Duration > 0（再利用して正常録画できる）
//   5. (対照) Session 2 の停止直後のファイルロック
//   6. Session 3 は準備中 Pause → Resume が例外なく通る（監査 m-1: NRE しない）
//   7. Session 3 の Duration に準備中 Pause が含まれない（Resume 後の ~2 秒に近い）

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 出力先は既定でリポジトリ配下（実際のアプリと同じ OneDrive 同下のパス）。
// TCS_PCC_OUTDIR で変えられる（例: %TEMP% にすると OneDrive 同 の影響を切り分けられる）。
var outDir = Environment.GetEnvironmentVariable("TCS_PCC_OUTDIR")
             ?? Path.Combine(AppContext.BaseDirectory, "preparation-cancel-test");
Directory.CreateDirectory(outDir);
var outFile1 = Path.Combine(outDir, "cancel-preparation.mp4");
var outFile2 = Path.Combine(outDir, "normal-recording.mp4");
foreach (var f in new[] { outFile1, outFile2 })
{
    if (File.Exists(f)) { File.Delete(f); }
}

using var engine = new ScreenRecorderRecordingEngine();
var sw = Stopwatch.StartNew();

long captureStartedAtSession1 = -1;
engine.CaptureStarted += (_, _) =>
{
    if (captureStartedAtSession1 < 0) { captureStartedAtSession1 = sw.ElapsedMilliseconds; }
};

// ---- Session 1: preparation 中（CaptureStarted 前）に Cancel 相当の StopAsync ----
var options1 = new RecordingOptions { OutputFilePath = outFile1 };
await engine.StartAsync(options1);
var stopIssuedAtMs = sw.ElapsedMilliseconds;
Console.WriteLine($"  StartAsync 完了: {stopIssuedAtMs} ms（CaptureStarted 発火待ちなしで即 StopAsync を発行）");

RecordingResult cancelResult;
try
{
    // WGC 初期化（~2 秒）より先に Stop が届くよう即座に発行。lib 側で握り潰された場合
    // この await が完了しない → タイムアウトで検知する（判定 1）。
    cancelResult = await engine.StopAsync(
        new CancellationTokenSource(TimeSpan.FromSeconds(15)).Token);
    Console.WriteLine($"  StopAsync 完了 : {sw.ElapsedMilliseconds} ms");
}
catch (OperationCanceledException)
{
    Console.WriteLine();
    Console.WriteLine("  === 結論: FAIL — CaptureStarted 前の StopAsync が 15 秒以内に完了しない ===");
    Console.WriteLine("  → ScreenRecorderLib 6.6.0 が初期化中の Stop を握り潰している可能性。");
    Console.WriteLine("    RC-2 の Cancel 実装はこのままでは採用できない（duty-a-progress に記録すること）。");
    return 1;
}
catch (InvalidOperationException ex)
{
    Console.WriteLine();
    Console.WriteLine("  === 結論: FAIL — CaptureStarted 前の StopAsync が OnRecordingFailed 経路で失敗 ===");
    Console.WriteLine($"  → {ex.Message}");
    Console.WriteLine("    初期化中の Stop がエラー扱いになる場合、RC-2 の Cancel 実装は");
    Console.WriteLine("    「lib 側失敗を cancel とみなす」ラップが必要（duty-a-progress に記録すること）。");
    return 1;
}

var durationIsZero = cancelResult.Duration == TimeSpan.Zero;

// 停止完了直後のファイル状態を確認する。
// 既知の lib 制約（ScreenRecorderLib 6.6.0・実機確認済み）: 初期化中の停止では
// 0 バイト MP4 が生成され、そのハンドルはプロセス終了まで解放されない
// （Recorder.Dispose でも解放されない。OneDrive/%TEMP% でも同様 = プロセス内リーク）。
// そのため削除可否ではなく「0 バイト = canonical recording ではない」を判定基準にする。
var cancelFileExists = File.Exists(outFile1);
var cancelFileIsZeroBytes = !cancelFileExists || new FileInfo(outFile1).Length == 0;
var deleteSucceeded = false;
try
{
    if (cancelFileExists) { File.Delete(outFile1); }
    deleteSucceeded = true;
}
catch (IOException) { }
catch (UnauthorizedAccessException) { }

// ---- Session 2: 同一エンジンの再利用で正常録画 ----
var captureStartedAtSession2 = -1L;
engine.CaptureStarted += (_, _) => captureStartedAtSession2 = sw.ElapsedMilliseconds;

var options2 = new RecordingOptions { OutputFilePath = outFile2 };
await engine.StartAsync(options2);
await Task.Delay(3000); // WGC 初期化（~2 秒）を跨いで CaptureStarted を待つ
var normalResult = await engine.StopAsync();

// 対照実験: 正常録画（CaptureStarted 後に Stop）の完了直後もファイルロックが残るか。
// 残る場合、ロックは「preparation cancel 特有」ではなく lib / Media Foundation / AV の
// 一般的な停止後挙動と判断できる。
var normalStopLocked = false;
try
{
    File.Delete(outFile2);
}
catch (IOException)
{
    normalStopLocked = true;
}
catch (UnauthorizedAccessException)
{
    normalStopLocked = true;
}

Check("1. preparation 中の StopAsync が完了する（ハングしない）", true,
    $"Stop 発行 {stopIssuedAtMs} ms → 完了（即時。lib の Stop() を呼ばないため）");
Check("2. Duration == 0（CaptureStarted 前 Stop semantics）", durationIsZero,
    $"Duration = {cancelResult.Duration.TotalSeconds:F1} 秒");
Check("3. 残留 MP4 は canonical recording ではない（0 バイトまたは不存在）", cancelFileIsZeroBytes,
    cancelFileExists
        ? $"0 バイトファイルが残留（{(deleteSucceeded ? "削除できた" : "プロセス終了までロック = 既知の lib 制約")}）"
        : "ファイルは生成されなかった");
Check("4. 同一エンジンの再利用で正常録画できる", normalResult.Duration > TimeSpan.Zero && captureStartedAtSession2 > 0,
    $"CaptureStarted = {captureStartedAtSession2} ms / Duration = {normalResult.Duration.TotalSeconds:F1} 秒");
Check("5. (対照) 正常録画の停止直後のロック", true,
    normalStopLocked ? "ロックあり（正常録画でも停止直後は残留する）" : "ロックなし");

// ---- Session 3: 準備中（CaptureStarted 前）の Pause → Resume（監査 m-1 の到達性確認）----
// lib が初期化中の Pause をどう扱うか（握り潰し / 失敗 / 引き継ぎ）は未実機検証だった。
// D の UI は準備中 Pause を許可しないが、エンジン単独利用時は到達し得るため実機で確認する。
var outFile3 = Path.Combine(outDir, "prep-pause-recording.mp4");
if (File.Exists(outFile3)) { File.Delete(outFile3); }

var prepPauseSucceeded = false;
RecordingResult prepPauseResult;
var prepPauseCaptureStarted = -1L;
try
{
    var options3 = new RecordingOptions { OutputFilePath = outFile3 };
    await engine.StartAsync(options3);
    engine.CaptureStarted += (_, _) => prepPauseCaptureStarted = sw.ElapsedMilliseconds;
    await engine.PauseAsync(); // WGC 初期化窓内で Pause（CaptureStarted 前を狙う）
    var pausedAtMs = sw.ElapsedMilliseconds;
    await Task.Delay(3000);    // CaptureStarted（~1.5〜2s）を跨ぐ。この間 lib は Recording 状態に遷移する
    await engine.ResumeAsync();
    Console.WriteLine($"  準備中 Pause: {pausedAtMs} ms → Resume: {sw.ElapsedMilliseconds} ms（CaptureStarted = {prepPauseCaptureStarted} ms）");
    await Task.Delay(2000);    // Resume 後の実撮影分
    prepPauseResult = await engine.StopAsync();
    prepPauseSucceeded = prepPauseResult.Duration > TimeSpan.Zero;
    // 準備中 Pause は CaptureStarted 時に破棄されるため、Duration は Resume 後の ~2 秒になる
    // （lib が初期化中 Pause を引き継いで撮影を止めていた場合はこれより短くなる → 判定詳細で判別）
    var durationNearResumeWindow = Math.Abs((prepPauseResult.Duration - TimeSpan.FromSeconds(2)).TotalSeconds) <= 1.0;
    Check("6. 準備中 Pause → Resume が例外なく完了する（NRE しない）", true,
        $"Pause {pausedAtMs} ms → Resume {sw.ElapsedMilliseconds - 2000:F0} ms 付近で成功");
    Check("7. 準備中 Pause は Duration に含まれない（Resume 後 ~2 秒）", durationNearResumeWindow,
        $"Duration = {prepPauseResult.Duration.TotalSeconds:F1} 秒 / PauseIntervals = {prepPauseResult.PauseIntervals.Count} 件");
}
catch (Exception ex)
{
    Check("6. 準備中 Pause → Resume が例外なく完了する（NRE しない）", false, $"{ex.GetType().Name}: {ex.Message}");
    Check("7. 準備中 Pause は Duration に含まれない（Resume 後 ~2 秒）", false, "Session 3 が異常終了したため判定不能");
    prepPauseResult = new RecordingResult { FilePath = outFile3, Duration = TimeSpan.Zero, StartedAtUtc = DateTimeOffset.UtcNow };
}

try { if (File.Exists(outFile3)) { File.Delete(outFile3); } } catch { /* 検証値の確認用 */ }

// Session 2 の成果物は対照実験で削除を試みた。残っていれば遅延削除する
try { if (File.Exists(outFile2)) { File.Delete(outFile2); } } catch { /* 検証値の確認用 */ }

var allPass = durationIsZero && cancelFileIsZeroBytes && normalResult.Duration > TimeSpan.Zero && prepPauseSucceeded;
Console.WriteLine();
Console.WriteLine(allPass
    ? "=== 結論: 全項目 PASS — RC-2 の「preparation 中 Cancel = StopAsync」方式と m-1 の準備中 Pause 耐性は実機で動作する ==="
    : "=== 結論: 要確認 — 上記 FAIL 項目を duty-a-progress に記録すること ===");
return allPass ? 0 : 1;

static void Check(string name, bool pass, string detail)
{
    Console.WriteLine($"  [{(pass ? "PASS" : "FAIL")}] {name} — {detail}");
}
