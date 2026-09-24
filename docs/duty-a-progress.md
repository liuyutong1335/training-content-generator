# 担当 A（Recording Engine）作業ログ・進捗

- 担当: Liu Yutong
- 担当領域: `src/TrainingContent.Capture/`（開発計画書 v0.2 §24）+ `src/TrainingContent.Video/`（R-05・2026-09-16 にリーダー判断で A 担当に）
- 作業ブランチ: `feature/capture`
- **2026-09-14: ✅ GATE A 確定 — Spike A 完了。** 自動検証（10 分録画含む）全 PASS + 手動確認（音ズレ / seek / 各音声 / アプリ切替）もユーザーが確認済み。
- 本書の読み方: 同僚および同僚の AI は、A の実装状況を確認する際に本書を読む。契約事項は `docs/phase0-contract.md`（FROZEN）が優先。本書は進捗・知見・未決事項の記録。

---

## 1. 現在の進捗（2026-09-14 時点）

> 更新: **Gate A 完了** — 自動検証（10 分録画含む）+ 手動確認すべて PASS。**Phase 2（B の EventCapture との統合）着手**。最初の対応として B から要望のあった `CaptureStarted` イベントを実装（§7）。**2026-09-16: D の WPF UI（`feature/WPF-integration`）が `CaptureStarted` 同期手順を実装済みのことを確認 — A 側のコード変更は不要（§5-2）。**

| Phase | 項目 | 状態 |
|---|---|---|
| Phase 0 | Contract Freeze（全員） | ✅ 完了（契約 v1.0 FROZEN・`docs/phase0-contract.md`） |
| Phase 1 | Spike A: Recording | ✅ **GATE A PASS**（自動検証 + 手動確認完了） |
| Phase 1 | Spike A: v6.6.0 vs v7.0.1 実機比較 | ✅ 実施済（§3 知見 9・10 参照。**MVP は v6.6.0 推奨**） |
| Phase 2 | Integrated Recording | 🔶 進行中（B の EventCapture と統合。§5 参照） |

### Gate A チェックリスト（開発計画書 §14）

