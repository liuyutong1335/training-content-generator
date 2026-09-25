# 統合時の対齐事項（例会で決めること）

- 作成: 2026-09-14（担当 A / Liu Yutong）
- 目的: git の衝突はないが、**モジュール境界の設計的重複**を統合前に決めておく

## 1. Master Clock の帰属

- 現状: B が `TrainingContent.EventCapture/MasterClock.cs` を実装、A の `ScreenRecorderRecordingEngine` も内部に Master Stopwatch を持つ
- 契約 §5（Canonical Timeline = 録画開始 0ms・Pause 除外）を全員が守る前提で、**どちらの時計を正とするか**を決定する
- 案: B の MasterClock を正とし、A の Engine は `IRecordingEngine.StateChanged` イベントで B が start/pause/resume に同期する（A の Stopwatch は Engine 内部の自己記録用に降格）
- **A からの対応 (2026-09-15)**: 上記案を A も受け入れ。あわせて B README「A との時間同期」で要望のあった
  **`IRecordingEngine.CaptureStarted` イベントを実装済み**（`feature/capture`）。
  実際の撮影開始瞬間（`RecorderStatus.Recording` 初回検出・内部クロック `Restart()` と同一点）に 1 回だけ発火するので、
  D は B README 推奨手順の `session.Start()` を **`StateChanged(Recording)` ではなく `CaptureStarted` で始めること**。
  これにより「撮影開始後に原点を張り直す」代替策（`RebaseClockToNow()`）は原則不要になる

## 2. Screenshot サービスの帰属

- 現状: B が `EventCapture/ScreenshotCapture.cs`（イベント連動 → `screenshots/original/`）、担当 C の領域に Step 用 Screenshot（→ `screenshots/edited/`）がある
- 契約 §10/§12 の original / edited 区分で役割分担は明瞭だが、**original 撮影サービスの実装をどちらのプロジェクトに置くか**を決定する
- 案: 撮影は B（イベントと同時に撮る）・加工（Redaction）は C。C が B の型を参照しなくていいよう、撮影結果のパスだけ契約 §10 の payload 形式で受け渡す
- **決定（2026-09-18・A 確認）**: 上記案を確定とする。実装レベルで両担当とも案どおり —
  B は `EventCapture/Screenshot/ScreenshotCapture.cs`（original 撮影・main 実装済み）、C は `TrainingContent.Screenshot/`
  （Redaction・独立プロジェクトで B の型に非依存・`ManualScreenshotPath` でパス受け渡し）を実装済み。
  矛盾なし・追加の設計判断は不要。例会なしの方針のため実装整合の確認をもって確定とする
  **※C 側の `TrainingContent.Screenshot/` は未マージの `feature/step-screenshot-manual` に存在（main には未取込）— 上記は C ブランチ上のコードで確認したもので、マージ後に本文書を再確認すること**

## 3. sln へのプロジェクト登録

- B の `TrainingContent.EventCapture` が `TrainingContentGenerator.sln` に未登録
- 案: マージ担当（D）が統合時に sln へ追加。x64 プラットフォーム必須（ScreenRecorderLib と同じ制約）

## 4. 音声はミックスで MP4 に収まる（全員向け情報）

- ScreenRecorderLib v6.6.0 は マイク(Input) + システム音声(Output) を**同一音声トラックにミックス**する
- 独立トラックで管理したい場合（NessStudio 方式）は raw 構成の変更（Breaking Change）になるため、必要なら早期に契約変更手順（§27）を使うこと

## 5. 録画準備中の Cancel と EventCapture の cleanup（RC-2 決め / 2026-09-17 B 回答）

D より提示のあった「Capture preparation 中の Cancel」について、`OperationCaptureSession` の
cleanup semantics は次のとおり。判定用に **`IsRecording` プロパティを追加済み**（次の B の PR に同梱）。

- **状態別の cleanup（確認事項 1 への回答）**:
  - **Start 前**: セッションは何も生成していない（スレッド / フック / ディレクトリも作らない）。
    `Dispose()` のみで完全。`Stop()` は `InvalidOperationException` を投げる
  - **Start 失敗**: Start 内で例外安全に後片付け済み（フック解除 / ワーカー停止 / `_writer = null` で以後の書き込み不能）。
    セッションは再利用不可。呼び出し側は `Dispose()` のみ呼べばよい
  - **正常 Stop**: フック停止 → drain → textEntry flush → `recording.stopped` 終端 → durationMs 返却。
    以後 1 セッション = 1 録画（再利用不可）
