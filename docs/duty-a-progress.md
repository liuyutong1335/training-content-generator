# 担当 A（Recording Engine）作業ログ・進捗

- 担当: Liu Yutong
- 担当領域: `src/TrainingContent.Capture/`（開発計画書 v0.2 §24）
- 作業ブランチ: `feature/capture`
- **2026-09-14: Spike A の録画・音声（Mic + System Audio）をユーザーが実機確認済み。Gate A 確認ツール（`spike/gate-a-check`）を同ブランチの PR で提供。**
- 本書の読み方: 同僚および同僚の AI は、A の実装状況を確認する際に本書を読む。契約事項は `docs/phase0-contract.md`（FROZEN）が優先。本書は進捗・知見・未決事項の記録。

---

## 1. 現在の進捗（2026-09-14 時点）

| Phase | 項目 | 状態 |
|---|---|---|
| Phase 0 | Contract Freeze（全員） | ✅ 完了（契約 v1.0 FROZEN・`docs/phase0-contract.md`） |
| Phase 1 | Spike A: Recording | 🔶 自動検証項目は合格・手動 Smoke Test 残存 |
| Phase 1 | Spike A: v6.6.0 vs v7.0.1 実機比較 | ⬜ 未実施（6.6.0 で骨格確定済み） |
| Phase 2 | Integrated Recording | ⬜（B の EventCapture との統合） |

### Gate A チェックリスト（開発計画書 §14）

| 項目 | 状態 | 備考 |
|---|---|---|
| デバイス列挙（Display / Mic / System Audio） | ✅ 実機確認済 | `IRecordingEngine.Get*()` 3 メソッド |
| 録画開始 / 停止 | ✅ 実機確認済 | 10 秒・60 秒テストで MP4 生成成功 |
| システム音声あり / マイクあり | ✅ **ユーザー確認済**（2026-09-14・無音問題をデバイス ID 解決方式で修正） | GateACheck で自動検証可 |
| Pause / Resume | ✅ 実機確認済 | 60 秒テストで Pause 2001ms を正しく論理時間から除外（契約 §5.2 準拠） |
| 10 分録画 | ⬜ 手動テスト待ち | `spike/gate-a-check` を `--full` モードで実施 |
| 複数アプリ切替 | ⬜ 手動テスト待ち | GateACheck シナリオ 3 の録画を再生して確認 |
| MP4 seek | ⬜ 手動テスト待ち | 生成物をプレーヤーでシークして確認（faststart は WARN・下記知見 7 参照） |
| 明確な音ズレなし | ⬜ 手動テスト待ち | 同上 |
| v6.6.0 vs v7.0.1 比較 | ⬜ 未実施 | csproj の PackageReference バージョンを差し替えて同一テストを実施 |

## 2. A の成果物（実装済み）

```
src/TrainingContent.Capture/
├─ IRecordingEngine.cs               録画エンジン抽象（§16）。State 遷移イベント付き
├─ RecordingOptions.cs               録画設定 + RecordingResult（論理 Duration は Pause 除外）
├─ ScreenRecorderRecordingEngine.cs  ScreenRecorderLib 6.6.0 実装（実 API 対応済み）
└─ TrainingContent.Capture.csproj    net8.0-windows / x64 必須（ScreenRecorderLib 制約）

spike/recording-spike/       CLI 実機検証ツール（引数: 出力先 [秒数] [nopause]）
spike/recording-spike-gui/   WPF 検証 GUI（デバイス選択・開始/一時停止/再開/停止）
spike/gate-a-check/          Gate A 自動判定ツール（MP4 解析・3 シナリオ + 手動確認プロンプト）
```

### B・C・D へのインターフェース（これが A→全体の受け渡し形）

```csharp
// A が返す RecordingResult（契約 §11 の RecordingInfo にそのまま入る）
new RecordingResult {
    FilePath  = "…/raw/recording.mp4",  // ScreenRecorderLib が音声をミックスした 1 本の MP4
    Duration  = TimeSpan,                // ★ Pause を除外した論理時間（契約 §5.2）
    StartedAtUtc = DateTimeOffset,       // Master Session Clock の起点
    PauseIntervals = [(start, end)],     // B の Timeline から除外する区間
};
```