| 項目 | 状態 | 備考 |
|---|---|---|
| デバイス列挙（Display / Mic / System Audio） | ✅ 実機確認済 | `IRecordingEngine.Get*()` 3 メソッド |
| 録画開始 / 停止 | ✅ 実機確認済 | 10 秒・60 秒テストで MP4 生成成功 |
| システム音声あり / マイクあり | ✅ **ユーザー確認済**（2026-09-14・無音問題をデバイス ID 解決方式で修正） | GateACheck で自動検証可 |
| Pause / Resume | ✅ 実機確認済 | 60 秒テストで Pause 2001ms を正しく論理時間から除外（契約 §5.2 準拠） |
| 10 分録画 | ✅ **自動 PASS**（2026-09-14: 600.4s 実測 → 論理 595.7s・mp4 594.8s・差 0.9s） | `GateACheck --full` で再現可 |
| 複数アプリ切替 | ✅ **ユーザー確認済** | GateACheck シナリオ 3 で確認 |
| MP4 seek | ✅ **ユーザー確認済** | faststart は WARN だが seek 動作に問題なし（知見 7） |
| 明確な音ズレなし | ✅ **ユーザー確認済** | GateACheck の手動確認で回答 |
| v6.6.0 vs v7.0.1 比較 | ✅ 実施済 | 下記 §3 知見 9・10。**MVP は v6.6.0 を推奨** |

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
spike/capture-started-check/ CaptureStarted イベント実機検証（Phase 2・自動判定）
spike/integration-smoke/     A+B 統合スモークテスト（Phase 2・自動判定）
```

### B・C・D へのインターフェース（これが A→全体の受け渡し形）

```csharp
// A が返す RecordingResult（契約 §7 の RecordingInfo にそのまま入る）
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
8. **WGC 初期化に ~2 秒かかる**ため、Canonical Timeline（0ms）は `RecorderStatus.Recording` になった瞬間に時計を合わせる（Record() 呼び出し時点で計測を始めると全タイムスタンプが ~2 秒ずれる）→ GateACheck で全シナリオ差 0.9s 以内を確認済み
9. **v7.0.1 は音声系が Breaking Change**: `GetSystemAudioDevices(source)` 廃止（`GetSystemAudioCaptureDevices` / `GetSystemAudioLoopbackDevices` に分離）、`AudioInputDevice`/`AudioOutputDevice` 廃止 → `AudioSources` リスト（`CaptureAudioSource` / `LoopbackAudioSource` / `ProcessAudioSource`）に一本化。`OnAudioPacketRecorded` イベント追加（将来の音ズレ検証・STT に有用）。`IRecordingEngine` 抽象は影響なし（実装差し替えで吸収可能）
10. **v7.0.1 実録比較の結論**: 初回測定で「mp4 が論理時間より 3.2s 短い異常」に見えたが、**比較スクリプト側の測定ミス**（Pause 減算漏れ）で、修正後は v6.6.0 と同等（差 0.7s 以内）だった。faststart は v7 でも効かず（両バージョン共通の制約）。v7 は音声系 API の Breaking Change のみが実質的な移行コスト。→ **MVP は v6.6.0（実績あり・契約整合済み）を推奨、v7 への移行は低リスト**。検証ツール: `spike/v7-comparison/`
11. **CaptureStarted 前の Stop で Duration が巨大になるバグ（2026-09-16 修正・D 側レビューで指摘）**: `_clock` はエンジン構築時に StartNew されるため、WGC 初期化の ~2 秒窓内（および同一インスタンスの 2 回目 `StartAsync` 直後）に `StopAsync` すると `CanonicalDuration()` の起点が実撮影開始より前になり、実録時間より大幅に大きい値を返していた。**修正**: `BuildResult` で `_captureStarted == false` の場合は `Duration = TimeSpan.Zero`・`PauseIntervals = []` を返す。併せて CaptureStarted 時点で旧クロック領域の Pause 情報を破棄（WGC 窓内 Pause → Resume の区間が残るため）。統合アプリでは Coordinator が `!_captureReady` を fault 扱いするため影響なし・単独利用（ツール類）時のみ顕在化。ユニットテスト不可（完了イベントが実録依存のため）— 実機再現は `spike/capture-started-check/` で継続確認。※契約参照は §7（`StartedAtUtc`）
12. **要件カバレッジ監査（2026-09-16）への対応 — A の検証ツールの判定強化**: 監査 §07 SP 系の指摘に対し以下を修正した。
    - **NEW-3**（CONFIRMED）: `StartedAtUtc` が `StartAsync` 時刻のままで実撮影開始と ~1.5〜2.0s 離れていた → CaptureStarted 瞬間で上書きするよう修正（契約 §11 の「Master Session Clock の起点」どおり）
    - **SP-1**: `--full`（10 分録画）なしでは「GATE A: PASS」「完了条件を満たしました」を表示しないように変更（標準モードの全 PASS は「完了判定は行いません（--full で再実行）」と明示）
    - **SP-2**: Engine 論理 Duration と MP4 の一致判定に加え、**CaptureStarted → Stop 完了の実撮影経過**（Pause 分を除く wall 実測）と MP4 を突き合わせるチェックを追加（±1.5 秒）。両者が同方向にずれるだけでは検出できなかった原点ズレ系を拾う
    - **SP-3**: 音声はトラック存在確認のみだったため、ffmpeg（`tools/get-ffmpeg.ps1` で取得、無ければ WARN 扱いでスキップ）の `volumedetect` による無音検査（mean_volume > -50dB）を追加
    - **SP-4**: integration-smoke の events 検査を「type 2 種の存在確認」から、**seq 単調増加（§8.1）・started timestampMs=0（§20）・stopped が最終行（§20）**の検証に強化
    - **SP-5**: integration-smoke が MP4 を一度も開いていなかった問題 → `Mp4Inspector`（gate-a-check からソース共有）で MP4 を実際に解析し、「Duration が Engine 論理値と ±1s」「最終イベント時刻を覆う」を判定
    - **未解決の注記**: 原点（0ms 対応）の frame-level 検証は自動化できておらず、G2 の差 146ms の原因・安定性も未証明のまま（監査指摘どおり）。テストスイートの合計は **42/42（Core 13 + Capture 3 + Video 26・2026-09-18 現在）**。WPF ブランチ構成では 69/69（監査 §08 の記載どおり）。R-05（動画生成）は A 担当として FFmpeg + ASS 方式で spike 着手（`spike/video-compose-check/`）

## 4. テスト状況

- `dotnet test` **42/42 合格**（Core 契約テスト 13 + Capture 状態機械テスト 3 + Video 26・2026-09-18 実機再確認。初期 Spike 完了時点は 16/16 だった）
- 実機テスト: 10 秒 / 60 秒録画成功（60 秒版は Pause 2001ms を含み、論理 58400ms を正しく返した）

