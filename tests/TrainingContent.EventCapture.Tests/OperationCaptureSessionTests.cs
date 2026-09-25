using System.Diagnostics;
using System.Text.Json;
using Xunit;
using TrainingContent.EventCapture;

namespace TrainingContent.EventCapture.Tests;

/// <summary>
/// OperationCaptureSession の例外安全性と events.jsonl 終端イベント規約の検証。
/// D5-B 統合テストで指摘された 2 件（Start 部分失敗時のフック / ワーカー残留 /
/// recording.stopped が終端イベントにならない）に対応。
///
/// 実機のグローバル フック / UIA / スクリーンショットを伴うため、対話ログオン中の
/// 実 Windows セッションで実行すること。
/// xUnit は同一クラス内ではテストを並列実行しないため、セッション系テストは本クラスに
/// 集約する（複数セッションを並列動作させるとグローバル フックが実入力を重複記録する）。
/// </summary>
public class OperationCaptureSessionTests : IDisposable
{
    private readonly string _directory;
    private readonly string _eventsPath;

    public OperationCaptureSessionTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "event-capture-tests", Guid.NewGuid().ToString());
        _eventsPath = Path.Combine(_directory, "events.jsonl");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private OperationCaptureSession CreateSession()
    {
        // 既定の自プロセス判定フィルタだとテストランナー配下で Event が全て弾かれるため全通しにする。
        return new OperationCaptureSession(
            _directory,
            new OperationCaptureOptions { WindowFilter = _ => false });
    }

    private OperationCaptureSession CreateSessionWithoutHooks()
    {
        // 実フックを設置しないセッション（テスト実行中の実入力が queue へ流れ込むと
        // UIA の重い処理で worker が滞留し、書き出し順の検証がタイミング依存になるため）。
        var session = CreateSession();
        session.InstallHooksForTest = false;
        return session;
    }

    [Fact]
    public void Start失敗時_recording_startedの書き込みエラーでもフックとワーカーを残さない()
    {
        // events.jsonl を他プロセスに掴まれている状態にし、recording.started の書き出しを失敗させる
        // （この時点でフックスレッド / ワーカースレッドは既に起動済み。指摘 §1 のシナリオ）。
        Directory.CreateDirectory(_directory);
        using var lockFile = new FileStream(
            _eventsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        using var session = CreateSession();

        Assert.ThrowsAny<Exception>(() => session.Start());

        // フックスレッド / ワーカースレッドが残留していないこと。
        Assert.False(session.IsHookThreadAliveForTest);
        Assert.False(session.IsWorkerAliveForTest);
        // Start 失敗セッションは「録画中」ではないこと（RC-2: Cancel 判定用）。
        Assert.False(session.IsRecording);

        // 失敗したセッションの Dispose が安全に完了すること（ハングしない）。
        session.Dispose();
    }

    [Fact]
    public void IsRecordingはStart成功からStopまでtrue()
    {
        using var session = CreateSession();

        // Start 前: 録画中ではない（Cancel なら Dispose する分岐）。
        Assert.False(session.IsRecording);

        session.Start();
        Assert.True(session.IsRecording);

        var _ = session.Stop();
        Assert.False(session.IsRecording);
    }

    [Fact]
    public void Stop中にwriterが異常してもリソース解放が行われる()
    {
        using var session = CreateSession();
        session.Start();

        // 録画開始後に events.jsonl を排他ロックし、recording.stopped の書き込みを必ず失敗させる。
        using var lockFile = new FileStream(_eventsPath, FileMode.Open, FileAccess.Read, FileShare.None);

        // 書き込み失敗は例外として呼び出し元へ伝播する
        // （終端イベントを欠く recording を integrated として確定させないため。Coordinator が fault 扱いにする）。
        Assert.ThrowsAny<Exception>(() => session.Stop());

        // それでもリソース解放は実行されていること（try/finally）。
        Assert.False(session.IsHookThreadAliveForTest);
        Assert.False(session.IsWorkerAliveForTest);

        lockFile.Dispose(); // 自分のロックを解除してからファイル内容を確認する

        // 終端イベントは書き込めていない。
        var lines = File.ReadAllLines(_eventsPath);
        Assert.DoesNotContain(lines, line => line.Contains("\"recording.stopped\""));

        // 例外後の Dispose も安全に完了すること（ハングしない）。
        session.Dispose();
    }

    [Fact]
    public void queueに滞留があってもrecording_stoppedは最終行_かつseqとtimestampMsは単調()
    {
        using var session = CreateSession();
        session.Start();

        // ワーカー（UIA + スクリーンショット: 1 Event 百ms 級）が追いつかない量を投入し、
        // Stop 時点で queue に未処理イベントが残った状態を作る（指摘 §2 のシナリオ）。
        const int injectedClicks = 10;
        for (var i = 0; i < injectedClicks; i++)
        {
            session.EnqueueMouseClickForTest(10 + i * 3, 10 + i * 3);
        }

        var durationMs = session.Stop();

        var lines = File.ReadAllLines(_eventsPath);
        var events = lines
            .Select(line =>
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString()!;
                long seq = root.GetProperty("seq").GetInt64();
                long ts = root.GetProperty("timestampMs").GetInt64();

                // mouse.click の投入座標（テスト実行中の実入力が混ざっても壊れないよう、
                // 欠落判定は件数ではなく座標の存在で行う）。
                int x = -1, y = -1;
                if (type == "mouse.click")
                {
                    var payload = root.GetProperty("payload");
                    x = payload.GetProperty("x").GetInt32();
                    y = payload.GetProperty("y").GetInt32();
                }

                return (Type: type, Seq: seq, TimestampMs: ts, X: x, Y: y);
            })
            .ToList();

        // recording.stopped が最終行であること（stop 時に queue に残っていた
        // ユーザー Event は全て終端より前に書かれている）。
        Assert.Equal("recording.stopped", events[^1].Type);

        // stop 前に投入した mouse.click が 1 件も欠落しないこと。
        for (var i = 0; i < injectedClicks; i++)
        {
            Assert.Contains(events, e => e.Type == "mouse.click" && e.X == 10 + i * 3 && e.Y == 10 + i * 3);
        }

        // seq は 1 始まりで厳密に単調増加すること。
        var seqs = events.Select(e => e.Seq).ToArray();
        Assert.Equal(Enumerable.Range(1, events.Count).Select(i => (long)i), seqs);

        // timestampMs は非減少であること（ファイル行順 = timestamp 順）。
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i - 1].TimestampMs <= events[i].TimestampMs,
                $"timestampMs が逆転: 行 {i} ({events[i - 1].TimestampMs}ms) → 行 {i + 1} ({events[i].TimestampMs}ms)");
        }

        // recording.stopped の timestampMs は Stop が返した durationMs と一致すること。
        Assert.Equal(durationMs, events[^1].TimestampMs);
    }

    [Fact]
    public void Pause中に投入したイベントはResume後も境界時刻で洩れず破棄される()
    {
        // 監査 NEW-2: 境界フィルタが < だと Pause 中に発生した Event の
        // 凍結 timestampMs（== boundary）が Resume 後の処理ですり抜けて記録される。
        using var session = CreateSession();
        session.Start();
        session.Pause();

        // Pause 中の Canonical 時刻は凍結値（= boundary）で採番される。
        session.EnqueueMouseClickForTest(777, 777);

        session.Resume();
        session.Stop();

        var lines = File.ReadAllLines(_eventsPath);
        Assert.DoesNotContain(lines, line => line.Contains("mouse.click") && line.Contains("\"x\": 777"));
        // Pause 自体のライフサイクルイベントは記録されていること（フィルタが広すぎないことの確認）。
        Assert.Contains(lines, line => line.Contains("\"recording.paused\""));
        Assert.Contains(lines, line => line.Contains("\"recording.resumed\""));
    }

    [Fact]
    public void ワーカーのJoinがタイムアウトしてもrecording_stoppedは最終行_後発Eventは破棄される()
    {
        // 監査指摘 §2: worker の Join(15000) タイムアウト後も finalization が進むため、
        // recording.stopped の後に mouse / specialKey / shortcut が append されうる問題。
        // WindowFilter を worker の滞留ポイント（IsFiltered 内、ロック非保持）に使い、
        // Stop を Join タイムアウトさせてから worker を解放し、後発 Event が破棄されることを確認する。
        using var workerStuck = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();

        using var session = new OperationCaptureSession(
            _directory,
            new OperationCaptureOptions
            {
                // 解放までは worker をフィルタ内で滞留させ、解放後は通す
                // （UIA + スクリーンショットを経て Append まで進ませる）。
                WindowFilter = _ =>
                {
                    workerStuck.Set();
                    releaseWorker.Wait(5000);
                    return false;
                }
            });
        session.Start();
        session.WorkerJoinTimeoutForTest = 500;

        session.EnqueueMouseClickForTest(500, 500);
        Assert.True(workerStuck.Wait(5000), "ワーカーがフィルタに到達していない");

        // Join がタイムアウトしても Stop は完了する。
        var durationMs = session.Stop();

        var lines = File.ReadAllLines(_eventsPath);
        Assert.Equal("recording.stopped", GetEventType(lines[^1]));
        Assert.Equal(durationMs, GetEventTimestamp(lines[^1]));

        // worker を解放: 後追いで進んだ mouse.click は recording.stopped の後に
        // 書かれることなく破棄されること。
        releaseWorker.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.IsWorkerAliveForTest && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.False(session.IsWorkerAliveForTest);

        lines = File.ReadAllLines(_eventsPath);
        Assert.Equal("recording.stopped", GetEventType(lines[^1]));
        Assert.DoesNotContain(lines, line => line.Contains("mouse.click") && line.Contains("\"x\": 500"));

        // Join タイムアウト後の Dispose も安全に完了すること（ハングしない）。
        session.Dispose();
    }

    private static string GetEventType(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("type").GetString()!;
    }

    private static long GetEventTimestamp(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("timestampMs").GetInt64();
    }

    private static long GetEventSeq(string line)
    {
        using var doc = JsonDocument.Parse(line);
        return doc.RootElement.GetProperty("seq").GetInt64();
    }

    // ---- Event seq の物理発生順保証（D の E+F-B production runtime smoke 指摘） ----
    //
    // 保留左クリック（ダブルクリック判定待ち、最大約 900ms）より物理時刻が後のキー
    // Event を先に書くと、timestampMs は click < key のまま seq だけ逆転する。
    // StepBuilder は行順（seq 順）を TrainingStep.Order とするため、Manual / Review の
    // Step 順が物理操作と逆になる。worker 側の書き出し保留（reorder buffer）で防ぐ。
    //
    // 実フックは実入力も拾うため、注入 Event は実入力と混ざっても一意に識別できる
    // 座標 / ショートカット名を使う（実フックが生成しうるショートカットは契約の
    // 5 種のみなので "Ctrl+" で始まるテスト専用名は衝突しない）。

    private const int OrderingClickX = 4321;
    private const string OrderingShortcutName = "Ctrl+OrderingTest";

    [Fact]
    public void 保留クリックより物理時刻が古いEventは保留を待たずに書かれる()
    {
        using var session = CreateSessionWithoutHooks();

        // 保留クリックの物理時刻が未来（= 対象 Event より後から保留されるクリック）
        // なので、対象 Event はその確定を待たずに書いてよい。待つと無意味な遅延になる。
        var pendingQpc = new long?[] { Stopwatch.GetTimestamp() + TimeSpan.FromSeconds(60).Ticks };
        session.PendingLeftClickQpcSourceForTest = () => pendingQpc[0];
        session.Start();

        session.EnqueueShortcutForTest(OrderingShortcutName);

        // 書き出されるまで短期間で待つ（保留を待つ実装だとここでタイムアウトする）。
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.ReadAllLines(_eventsPath).Any(l => l.Contains(OrderingShortcutName))
               && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.Contains(File.ReadAllLines(_eventsPath), line => line.Contains(OrderingShortcutName));

        session.Stop();
    }

    [Fact]
    public void 保留クリックより物理時刻が後のキーは保留クリック確定後に書かれる()
    {
        using var session = CreateSessionWithoutHooks();

        session.Start();

        // 保留クリックありの状態を模擬（物理クリック時刻 = clickQpc の保留左クリック）。
        // 録画開始後に発生させる（開始前のクリックは Canonical 時刻が原点前で凍結され、
        // 後続キーと同時刻になりうるため。同時刻は seq で発生順を表す範ちゅう）。
        var clickQpc = Stopwatch.GetTimestamp();
        var pendingQpc = new long?[] { clickQpc };
        session.PendingLeftClickQpcSourceForTest = () => pendingQpc[0];

        // 保留クリックより物理時刻が後のキーを投入 → 確定待ちで保留される。
        session.EnqueueShortcutForTest(OrderingShortcutName);
        Thread.Sleep(300);
        Assert.DoesNotContain(
            File.ReadAllLines(_eventsPath),
            line => line.Contains(OrderingShortcutName));

        // 保留クリックが単クリックとして確定（フックが発火 → queue 経由で後追い到達）。
        pendingQpc[0] = null;
        session.EnqueueMouseClickForTest(OrderingClickX, OrderingClickX, clickQpc);

        session.Stop();

        var lines = File.ReadAllLines(_eventsPath);
        var clickLines = lines.Where(l => l.Contains("mouse.click") && l.Contains($"\"x\":{OrderingClickX}") && l.Contains($"\"y\":{OrderingClickX}")).ToList();
        var keyLines = lines.Where(l => l.Contains(OrderingShortcutName)).ToList();
        Assert.True(clickLines.Count == 1, $"click 件数={clickLines.Count}: " + string.Join(" ||| ", lines));
        Assert.True(keyLines.Count == 1, $"key 件数={keyLines.Count}: " + string.Join(" ||| ", lines));
        var clickLine = clickLines[0];
        var keyLine = keyLines[0];

        // seq は物理発生順（click < key、厳密）。timestampMs は click <= key
        // （同時刻の ties は契約どおり seq が発生順を表す）。
        Assert.True(GetEventSeq(clickLine) < GetEventSeq(keyLine), $"seq が逆転: {clickLine} / {keyLine}");
        Assert.True(GetEventTimestamp(clickLine) <= GetEventTimestamp(keyLine),
            $"timestampMs が逆転: {clickLine} / {keyLine}");
    }

    [Fact]
    public void 保留クリック未確定のままStopしても保留していたEventはstoppedより前に書かれる()
    {
        using var session = CreateSessionWithoutHooks();

        // 保留クリックが最後まで確定しない状態（Stop 時の flush 待ち）を模擬。
        var pendingQpc = new long?[] { Stopwatch.GetTimestamp() };
        session.PendingLeftClickQpcSourceForTest = () => pendingQpc[0];
        session.Start();

        session.EnqueueShortcutForTest(OrderingShortcutName);
        Thread.Sleep(300);
        Assert.DoesNotContain(
            File.ReadAllLines(_eventsPath),
            line => line.Contains(OrderingShortcutName));

        session.Stop(); // queue 締め切り後、worker が保留していた分を書き切ってから stopped を書く

        var lines = File.ReadAllLines(_eventsPath);
        Assert.Equal("recording.stopped", GetEventType(lines[^1]));
        var keyLine = Assert.Single(lines, l => l.Contains(OrderingShortcutName));
        Assert.True(GetEventSeq(keyLine) < GetEventSeq(lines[^1]));
    }

    [Fact]
    public void 処理中クリックの完了より先にPauseしてもそのクリックはpausedより後へ割り込まない()
    {
        // AppendUserEvent 直前の境界再判定の検証: UIA / スクリーンショットなど重い処理の
        // 遅延中に Pause されたユーザー Event が、recording.paused より後へ
        // 「物理時刻は Pause より前」のまま割り込むと seq（発生順）が崩れる。
        using var workerStuck = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();

        // 解放までは worker をフィルタ内で滞留させ、解放後は通す（UIA + スクリーンショットを経て Append まで進ませる）。
        using var session = new OperationCaptureSession(
            _directory,
            new OperationCaptureOptions
            {
                WindowFilter = _ =>
                {
                    workerStuck.Set();
                    releaseWorker.Wait(5000);
                    return false;
                }
            });
        session.InstallHooksForTest = false;
        session.Start();

        session.EnqueueMouseClickForTest(500, 500);
        Assert.True(workerStuck.Wait(5000), "ワーカーがフィルタに到達していない");

        session.Pause();
        releaseWorker.Set();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (session.IsWorkerAliveForTest && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        session.Stop();

        var lines = File.ReadAllLines(_eventsPath);
        Assert.Contains(lines, line => line.Contains("\"recording.paused\""));
        Assert.DoesNotContain(lines, line => line.Contains("mouse.click") && line.Contains("\"x\": 500"));
    }
}