- 動画は**画面 + ミックス音声の 1 本の MP4**（v6.6.0 の仕様）。独立トラックが必要なら契約変更手順（§27）で相談
- `StateChanged` イベントで状態遷移（Recording/Paused/Failed）を購読可能 → B の MasterClock 同期に使える

## 3. 実装時に確認した技術知見（他担当・AI 向け）

1. **ScreenRecorderLib は AnyCPU 非対応**。`$(Platform)` が x86/x64/Win32/ARM64 でないと targets チェックで失敗する → sln は **x64 プラットフォーム**で構成すること（`PlatformTarget` ではダメ）
2. **デバイス指定はデバイス ID 形式**。`GetSystemAudioDevices()` が返す `AudioDevice.DeviceName` は `{0.0.0.00000000}.{…}` 形式。これを `AudioOptions.AudioInputDevice / AudioOutputDevice` に渡す。**FriendlyName（表示名）を渡すと無音になる**（最初の実機テストで発生・修正済み）
3. `SourceOptions.RecordingSources` には `GetDisplays()` の実体（`RecordableDisplay`）を渡す。空の `DisplayRecordingSource()` は "No valid recording sources" エラーになる
4. `StartAsync` は**開始時に戻る**。録画完了の受取は `StopAsync` の戻り値または `OnRecordingComplete` で行う
5. マイクとシステム音声は**同一音声トラックにミックス**されて MP4 に収まる（§4 参照）
6. v0.1 期の Python Spike（`spike/python-recording/`）からの知見: pyaudiowpatch の blocking read は生 bytes / DPI 125% 環境で `GetSystemMetrics` が仮想化値を返す / モノラルマイクはチャンネル数を収めないと失敗
7. **IsMp4FastStartEnabled が v6.6.0 で効かない**（HW/SW エンコーダ両方で moov が末尾）。seek 自体は問題なく可能なため WARN 扱い。**v7.0.1 比較時の確認ポイント**
8. **WGC 初期化に ~2 秒かかる**ため、Canonical Timeline（0ms）は `RecorderStatus.Recording` になった瞬間に時計を合わせる（Record() 呼び出し時点で計測を始めると全タイムスタンプが ~2 秒ずれる）→ GateACheck で全シナリオ差 0.7s 以内を確認済み

## 4. テスト状況

- `dotnet test` **16/16 合格**（Core 契約テスト 13 + Capture 状態機械テスト 3）
- 実機テスト: 10 秒 / 60 秒録画成功（60 秒版は Pause 2001ms を含み、論理 58400ms を正しく返した）

## 5. 未決事項・相談

1. **Master Clock の帰属**（B と）— `docs/integration-notes.md` §1
2. **Screenshot サービスの帰属**（B・C と）— 同 §2
3. Gate A 手動 Smoke Test の実施（10 分録画・アプリ切替・seek・音ズレ）
4. v6.6.0 vs v7.0.1 実機比較（v7 はフリーズ/FPS 低下の Issue #374 あり）
5. 複数モニター環境での個別ディスプレイ録画指定（コード内 TODO(Spike A)）

## 6. 検証手順（再現する場合）

```powershell
# ビルド & テスト（.NET 8 SDK 必須）
dotnet build TrainingContentGenerator.sln
dotnet test TrainingContentGenerator.sln

# CLI 実機テスト（秒数省略で 10 秒 + Pause テスト）
spike\recording-spike\bin\x64\Debug\net8.0-windows\win-x64\RecordingSpike.exe test.mp4 60

# GUI 実機テスト（デバイス選択・開始/停止ボタン）
spike\recording-spike-gui\bin\x64\Debug\net8.0-windows\win-x64\RecordingSpikeGui.exe

# Gate A 確認（自動シナリオ + 手動確認プロンプト。10 分録画も含める場合）
spike\gate-a-check\bin\x64\Debug\net8.0-windows\win-x64\GateACheck.exe
spike\gate-a-check\bin\x64\Debug\net8.0-windows\win-x64\GateACheck.exe --full
```
