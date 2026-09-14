# 参考リポジトリ調査（「GitHub 抄作業リスト」）

- 作成日: 2026-09-14
- 用途: MVP 実装の設計参考。**fork して魔改するのではなく、設計・アルゴリズムの借用が基本**。
- ライセンス総論: MIT 素材のみ活用。**OBS (GPL-2.0) / ShareX (GPL-3.0) はコードをコピーしない**（読むだけなら可）。

## 担当別の優先研究リスト

| 担当 | 優先研究 | 目的 |
|------|---------|------|
| A: Capture | Python Screen Recorder Pro / PyAudioWPatch / NessStudio | 録画実装・システム音声・Session Clock |
| B: Timeline / Video | NessStudio（manifest / master clock / export）+ FFmpeg | トラック mix・字幕・MP4 |
| C: Manual | OpenSteps | Step 編集・赤紙（redaction）・MD/HTML 出力 |
| D: UI / Storage | OpenSteps（session 管理）+ NessStudio（project library） | 管理画面・SQLite |
| 全員（後続） | microsoft/skill-recorder | v0.6+ の AI Step Detection の設計参考。MVP では実装しない |

## 各リポジトリの使い方

### Python Screen Recorder Pro（MIT・検証少なめ → Spike 向け / 質の基準にしない）
- `recorder.py`: MSS の持続フレーム取得、FFmpeg subprocess の起動・raw フレーム供給・stdin 終了、pause/resume、segment 管理
- `audio.py`: WASAPI デバイス探索、loopback/mic の二系統、サンプルレート・チャンネル統一
- `ui.py`: A は start/stop の recorder 呼び出しとエラー伝播だけ見る（UI は D 担当）
- 8 commit 程度でコミュニティ検証が無いため「こう書くべき」ではない

### PyAudioWPatch（MIT・システム音声の本命）
- `get_default_wasapi_loopback()` と loopback デバイス列挙で「スピーカーで鳴っている音」を直接録音
- 実績ある落とし穴: callback マルチスレッドでの host error 報告 → **まず blocking stream read で検証してから複雑化する**
- 当プロジェクトの `capture/system_audio.py` はこの方式に統一済み（2026-09-14）

### NessStudio（MIT・新規・stars 少 → UI は写さずアーキテクチャを写す）
- 吸収する 2 原則:
  1. **原始トラックを分けて保存する**（screen/system/mic を 1 つの MP4 に混ぜない）。マイク不調でも画面を再録画せず済む。原声→TTS 置換も `mic.wav` の差し替えだけで可能
  2. **Master Session Clock**（`StartedAtUtc` 基準 + track ごとの `start_offset_ms`）。B が確実に mix できる前提
- 構成対応: `WgcScreenCapturePipe→screen.mkv` / `MicCaptureService→mic.wav` / `SystemLoopbackService→system.wav` / `SessionManifest.cs` → 当プロジェクトの `capture/` + `RecordingManifest`

### OpenSteps（MIT・early beta → 手動部分の設計答案）
- 研究対象ディレクトリ: `src/OpenSteps.Capture/`
  - `GlobalMouseHook.cs` / `GlobalKeyboardHook.cs` — click → 自動 Step Marker（MVP 後）
  - `DpiAwarenessService.cs` / `ScreenshotCoordinateMapper.cs` / `MonitorService.cs` — **マルチモニタ・DPI スケーリングでハイライト座標がずれる問題の解法**。技術債として登録済み
  - `UiAutomationService.cs` — x/y だけでなく Name/ControlType を取得。v0.6 AI Step Detection の入口
  - `ScreenshotRedactionService.cs` — BlackBox/Pixelate/図形/番号マーカー。**元画像を書き換えず新 PNG へ出力**（C 担当が参照）
- 注意: .NET 8 製。集成せず「設計 + 必要なら小塊移植」

### Microsoft Skill Recorder（MIT・v0.6+ の必読）
- record → screen change detection / low-rate frame extraction / window tracking / narration transcription → ordered steps
- MVP の録画基盤には使わない（重点が Skill/Automation 出力で、システム音声を扱わないため）

### Captura（MIT・開発停止）
- 実装は写さない。成熟した Windows Recorder の設計研究（cursor/click/keystroke/mix、ホットキー）用

## A→B の受け渡し契約（manifest.json）

A が B に渡すのは「完成 MP4」ではなく **RecordingSession（トラック参照 + clock + offset）**:

```json
{
  "recording_started_at": "2026-09-14T10:00:00Z",
  "duration_ms": 65321,
  "screen_resolution": "1920x1080",
  "fps": 30,
  "tracks": {
    "screen":       { "path": "raw/screen.mp4",       "start_offset_ms": 162 },
    "system_audio": { "path": "raw/system_audio.wav", "start_offset_ms": 228, "sample_rate": 48000, "channels": 2 },
    "microphone":   { "path": "raw/microphone.wav",   "start_offset_ms": 2106, "sample_rate": 48000, "channels": 1 }
  }
}
```

- `core/models.py` の `RecordingManifest` がこの契約の実体（Pydantic）。
- 自動短縮 (v0.3) / TTS (v0.4-0.5) / AI Step Detection (v0.6) を追加しても録画基盤の作り直しが不要になる形。

## A の MVP スコープ（確定）

- やる: 3 ソース同時録画 → `raw/{screen.mp4, system.wav, microphone.wav}` + `manifest.json` の安定化 ✅ 実機スパイク済
- やらない（他担当・後続）: mix・字幕（B）/ click capture・OpenSteps 統合（MVP 後）/ STT・TTS（v0.4+）
