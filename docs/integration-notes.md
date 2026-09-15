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

## 3. sln へのプロジェクト登録

- B の `TrainingContent.EventCapture` が `TrainingContentGenerator.sln` に未登録
- 案: マージ担当（D）が統合時に sln へ追加。x64 プラットフォーム必須（ScreenRecorderLib と同じ制約）

## 4. 音声はミックスで MP4 に収まる（全員向け情報）

- ScreenRecorderLib v6.6.0 は マイク(Input) + システム音声(Output) を**同一音声トラックにミックス**する
- 独立トラックで管理したい場合（NessStudio 方式）は raw 構成の変更（Breaking Change）になるため、必要なら早期に契約変更手順（§27）を使うこと