## 5. Phase 2 統合作業（2026-09-15 開始）

1. **`IRecordingEngine.CaptureStarted` イベントを追加**（B README「A との時間同期」への対応）
   - 実際の撮影開始瞬間（`OnStatusChanged` で `RecorderStatus.Recording` を初回検出し内部クロックを `Restart()` した同一点）に 1 回だけ発火
   - D は B README 推奨手順の `session.Start()` 契機を `StateChanged(Recording)` から `CaptureStarted` へ変更することで、Event 側 0ms と MP4 の 0 秒が一致する
   - `RebaseClockToNow()` による原点張り直しは原則不要になる（保険として残す）
   - Pause/Resume で `RecorderStatus.Recording` が再発火しても `_captureStarted` ガードにより 2 回目は発火しない
   - ✅ **実機検証済み（2026-09-15・`spike/capture-started-check/`）**: StateChanged(Recording) 215ms → CaptureStarted 1485ms（撮影開始まで 1.3 秒）・Pause/Resume 挟んで発火 1 回・論理 Duration は Pause 除外を確認。全 4 項目 PASS
2. **統合メモ §1（Master Clock の帰属）**: B の MasterClock を正とする案を受け入れを記載 — `docs/integration-notes.md` §1
   - 残タスク: 例会で B・D の合意を取り、D が Record UI に同期手順を実装する
   - ✅ **D 側の実装を確認（2026-09-16・`feature/WPF-integration`）**: D の `RecordingCoordinator` が B README 推奨手順どおり `CaptureStarted` 契機で `session.Start()` を実装済み（`session.Start()` 完了まで UI は「録画準備中」として区別）。実装レベルでは B の MasterClock 正とする案と整合。例会での正式合意のみ残す
3. ✅ **A+B 統合スモークテスト実機 PASS（2026-09-15・`spike/integration-smoke/`）**
   - B README 推奨手順（`CaptureStarted` 契機で `session.Start()`）を実際に実行
   - Engine 論理 5059ms vs Session 論理 4913ms（**差 146ms**・許容 ±500ms 内）
   - `events.jsonl` は契約 §20 どおり（`recording.started` timestampMs=0 / `recording.stopped` seq 4）
   - D が Record UI を実装する際は本ツールのコードをそのまま転用できる（手順は B README どおりで追加調整なし）。※当時の記載「sln は変更していない」は PR #5 時点のもの — その後 2026-09-17 の RC-2 対応（PR #12）で A も `PreparationCancelCheck` を sln に登録済み
4. **PR 作成（2026-09-15）**: `feature/capture` → `main` へ Phase 2 第一弾（`CaptureStarted` + 実機検証 2 本）を Pull Request した。マージまで本ブランチで統合検証ツールが使える。`sln` は変更していない（§3 どおり D の窓口）

## 6. 未決事項・相談

1. **Master Clock の帰属**（B と）— `docs/integration-notes.md` §1 — **実装レベルでは解決**（D の `feature/WPF-integration` が B README 手順どおり実装・§5-2 参照）。例会での正式合意を残すのみ
2. **Screenshot サービスの帰属**（B・C と）— 同 §2 — **解決（2026-09-18・例会なしのため実装整合の確認で確定。§2 追記参照）**
3. 複数モニター環境での個別ディスプレイ録画指定（コード内 TODO(Spike A)・単一モニター環境では未検証）
4. v7.0.1 への移行タイミング（音声パイプライン再設計時・知見 9・10）

## 7. 検証手順（再現する場合）

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

# CaptureStarted イベントの実機検証（約 10 秒・自動判定）
spike\capture-started-check\bin\x64\Debug\net8.0-windows\win-x64\CaptureStartedCheck.exe

