# Spike B — Operation Capture（担当 B）

Phase 1 Technical Spike B: ユーザー操作をグローバルフックで取得し、
Phase 0 契約 v1.0 準拠の `TimelineEvent`（events.jsonl）を出力できることを検証する。

## 実行方法

リポジトリルートで:

```bash
dotnet run --project spike/operation-capture
```

操作フロー:

1. コンソールで `[Enter]` を押すと録画開始
2. 好きなデスクトップアプリを操作する（クリック・入力など）
3. コンソールに戻り `P`（一時停止）/ `R`（再開）/ `S`（停止・終了）
4. 終了後、`projects/<project-guid>/events.jsonl` と `screenshots/original/` に出力

※ このコンソール自体の操作は自プロセス判定により記録対象外。

## Gate B の確認項目

1 回のマウスクリック（`mouse.click` 1 行）に以下が揃っていること:

| 項目 | 取得元 |
|---|---|
| Timestamp | MasterClock（録画開始 = 0 ms、Pause 除外） |
| Action | GlobalMouseHook の分類（click / doubleClick / rightClick） |
| Process | WindowFromPoint → GetWindowThreadProcessId |
| Window | 同上（トップレベルウィンドウのタイトル） |
| Element | UiAutomationService（AutomationId 等） |
| ControlType | 同上 |
| Coordinates | フックの MSLLHOOKSTRUCT（仮想スクリーン座標） |
| Screenshot | ScreenshotCapture（クリックハイライト付き PNG） |

## 実装メモ

- **Hook コールバックをブロックしない**: UIA・スクリーンショットは時間がかかるため、
  フックスレッドはイベントをキューに入れるだけ。処理は専用ワーカースレッドで行う。
  （Low-Level Hook は応答が遅いと Windows に外されるため）
- **Low-Level Hook にはメッセージループが必要**: LL Hook を設置したスレッドが
  メッセージループを回さないとコールバックが一切呼ばれない。
  コンソールアプリにはループがないため、フック専用 STA スレッドで
  `Application.Run()` を回し、停止時は `Application.Exit()` で抜ける。
  （初回実行時に「イベントが 2 件しか出ない」不具合として顕在化した）
- **Pause 境界の後追いイベントを破棄**: `P` 押下のキーダウン自体は pause 処理より
  数 ms 先にフックされるため、Pause 時点の Canonical 時刻を境界として、
  それより前に発生した後追いイベントは破棄する（タイムスタンプ逆転防止）。
- **keyboard.textEntry の集約**: 連続するテキスト入力キーをバーストとして 1 Event にまとめ、
  `keyCount` を設定する（契約 §11.1 / §20 の例に合わせる形）。
  区切りは specialKey / shortcut / click / 2 秒以上の空白 / pause / stop。
- **Password 保護（契約 §11.1）**: バースト先頭のフォーカス要素が `IsPassword` の場合、
  `keyCount = null` かつ `isSensitive = true` で出力する。実入力文字はそもそも取得しない。
  ※ パスワード判定はバースト先頭のみ（スパイクの簡略化。本実装では要検討）
- **UIA 失敗時の容錯（契約 §10）**: `uiElement` は `null` を許容し、Event 自体は破棄しない。
  スクリーンショット失敗も同様（`screenshotPath = null`）。
- **Pause 中の操作は破棄**: Pause は Canonical Timeline の外側であり、
  Pause 中の Event の timestamp が定義できないため。
  既知のクセ: コンソールの `P` 押下そのものは pause 処理前にフックされるが、
  自プロセス判定で除外されるため実害なし。
- **events.jsonl は 1 Event = 1 行**、camelCase、pretty print なし（契約 §21）。
- **終了時にセルフチェック**: seq 単調増加 / timestampMs >= 0 / 既知 type のみ /
  絶対パス・`\` 不在（契約 §29 / §18 / §9 / §26）を検証して表示する。

## OpenSteps からの移植

移植元と改変内容はリポジトリルートの `THIRD_PARTY_NOTICES.md` を参照。
OpenSteps のコードのうち本スパイクが実際に使うのは Mouse/Keyboard Hook と
UiAutomationService で、MasterClock / EventWriter / WindowInfoService / ScreenshotCapture
は Phase 0 契約に合わせた本プロジェクト独自の実装（OpenSteps は構成の参考のみ）。

## 未解決（チーム要相談）

- `keyboard.textEntry` をバーストで 1 Event にまとめた場合、TrainingStep の `EndMs`
  （継続操作の終端）を StepBuilder（担当 C）がどう導くか。Raw Event 単位の区切り方と
  合わせて C と協議する（契約 §14 / §15 関連）。
- 自プロセス除外の判定を「クリック座標のウィンドウの PID」で行っているが、
  コンソールを Windows Terminal 上で動かすとウィンドウの PID が Terminal 側になるため
  除外できず、コンソール操作（P/R/S キー等）が記録されることがある。
  本実装では Record UI が WPF（自プロセスのウィンドウ）になるため
  この判定は機能する想定。タスクバー等シェルウィンドウも PID 解決が
  失敗し `processName = null`（契約 §10 で許容）になる。
