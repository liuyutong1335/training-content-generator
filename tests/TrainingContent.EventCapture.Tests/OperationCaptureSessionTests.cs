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
}