- **CaptureStarted 前・`session.Start()` 未実行なら（確認事項 2 への回答）**:
  **Stop ではなく Dispose が正しい**。目安として `session.IsRecording` が false のときは Dispose 経由
- **recording.started 前/後の差（確認事項 3 への回答）**:
  `recording.started` は `Start()` の最終ステップで書かれるため、**Start() が例外なく戻った時点で必ず存在する**。
  外部から観測できる境界は「Start() が正常に戻ったか」それ以降はそれ以前という区別でよい。
  started を書けずに失敗した場合、そのセッションの events.jsonl は契約 §20 のライフサイクル構造を
  満たさない（canonical artifact にしないこと）
- **Cancel 時に artifacts を残さない前提（確認事項 4 への回答）**: EventCapture 側は問題なし。
  書き込み先はすべて `ProjectDirectory` 配下（events.jsonl / screenshots/original/）で、
  プロセス横断の状態は持たない。注意は 2 点:
  - **削除は Stop / Dispose の後で行う**（セッション生存中の削除は書き込み例外の原因になる。
    writer は書き込みの都度 open/close するため、Dispose 後は EventCapture 自身がハンドルを掴まない）
  - Start 失敗や cancel で不完全になった events.jsonl は canonical と見なさず削除してよい
    （MP4 など Engine 側成果物の取り扱いは A/D 管轄）

## 6. PR #11 後の監査残留 2 件の B 対応（2026-09-24 / feature/event-capture-followup）

D 側の再確認で残っていた既存監査項目 2 点を EventCapture 内で修正した（Phase 0 契約は不変、D 側の代替修正は不要）:

1. **MasterClock のスレッド安全性**: `Pause` / `Resume`（アプリスレッド、`_stateSync` 保持）と
   `ToCanonicalMs` / `IsPaused` / `NowMs`（ワーカー スレッド）が `MasterClock._pauseIntervals` を
   非同期に読み書きしており、並行時に「Collection was modified」や torn state による
   例外・event drop の可能性があった。**全公開メソッドを内部ロックで直列化**して解消。
   あわせて `OperationCaptureSession._pauseBoundaryMs`（`long?` の非アトミック読み書き、同型の問題）
   を `Volatile` アクセスの `long`（未設定 = -1）に変更。
2. **worker Join タイムアウト後の後発 Event**: `Stop()` の `Join(15000)` がタイムアウトしても
   finalization は進むため、`recording.stopped` の後に mouse / specialKey / shortcut が
   append されうる問題（textEntry は `_textFlushFinal` で防御済みだったが他の Event 種は未防御）。
   フラグを全ユーザー Event 対応の `_userEventsFinal` に拡張し、worker 側の append を
   `_textSync` 直列化 + フラグ検査で阻否。stopped が常に最終行であることが保たれる。
   - タイムアウト時に破棄されるのは「stop 後の取りこぼし得る後発 Event」のみで、
     queue に残っていた stop 前の Event は Join が成功する限り従来どおり全件書かれる
     （`queueに滞留があってもrecording_stoppedは最終行` テストで継続検証）
- テスト: MasterClock 並行呼び出しテスト（Pause/Resume × ToCanonicalMs を 1 秒間連打）と、
  Join タイムアウトを WindowFilter 滞留 + 短縮タイムアウトで再現する session テストを追加。全 46/46 PASS。

## 7. 監査 Minor 残の B 一括対応（2026-09-24 / feature/event-capture-quality-2）

audit-2026-09-18 の B 担当残留 Minor 5 件を EventCapture 内で修正した（Phase 0 契約は不変）:

1. **MIN-1（seq 重複 / 行破損）**: `EventTimelineWriter` が seq を最終行だけから読むため、
   クラッシュで半壊した最終行の後に追記すると seq が 1 から再開（§8.1 違反）し、
   追記が半壊行に連結されて 1 行が完全破損していた。**全行から最大可読 seq を採用** +
   **開始時に末尾改行を正規化**（半壊行は分離される。半壊行の seq は読めないため再使用になり得るが、
   1 からやり直すよりベストエフォートで最大可読値を継続する方が良い）。
2. **MIN-2（KB-2 fail-closed の漏れ）**: UIA の `IsPassword` プロパティ取得例外が
   `false` に潰れ、パスワード入力が keyCount 採番され得た（§11.1）。`SafeBool` を三値化し、
   **取得失敗（null）は fail-closed で sensitive 側に倒す**。表示用の `IsKeyboardFocusable` は
   従来どおり fail-open。
3. **Enqueue の check-then-act 競合（監査外の新規指摘）**: `IsAddingCompleted` チェックと
   `Add` の間に `CompleteAdding` が入ると、LL Hook コールバック内で未処理例外 → プロセス墜落の
   可能性。停止瞬間の入力破棄として握り潰す。
