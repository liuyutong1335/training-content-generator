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
