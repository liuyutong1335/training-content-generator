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
| `WindowInfo/WindowInfoService.cs` | プロセス名・ウィンドウタイトル | OpenSteps 参考で最小化 |
| `Screenshot/ScreenshotCapture.cs` | デスクトップ撮影 + クリックハイライト | OpenSteps 参考で最小化 |
| `OperationCaptureSession.cs` | 上記の統合・スレッド制御・自プロセス除外 | 独自実装 |

移植記録（参照 Commit・改変内容）はリポジトリルートの `THIRD_PARTY_NOTICES.md` を参照。

## 実装上の要点

- **LL Hook にはメッセージループが必要**: フック設置 + `Application.Run()` を STA 専用スレッドで
  回す。フックスレッドをブロックすると Windows にフックを外されるため、重い処理（UIA・撮影）は
  ワーカースレッドへ分離。
- **keyboard.textEntry の集約**: 連続するテキストキーをバーストで 1 Event にまとめ `keyCount`
  を設定（契約 §11.1 / §20）。Password 判定バーストは `keyCount = null` + `isSensitive = true`。
  実入力文字はそもそも取得しない（契約 §11.1 / §27）。
- **UIA 失敗時も Event を破棄しない**（契約 §10）。撮影失敗も同様。
- **Pause 中の操作・Pause 直前の後追いイベントは破棄**（Canonical Timeline の外側、契約 §5.2）。

## 検証

```bash
dotnet test tests/TrainingContent.EventCapture.Tests
```

`MasterClock`（Pause 論理）と `EventTimelineWriter`（出力形式）の単体テスト。
実機での操作取得検証は `spike/operation-capture/`（Spike B）で完了済み — Gate B 8 項目 PASS。

## 担当 D への依頼

- `TrainingContentGenerator.sln` への本プロジェクト追加は統合担当の窓口でお願いする
  （競合回避のため B 側では sln を変更していない）。
- Record UI は自プロセスの WPF ウィンドウになるため、自プロセス除外判定は既定のままで機能する。