# A+B 統合スモークテスト（Engine + OperationCaptureSession・約 12 秒・自動判定）
spike\integration-smoke\bin\x64\Debug\net8.0-windows\win-x64\IntegrationSmoke.exe
```

---

## 8. R-05（動画生成）作業記録（2026-09-16・担当変更で A に）

- **方式の決定**: FFmpeg + ASS 字幕焼き込み（`spike/video-compose-check/` で実証・全 4 項目 PASS）。
  監査 Blocker #1 が求めていた「C# での合成方式の選定」はこれで確定。エンコーダは libopenh264（LGPL ビルドに libx264 は無い）。
- **モジュール実装**（開発計画書 §12 の 4 領域のうち Timeline / Subtitle / Renderer を実装・Overlay は将来拡張）:
  - `Timeline/StepTimelineBuilder.cs` — TrainingStep[] → 表示区間（契約 §14: EndMs=null は次 Step 開始まで・最終は +4s・Duration にクランプ）
  - `Subtitle/AssSubtitleWriter.cs` — ASS 生成（pure logic・単体テスト 16 本）
  - `Renderer/FfmpegVideoRenderer.cs` — 契約 §20 構成（Title Screen → 字幕焼き込み録画 → Ending）の ffmpeg 実行
  - `IVideoComposer.cs` — D の ContentsView「再生成」用の窓口。出力は契約 §17 の output/training_video.mp4
- **実装上の知見**:
  - ASS の `Format:` 行は `Dialogue` の 10 フィールドと一致させること（5 フィールドで書くと余剰フィールドがテキストとして描画される。spike のフレーム目視で発見）
  - ass フィルタの引数に絶対パスを渡せない（ドライブ文字 `:` がフィルタオプション区切りに解析される）→ 作業ディレクトリを指定して相対パスで渡す
  - xunit の `Assert.DoesNotContain`（文字列）は**文化依存比較**で、ja 環境では全角 `｛` と半角 `{` が同値扱いされる → 波括弧エスケープの検証は ordinal 比較で行う
  - concat は音声パラメータ不一致（録画側 AAC と anullsrc）で壊れ得るため再エンコードで繋ぐ
- **テスト**: `dotnet test` 32/32 合格（Core 13 + Capture 3 + Video 16）。実機検証は `spike/video-compose-check/`（Title/Ending 込み 15 秒出力の ffprobe 実測 + フレーム画素差で字幕焼き込みを証明）
- **未解決**: O-01（無操作区間の自動短縮）は Post-MVP。TTS は v0.5 以降。sln 登録済み（Video / Video.Tests）。実装分は **PR #10**（PR #9 は spike のみ先行マージ）。main 取込み済み（2026-09-16）
  例会事項: README 構成図の owner 表記修正（Video を A に）・開発計画書 §24 への Video Generator 追記・§8 OSS 一覧への FFmpeg 追加 — **✅ 実施済（2026-09-18・THIRD_PARTY_NOTICES.md は §9 で対応済み）**
- **追記（2026-09-17・D 統合向け強化）**: 生成処理の進捗報告とキャンセル時の後始末を実装
  - `VideoCompositionOptions.Progress`（`IProgress<VideoCompositionProgress>`）— 段階（入力解析 / 字幕焼き込み / Title / Ending / 結合 / 検証）と全体進捗 0→100% を報告。ffmpeg `-progress pipe:1` を `-nostats` 付きで起動して `out_time_us` を解析（**区切りは `=`。`:` で切ると 1 行も解析できない** — 実機で発見）
  - キャンセル時は ffmpeg を `Kill(entireProcessTree: true)` で残さず終了させる（旧実装は CancellationToken が飛んでも ffmpeg が temp を握り続けた）
  - `FfmpegProgressParser`（pure logic・単体テスト 5 本）を新設。out_time_ms は ffmpeg の歴史的経緯でマイクロ秒値が出るため out_time_us を優先
  - Title / Ending カードに `\fad(300,300)` のフェードを追加
  - 検証: `dotnet test` Video 26/26 合格・`spike/video-compose-check` 全 5 項目 PASS（進捗報告 9 回が単調に 0→100% をカバー）



---

## 9. RC-2（Capture preparation 中の Cancel）検証記録（2026-09-17）

D 側からの A 側確認（RC-2: preparation 中の Cancel）への回答材料として、実機検証を実施した。
検証ツール: `spike/preparation-cancel-check/`（StartAsync 直後の StopAsync → 再利用録画の 2 セッション・自動判定・全 5 項目 PASS）。

- **判定 1（Q1/Q2: lifecycle）**: `StartAsync` は同期的に戻り、直後に `StopAsync` を呼んでも
  ハングせず即時完了する。D は「StartAsync 完了待ち → Cancel 発行」でよく、CaptureStarted 待ちは不要。
- **エンジン側修正**: preparation 中の停止では ScreenRecorderLib の `Stop()` を呼ばず
  `Recorder.Dispose()` で打ち切る実装に変更した（`ScreenRecorderRecordingEngine.StopAsync`）。
  初期化中の `Stop()` は MP4 シンクが正常に閉じないため。
- **判定 2（Q3: Stop semantics）**: `Duration = 0`（BuildResult の CaptureStarted 前経路）で
  preparation cancel を識別できる。既存実装のままで契約変更なし。
- **判定 3（Q4: temp MP4）**: preparation cancel の残留 MP4 は **0 バイト**（canonical recording ではない）。
  **既知の lib 制約**: この 0 バイトファイルのハンドルは ScreenRecorderLib 6.6.0 の内部リークにより
  プロセス終了まで解放されない（Recorder.Dispose でも解放不可。OneDrive/%TEMP% でも同様＝プロセス内リーク）。
  呼び出し側は削除を試みて失敗なら無視してよい（0 バイトかつ captureReady == false）。
- **判定 4（対照）**: 正常録画（CaptureStarted 後に Stop）の完了直後はファイルロックなし
  ＝ロック残留は preparation cancel 経路特有。
- **Q5/Q6（watchdog）**: 実装不要（案 B の明示 Cancel で恒久対処）とする回答を D へ提示。
  正常時でも preparation は WGC 初期化 ~2 秒＋機器列挙等で伸び得るため、固定 timeout は誤爆の恐れがある。
- **THIRD_PARTY_NOTICES.md**: ScreenRecorderLib の Copyright を package 同梱 LICENSE 原文どおり
  「Copyright (c) 2017 Sverre Skodje」に修正（旧記載「Ramin Kaviani」は誤り）。
  FFmpeg（BtbN LGPL ビルド・LGPL-3.0・外部プロセス起動・非同梱）のセクションを追加。
- **追記（同日・integration-smoke 再実行時の修正）**: 通常停止（CaptureStarted 後）の完了直後に
  `Recorder` を即解放しないよう戻した — lib の完了処理と解放が競合すると MP4 の終端書き込みが
  欠ける恐れがあるため（解放は次 StartAsync の先頭 / engine.Dispose() で実施。preparation cancel
  経路の即時破棄は維持）。integration-smoke は再実行 2 回とも全 4 項目 PASS
  （MP4 と Engine 論理 Duration の差 0.28s / 0.32s）。なお負荷が高い環境ではこの差が
  ~1.1s まで膨らみ ±1s 判定を超過する実機観測がある（2026-09-17 17:43 / 17:44 の 2 回）。
  判定の再現性確認の際は機器負荷に注意すること。
- **追記（2026-09-18・R-2 対策の staging 録画を実装）**: 総合監査（`docs/audit-2026-09-18.md` §4 指摘 3）と
  D 側確認（RC-2 により preparation 中 Stop が UI から到達可能になった）を受け、エンジンを変更。
  - lib へは canonical（`options.OutputFilePath`）でなく **staging パス**（同一ディレクトリ・同一拡張子・
    GUID 付き `<name>.staging-<guid><ext>`）を渡す。**撮影成功時（CaptureStarted 後の完了）のみ
    `File.Move(overwrite: true)` で canonical へ置換**し、`RecordingResult.FilePath` は常に canonical を返す
  - preparation cancel / 録画失敗時は staging の削除を試みるのみで **canonical を一切触らない** →
    再録画時に既存の正常な録画を壊さない。0 バイト残留（既知の lib ハンドルリーク）は staging 側に
    発生するため、canonical には現れない（次回 StartAsync は別名を使うため衛突しない）
  - Move 失敗時（完成 MP4 が視聴中でロックされる等）は録画データを失わないよう staging を残して
    失敗として完了させる（エラーメッセージに staging パスを含む）
  - 呼び出し側（D の Coordinator 等）の変更は不要。実機検証: preparation-cancel-check 全 5 項目 PASS
    （判定 3 は「canonical にファイルが生成されなかった」に改善）・capture-started-check 全 4 項目 PASS・
    integration-smoke 全 4 項目 PASS（差 7ms）。
    **監査残留リスク R-1 も同時に再検証済み** — 現行エンジン（staging + 完了後 Move）での
    通常停止直後の canonical MP4 はロックなし（preparation-cancel-check 判定 5）

---

## 10. 監査 Minor 対応（2026-09-24 開始）

総合監査（`docs/audit-2026-09-18.md` §5「対応分担 A（Liu）」）の Minor 項目に着手。ブランチ: `fix/audit-minor-m7-comments`（以降の Minor 対応もこのブランチで継続）。

- **m-7 ✅（2026-09-24）**: 契約参照コメントの節番号を修正（6 箇所）。
  - `§11` → `§7`（RecordingInfo Contract）: `IRecordingEngine.cs` StopAsync / `ScreenRecorderRecordingEngine.cs` CaptureStarted 経由の `_startedAtUtc` 上書き箇所
  - `契約 §20` → `開発計画書 §20`（Phase 6 — Video Generator の MVP 構成）: `IVideoComposer.cs` ComposeAsync / `FfmpegVideoRenderer.cs` 2 箇所 / `AssSubtitleWriter.cs` WriteCard
  - `IVideoComposer.cs:30` の「契約 §0-6」は契約 §0 の第 6 条（Manual / Video は同一 TrainingProject を入力）を指す正しい参照と確認済みのため修正せず
  - B 領域（Core / EventCapture）の `§11`（keyboard payload）・`§20`（events.jsonl 例）参照は正しいため対象外
  - 確認: `dotnet build` 成功（0 警告 0 エラー）。コメントのみの変更のためテスト影響なし
- **m-4 ✅（2026-09-24）**: 音声なし録画（契約 §7 で正当）と Title / Ending（anullsrc 付き AAC）の concat 不一致 guard。
  - ffprobe で録画の音声ストリーム有無を探测し、無音声の場合は anullsrc（stereo / 44100Hz・カードと同一仕様）+ AAC で焼き込む
  - 判定ロジックを `AudioStreamArgs`（pure logic・単体テスト 2 本）に分離
  - `spike/video-compose-check` に音声なしシナリオ（項目 6/7）を追加・実機全 7 項目 PASS
- **m-5 ✅（2026-09-24）**: `Project.Recording == null` で字幕 0 件の動画が「成功」として返る問題。
  - 契約 §6.2 で Recording は Optional だが、Video の入力は契約 §28 どおり Recording + TrainingProject
  - `CompositionInputGuard.EnsureRecordingPresent`（pure logic・単体テスト 3 本）を新設し、ComposeAsync 冒頭で拒否
- **m-1 ✅（2026-09-24）**: 準備中（CaptureStarted 前）Pause → Resume の到達性確認と NRE 耐性。
  - **到達性（監査の未検証事項を実機で回答）**: D の統合 UI（`RecordingView.xaml.cs:144-146`）は準備中の Pause を不可効化
    （「準備中に Pause すると A/B の Canonical Timeline が壊れるため」）→ 統合アプリでは到達しない。
    エンジン単独利用（spike ツール等）では到達し得る
  - **lib の status 遷移（実機）**: 初期化中の `Pause()` は握り潰されず失敗もしない。
    CaptureStarted は通常どおり発火し、撮影は継続。準備中 Pause は知見 11 の旧クロック領域破棄により
    Duration に含まれない（Resume 後 ~2 秒録画 → Duration 2.4 秒・PauseIntervals 0 件）。
    検証: `spike/preparation-cancel-check` に Session 3（準備中 Pause → Resume → 録画）を追加・実機全 7 項目 PASS
  - **エンジン修正**: `ResumeAsync` の `_pauseStartedAt!.Value` を null 安全化（CaptureStarted 時に
    Pause 情報が破棄済みでも NRE しない。State は Paused を保持し、Resume で lib.Resume() をそのまま呼ぶ）

## 11. Recording Finalization Transaction の境界（2026-09-24・D 相談への回答）

D 側の「MP4 確定を transaction 最後に制御したい」要望に対し、two-phase finalize を
`DeferredCommit` opt-in flag（既定 false・既存動作不変）で実装した。詳細・3 案比較・
D 側の使い方は **統合メモ §7** を参照（integration-notes.md）。

- `RecordingOptions.DeferredCommit` / `RecordingResult.PendingCommit` / `PendingCommitPath` を新設
- `IRecordingEngine.CommitPendingRecording()` / `AbortPendingRecording()` を新設
  （Duration 等は stop 完了時点で固定 — Commit をいつ呼んでも同じ値。clock は stop 後も進むため）
- Dispose は確定待ち staging を削除しない・確定待ちありの再 StartAsync は拒否
- 実機検証: preparation-cancel-check に Session 4/5 を追加・全 10 項目 PASS。
  `dotnet test` 443/443（Capture 5 → 7）
