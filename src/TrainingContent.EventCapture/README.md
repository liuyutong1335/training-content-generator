# TrainingContent.EventCapture（担当 B: Event Capture / Timeline）

Phase 0 契約 v1.0（`docs/phase0-contract.md`）に準拠してユーザー操作を取得し、
`TimelineEvent`（events.jsonl）を TrainingProject ディレクトリへ書き出すモジュール。

成果物（開発計画 §24 B）:

```text
Mouse Hook / Keyboard Hook / UI Automation / Master Clock / events.jsonl
```

## 担当 D からの使い方

```csharp
using TrainingContent.EventCapture;

using var session = new OperationCaptureSession(projectDirectory);
session.Start();      // フック設置 + recording.started
// ... ユーザーがデスクトップを操作 ...
session.Pause();      // Pause 中の実時間は Canonical Timeline に含まれない
session.Resume();
long durationMs = session.Stop(); // recording.stopped、論理時間を返す
```

出力（契約 §17 準拠）:

```text
<projectDirectory>/
├─ events.jsonl                     TimelineEvent（1 Event = 1 Line）
└─ screenshots/original/event-*.png 各クリックの代表フレーム
```

## 構成

| ファイル | 内容 | 出自 |
|---|---|---|
| `Hooks/GlobalMouseHook.cs` | LL マウスフック（click / doubleClick / rightClick 判定） | OpenSteps 部分移植 |
| `Hooks/GlobalKeyboardHook.cs` | LL キーボードフック（Text / SpecialKey / Shortcut の意味分類、実文字は取得しない） | OpenSteps 部分移植 |
| `UiAutomation/UiAutomationService.cs` | クリック座標・フォーカスの UI 要素取得 | OpenSteps 部分移植 |
| `MasterClock.cs` | Canonical Timeline（0ms 始点 / Pause 除外） | 独自実装 |
| `EventTimelineWriter.cs` | events.jsonl（1 行 1 Event / camelCase / seq 単調増加） | 独自実装 |
| `TextEntryAggregator.cs` | textEntry バースト集約 + Password 保護 | 独自実装 |
| `WindowInfo/WindowInfoService.cs` | プロセス名・ウィンドウタイトル | OpenSteps 参考で最小化 |
| `Screenshot/ScreenshotCapture.cs` | デスクトップ撮影 + クリックハイライト（Per-Monitor DPI 対応） | OpenSteps 参考で最小化 |
| `OperationCaptureSession.cs` | 上記の統合・スレッド制御・自プロセス除外 | 独自実装 |

移植記録（参照 Commit・改変内容）はリポジトリルートの `THIRD_PARTY_NOTICES.md` を参照。

## 実装上の要点

- **LL Hook にはメッセージループが必要**: フック設置 + `Application.Run()` を STA 専用スレッドで
  回す。フックスレッドをブロックすると Windows にフックを外されるため、重い処理（UIA・撮影）は
  ワーカースレッドへ分離。
- **keyboard.textEntry の集約**: 連続するテキストキーをバーストで 1 Event にまとめ `keyCount`
  を設定（契約 §11.1 / §20）。集約ロジックは `TextEntryAggregator` に独立させて単体テスト可能。
- **Password 保護は毎キー判定**（契約 §11.1 / §27）: バースト内に 1 つでもパスワード欄のキーが
  含まれれば `keyCount = null` + `isSensitive = true`（バースト途中でパスワ欄へ移った場合も
  文字数を保存しない）。実入力文字はそもそも取得しない。
- **日本語 IME 対応**: IME 変換中のキーは `VK_PROCESSKEY` (0xE5) に置き換わるため、
  これを Text 入力として分類する（対応しないと日本語入力が一切記録されない）。
- **DPI 対応**: 撮影スレッドのみ `SetThreadDpiAwarenessContext(Per-Monitor V2)` に切り替え、
  125%/150% 等の表示スケール環境でもフック座標（常に物理ピクセル）と同じ座標系で撮影する。
- **UIA 失敗時も Event を破棄しない**（契約 §10）。撮影失敗も同様。
- **Pause 中の操作・Pause 直前の後追いイベントは破棄**（Canonical Timeline の外側、契約 §5.2）。

## 検証

```bash
dotnet test tests/TrainingContent.EventCapture.Tests
```

`MasterClock`（Pause 論理・原点張り直し）、`EventTimelineWriter`（出力形式）、
`TextEntryAggregator`（バースト集約 + Password 保護）の単体テスト。
実機での操作取得検証は `spike/operation-capture/`（Spike B）で完了済み — Gate B 8 項目 PASS。

## A（IRecordingEngine）との時間同期 — 統合時の最重要ポイント

A の Engine は `StartAsync()` 呼び出しから**実際の撮影開始まで ~2 秒かかり**（WGC 初期化、
duty-a-progress §3-8）、A の内部クロックは撮影開始瞬間に `Restart()` される。
一方 `StateChanged(Recording)` は `StartAsync()` 呼び出し時点で発火するため、
**このイベントを待って Session を開始すると Event 側の 0ms が MP4 より ~2 秒早くなり、
字幕・Step 画像がすべて 2 秒ずれる**。

推奨手順（担当 D が Record UI で実装する形）:

```csharp
// 1. A の撮影が実際に始まった瞬間まで Session を開始しない
engine.StateChanged += (s, e) =>
{
    if (e.State == RecordingState.Recording && !sessionStarted)
    {
        // ※ 要 A 側の対応: 撮影開始瞬間（OnStatusChanged で _captureStarted が立つ箇所）で
        //    もう一度 StateChanged(Recording) を発火、または専用イベントを追加してもらう。
        //    現状の実装では StartAsync 直後に 1 回しか発火しないため、A に依頼中。
        session = new OperationCaptureSession(projectDir);
        session.Start(); // ここが Canonical 0ms == MP4 の 0 秒になる
        sessionStarted = true;
    }
};
await engine.StartAsync(options);

// 2. 以降の Pause/Resume は engine と session の両方へ（UI から同一契機で呼ぶ）
// 3. 停止: var durationMs = session.Stop(); await engine.StopAsync();
//    両者の論理時間が一致することを統合テストで確認する
```

代替策: 録画開始順を制御できない場合、撮影開始の瞬間が分かった時点で
`session.RebaseClockToNow()` を呼べば Event 側の原点を張り直せる（単体テスト済み）。
**A へのお願い**: 撮影開始を検出した瞬間に再度 `StateChanged(Recording)` を発火するか、
専用の `CaptureStarted` イベントの追加（非破壊的変更）を `docs/integration-notes.md` §1 で提案する。

## 担当 D への依頼

- `TrainingContentGenerator.sln` への本プロジェクト追加は統合担当の窓口でお願いする
  （競合回避のため B 側では sln を変更していない）。x64 プラットフォーム必須。
- Record UI は自プロセスの WPF ウィンドウになるため、自プロセス除外判定は既定のままで機能する。
- 上記「A との時間同期」の手順を Record UI に実装すること。