4. **MIN-5（保留クリックのタイマー世代競合）**: 前の保留のタイマー コールバックが飛行中に
   保留が更新されると、新しい保留を判定時間待たずに flush しダブルクリックが 2 単発に分裂
   し得た。**世代カウンタでコールバックの有効性を確認**。
5. **MIN-6 / RC-6（IsRecording が Start 実行中も true / Start-Dispose 競合）**:
   `_startCompleted`（volatile）を導入し、**recording.started の書き出し成功後に IsRecording が
   true になる**よう補正。Stop は `_startCompleted` を要求し、Start 実行中の Dispose は
   `_startDone` シグナルで完了を待ってから後片付けする（フック設置との競合解消）。
- テスト: 半壊最終行からの seq 継続テストを追加。EventCapture.Tests 47/47 PASS（2 回実施）。
  MIN-5 / MIN-6 はレース再現が困難なため実装レビュー + 既存回帰で担保。

## 8. Recording Finalization Transaction の境界（2026-09-24 / D からの相談・A 提案）

D 側で StepBuilder を含む Recording Finalization Transaction を実装するにあたり、
「MP4 の確定（staging → canonical 置換）を transaction の最後に制御したい」という要望を D から受領。
現行エンジンは StopAsync 完了時点で staging → canonical 置換まで行うため、
「project.json 保存の後に録画を確定する」順序が作れない（置換成功 + project.json 保存失敗で
new MP4 + old RecordingInfo / Steps が残る）。

### 選定（3 案比較）

| 案 | 判定 | 理由 |
|---|---|---|
| ① StopAsync は staging で確定し、caller が Commit / Abort | **採用** | opt-in flag で既定動作が不変（契約 §7・B・既存ツールに影響なし）。Move 失敗時の staging 保持（R-2 の実機知見）を Engine が担い続ける。D は staging の命名・置換・失敗扱いを複製しなくてよい |
| ② staging path を caller に返し D が canonical replacement を所有 | 不採用 | `RecordingResult.FilePath`（= canonical）の扱いが全 caller で変わり契約上の変更になる。Move 失敗の意味論が D 側に複製される |
| ③ Engine からの rollback / backup contract | 不採用 | backup 復元自体がロックで失敗し得るため状態が増えるだけ。①で同じ保証が得られる |

### 実装（feature/deferred-commit-boundary）

- `RecordingOptions.DeferredCommit`（既定 false）を追加。true の場合:
  - StopAsync は録画成功時に **staging を保持したまま**戻り、`RecordingResult` は
    `PendingCommit = true`・`FilePath = staging パス`・`PendingCommitPath = canonical 予定地`。
    **Duration / PauseIntervals / StartedAtUtc は stop 完了時点で固定**（Commit をいつ呼んでも同じ値）
  - `CommitPendingRecording()` — staging → canonical の Move（two-phase finalize の Commit 相当）。
    Move 失敗時は例外だが staging は保持（録画データを失わない。R-2 と同じ方針）
  - `AbortPendingRecording()` — staging の削除のみ。canonical は一切変更されない
  - Dispose は確定待ち staging を削除しない（成功録画の唯一のコピーのため。caller の判断に委ねる）
  - 確定待ちがある状態の再 StartAsync は拒否する（Commit / Abort の機会を失わせないため）
- 既定（false）は従来どおり StopAsync 内で置換まで完了。B・既存ツール・テストに影響なし
- preparation cancel（CaptureStarted 前 Stop）経路は DeferredCommit でも変わらない
  （Duration = 0・staging 削除のみ・canonical 無変更）

### D 側の使い方（transaction への組み込み）

```csharp
var options = new RecordingOptions { OutputFilePath = canonical, DeferredCommit = true };
var result = await engine.StopAsync(ct);        // PendingCommit = true / FilePath = staging
// result.Duration は固定値 → ここで先に project.json / RecordingInfo を保存してよい
try
{
    // EventCapture finalize → StepBuilder → validation → project.json save
    var committed = engine.CommitPendingRecording();  // 最後に MP4 を確定
    // committed.FilePath == canonical（project.json と一致）
}
catch
{
    engine.AbortPendingRecording();   // transaction 失敗: canonical（旧録画）は無傷
}
```

- **実機検証**: `spike/preparation-cancel-check` に Session 4（Commit 経路）/ 5（Abort 経路）を追加。
  canonical に旧録画（"OLD" 3 バイト）を予め置き、StopAsync 時点で canonical が保護されていること・
  Commit で実 MP4 に確定されること・Abort で canonical 無変更かつ staging 削除されることを確認。全 10 項目 PASS
- **状態**: A 側実装・実機検証済み。例会で D 側の受領確認を残すのみ（§1 と同じ扱い）


## 9. Event seq の物理発生順保証（2026-09-25 / D の E+F-B production runtime smoke 指摘・B 対応）

D の E+F-B production runtime smoke で、保留左クリック（ダブルクリック判定待ち、最大約 900ms）
より物理時刻が後の keyboard Event が先に seq を採番され、timestampMs は click < key のまま
seq だけ逆転する contract-level issue を確認（StepBuilder は行順 = TrainingStep.Order なため
Manual / Review の Step 順が物理操作と逆になる）。D 側では sort / 後処理 / seq 書換をしない
方針のため、B 側（EventCapture）で解消した。

### 対応 1: worker 側の書き出し保留（reorder buffer）

- `OperationCaptureSession` の worker は queue から受けた Event を直接書かず、
  `_reorderBuffer` に貯めてから物理時刻（QPC）順に書き出す。
- 先頭（最古）Event より物理時刻が早い保留左クリックが `GlobalMouseHook` 側に残っている間は
  書き出しを中断する。保留クリックはタイマー / 後続クリック / Stop 時 flush のいずれかで
  必ず確定し queue へ届くため、待ちが無期限になることはない。
- `GlobalMouseHook.PendingLeftClickQpc`（新規 public プロパティ）で保留クリックの物理クリック時刻を
  照会できるようにした（`_clickSync` 下で読むため race なし）。
- キー Event は保留クリック確定まで待たされる（最大 ~900ms）が、timestampMs は物理クリック時刻を
  保持したままなので Canonical Timeline の整合は変わらない。append-only / Pause フィルタ /
  stopped 終端 / MIN-5 世代ガードはすべて従来どおり。

### 対応 2: lifecycle 行とユーザー Event の直列化

- 同型の逆転が Pause / Resume でも起き得たため、`recording.paused` / `recording.resumed` の
  書き出しを worker のユーザー Event 書き出し（`AppendUserEvent` / `FlushTextBuffer`）と同じ
  `_textSync` で直列化した。
- `AppendUserEvent` は書き出し直前に境界を再判定する（UIA・スクリーンショットなど重い処理を
  挟んだ間に Pause された Event が、物理時刻「Pause より前」のまま paused 行より後へ割り込まない）。

### 対応 3: writer の一時的書き込み失敗への耐性（追加発見）

- 従来の `EventTimelineWriter.Append` は `_seq++` → `File.AppendAllText` の順で、events.jsonl が
  別プロセス / Defender 等の `FileShare.Read` ハンドルに掴まれると書き込みが IOException になり、
  **seq だけが空振りして進み該当 Event は欠落**していた（worker の 1 Event 単位の catch で握り潰し）。
  実機のユニットテストでも再現（tests 側の events.jsonl ポーリング読みと衝突）。
- 対応: 共有違反（一時的 IOException）を 10ms〜500ms のバックオフで最大 12 回再試行し、
  **seq は書き込み成功時にのみ進める**。永続失敗は従来どおり例外伝播（Coordinator が fault 扱い）。
  D 側が録画中に Review UI で events.jsonl を読む場合の Event 欠落抑止にもなる。

### 実機検証

- テスト追加 5 件（保留クリックより後のキーの順序保証 / 保留未確定のまま Stop しても stopped より前 /
  保留クリックより古い Event は待たない / 処理中クリックと Pause の競合 / hook の保留照会 2 件）。
  EventCapture.Tests 53/53 PASS（6 連続実施）。sln 443 も PASS。
- テストは実フックを設置しない `InstallHooksForTest=false` で実行する
  （テスト実行中の実入力が queue へ流れ込み、UIA の重い処理で worker が滞留し
    タイミング依存になるため。フック単体の保留照会は別テストで検証）。

### 補足（D への返答）

- 追加観察の「synthetic F5 specialKey が events.jsonl に記録されない」は **仕様どおり**:
  F5 (VK 0x74) は契約 §11.2 の MVP specialKey 対象（Enter/Tab/Escape/Backspace/Delete/方向キー）
  にも可印刷キーにも含まれないため、フックが分類せず Event を生成しない。
  runtime smoke でキー記録を確認する場合は対象キーの変更（例: Enter）か、
  対象キー拡張を Shared Contract 変更（4 人合議）として別途議論する。
- 対応 1-3 はすべて B 担当モジュール内の変更で、Shared Contract（Phase 0 契約）は不変。
  D の targeted recheck を待つ。
