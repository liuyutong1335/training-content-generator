
---

# トレーニングコンテンツ生成ツール 開発計画書

**Version:** 0.2
**作成日:** 2026-09-14
**対象:** MVP 開発
**開発体制:** 4名
**対象OS:** Windows 10 / 11

---

## 1. プロジェクト概要

### 1.1 目的

デスクトップ上で実施した業務操作を記録し、その記録をもとに以下のトレーニングコンテンツを作成・管理できるデスクトップアプリケーションを開発する。

* トレーニング動画
* 操作マニュアル
* 記録済みコンテンツの管理

### 1.2 基本方針

本システムでは、動画とマニュアルを個別に生成するのではなく、録画・操作イベント・テキスト等を共通の `TrainingProject` として管理する。

```text
デスクトップ操作
      │
      ├─ 画面録画
      ├─ システム音声
      ├─ マイク音声
      ├─ マウス操作
      ├─ キーボード操作
      └─ 入力テキスト
             │
             ▼
       TrainingProject
       ├─ Recording
       ├─ Timeline
       ├─ Events
       ├─ Steps
       └─ Assets
             │
       ┌─────┴─────┐
       ▼           ▼
 Training Video   Manual
       │           │
       └─────┬─────┘
             ▼
      Content Manager
```

操作マニュアルについては、完成済み動画を再解析する方式に限定せず、動画生成時に保持している録画情報・操作イベント・テキスト・画像を利用する。

---

# 2. 要件定義

## 2.1 必須要件

| ID   | 要件                 | MVP対応 |
| ---- | ------------------ | ----- |
| R-01 | 画面録画               | ○     |
| R-02 | システム音声録画           | ○     |
| R-03 | マイク音声録画            | ○     |
| R-04 | テキスト入力             | ○     |
| R-05 | ①～③を利用したトレーニング動画生成 | ○     |
| R-06 | 操作マニュアル生成          | ○     |
| R-07 | 作成コンテンツの管理画面       | ○     |

### 画面録画対象

* ブラウザのみではなくデスクトップ全体
* Windows 上の任意アプリケーションを対象とする
* SAP GUI、Office、ブラウザ、エクスプローラー等を区別しない

### 音声録画対象

* システム音声
* マイク音声

両方を同時に録画可能とする。

---

## 2.2 あれば嬉しい機能

| ID   | 機能            | 対応予定     |
| ---- | ------------- | -------- |
| O-01 | 無操作・不要区間の自動短縮 | Post-MVP |
| O-02 | 録画音声の機械音声化    | Post-MVP |
| O-03 | 入力テキストの機械音声化  | Post-MVP |

---

## 2.3 MVP対象外

以下は MVP では実装しない。

* AIによる自動手順生成
* AIによる説明文生成
* 自動動画カット
* Whisper/STT
* TTS
* PDF/DOCX
* SAP専用自動化
* Playwright
* クラウド保存
* ユーザー認証
* 複数ユーザー管理
* 複雑な動画編集
* SQLite等のDB導入

---

# 3. 技術構成

| 領域               | 採用技術                   |
| ---------------- | ---------------------- |
| Language         | C#                     |
| Runtime          | .NET 8                 |
| Desktop UI       | WPF                    |
| Screen Recording | ScreenRecorderLib      |
| System Audio     | WASAPI Loopback        |
| Microphone       | WASAPI Capture         |
| Mouse Capture    | Win32 Low-Level Hook   |
| Keyboard Capture | Win32 Low-Level Hook   |
| UI情報取得           | Windows UI Automation  |
| Screenshot       | System.Drawing / Win32 |
| Data             | JSON / JSONL           |
| Manual           | Markdown / HTML        |
| Video            | MP4                    |
| Test             | xUnit / dotnet test    |
| Storage          | Local filesystem       |

---

# 4. OSS・GitHub活用方針

MVPでは、録画・操作キャプチャ等の基盤機能を一から実装せず、既存OSSを利用・参考実装として活用する。

## 4.1 ScreenRecorderLib

**Repository**

[https://github.com/sskodje/ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)

**用途**

* デスクトップ録画
* Windows Graphics Capture
* MP4/H.264録画
* システム音声
* マイク音声
* オーディオデバイス列挙
* ディスプレイ列挙

ScreenRecorderLib は `LoopbackAudioSource` と `CaptureAudioSource` を同時に録画ソースとして利用できるため、システム音声＋マイクという必須要件に直接対応できる。

デバイス列挙についても `GetSystemAudioCaptureDevices()`、`GetSystemAudioLoopbackDevices()`、`GetDisplays()` 等のAPIが提供されている。

**重点確認ファイル**

```text
TestConsoleAppDotNetCore/Program.cs
ScreenRecorderLib/AudioSources.h
ScreenRecorderLib/Recorder.h
ScreenRecorderLib/Options.h
```

**利用方式**

> NuGetライブラリとして直接利用

**注意事項**

最新 v7 系では録画フリーズ/FPS低下に関する報告があるため、開発開始時に v6.6.0 と v7.0.1 の実機比較を行う。関連 Issue #374 は現在も検証対象とする。

**License**

MIT License。

---

# 5. OpenSteps

**Repository**

[https://github.com/ebanez8/openstep](https://github.com/ebanez8/openstep)

**用途**

本プロジェクトに最も近い参考実装として利用する。

対象：

* Global Mouse Hook
* Global Keyboard Hook
* UI Automation
* UI Element特定
* Screenshot Capture
* Click Highlight
* Password入力検知
* Screenshot Redaction
* Step Model
* Session保存
* Markdown / HTML Export
* Step Editor

OpenSteps の `RecordedStep` は、クリック座標だけでなく Window、Process、AutomationId、ControlType、ElementBounds、入力情報、Screenshot 等を保持しているため、本システムの `TrainingStep` 設計の基礎として利用できる。

### 重点確認ファイル

**マウス操作**

```text
src/OpenSteps.Capture/GlobalMouseHook.cs
```

Windows Low-Level Hook による Click / RightClick / DoubleClick 判定を実装済み。

**キーボード操作**

```text
src/OpenSteps.Capture/GlobalKeyboardHook.cs
```

入力文字そのものを保存せず、TextEntry / SpecialKey / Shortcut として意味的に記録する方式を参考とする。

**UI Automation**

```text
src/OpenSteps.Capture/UiAutomationService.cs
```

以下を取得する。

```text
ElementName
AutomationId
ControlType
ClassName
ElementBounds
IsEditable
IsPassword
```

**Screenshot**

```text
src/OpenSteps.Capture/ScreenshotService.cs
```

以下を参考とする。

```text
Full Desktop
Active Window
Multi Monitor
DPI
Global → Local Coordinate
Click Highlight
Fallback
```

**Redaction / Annotation**

```text
src/OpenSteps.Capture/ScreenshotRedactionService.cs
```

参考機能：

```text
BlackBox
Pixelate
RedCircle
Rectangle
Highlight
Arrow
Marker
```

**Session Storage**

```text
src/OpenSteps.Core/Services/SessionStore.cs
```

Save / Load / List / Delete / Rename の設計を Content Manager に利用する。

**Manual Export**

```text
src/OpenSteps.Core/Services/MarkdownExporter.cs
src/OpenSteps.Core/Services/MarkdownBuilder.cs
```

Markdown / HTML / Screenshot export の基盤として参考・改造する。

**利用方式**

> 必要機能を選択して移植・改造する。

OpenSteps 全体を fork して本システムを構築する方式は採用しない。

**License**

MIT License。

---

# 6. NessStudio

**Repository**

[https://github.com/micilini/NessStudio](https://github.com/micilini/NessStudio)

**用途**

録画セッションと時間同期設計の参考とする。

直接ベースコードとして利用することは想定しない。

### 重点確認ファイル

```text
NessStudio/Recording/SessionManifest.cs

NessStudio/ViewModel/Helpers/RecordAssist.cs

NessStudio/ViewModel/Helpers/SystemLoopbackSession.cs
```

`SessionManifest` では Screen / Mic / System Audio を独立した Track として扱い、録画開始・終了、Duration、Offset、Pause Interval 等を管理している。

`RecordAssist` では master `Stopwatch` を利用して、録画全体の共通時間軸を管理している。

本システムでもこの考え方を採用し、

```text
Video
Mouse
Keyboard
Screenshot
Subtitle
Step
```

を同一 Timeline 上で管理する。

**利用方式**

> アーキテクチャ参考のみ

**License**

MIT License。

---

# 7. Microsoft Skill Recorder

**Repository**

[https://github.com/microsoft/skill-recorder](https://github.com/microsoft/skill-recorder)

**用途**

MVP後の高度化機能の主要参考実装とする。

対象：

* Event Timeline
* Key Frame生成
* Frame Deduplication
* Event / Frame correlation
* Whisper
* Sensitive Data Scan
* Workflow → Ordered Steps

Skill Recorder は録画データを直接一つの手順書として扱わず、イベントを `events.jsonl` に保持した後、分析 Pipeline で ordered steps に変換する構造を採用している。

この考え方を本プロジェクトの

```text
Raw Event
↓
Training Step
```

分離設計に利用する。

### 重点確認ファイル

**Frame Capture**

```text
electron/video/capture-preload.cjs
```

低頻度画像取得と dHash による重複除去を実装している。

**Frame Extraction**

```text
electron/frames/extractor.ts
```

Event timestamp から最も近い Frame を取得する設計を参考とする。

**Event Storage**

```text
electron/recorder/session-store.ts
common/types.ts
electron/pipeline.ts
```

**Windows Capture**

```text
docs/windows-capture.md
```

**Sensitive Data**

```text
electron/sensitive/scanner.ts
common/sensitive.ts
```

Token、Credential、Email、Phone等の検出・mask方式を Post-MVP の安全機能に利用する。

**Whisper**

```text
electron/narration/whisper.ts
electron/narration/transcribe.ts
```

録音音声の STT 化を追加する際の参考とする。

**利用方式**

> MVPでは直接依存しない。Post-MVP機能の参考実装とする。

**License**

MIT License。

---

# 8. OSS利用一覧

| Repository               | MVP | 用途                          | 利用方法       |
| ------------------------ | --: | --------------------------- | ---------- |
| ScreenRecorderLib        |   ○ | Video/System Audio/Mic      | NuGet依存    |
| FFmpeg（BtbN LGPL ビルド） |   ○ | Video 合成（字幕焼き込み・結合） | 外部プロセス起動 |
| OpenSteps                |   ○ | Hook/UIA/Screenshot/Manual  | 部分移植       |
| NessStudio               |   △ | Timeline/Session設計          | 参考         |
| Microsoft Skill Recorder |   × | Smart Frame/STT/AI/Security | Post-MVP参考 |

---

# 9. システムデータモデル

## 9.1 TrainingProject

```text
TrainingProject
├─ Id
├─ SchemaVersion
├─ Title
├─ Objective
├─ TargetAudience
├─ Prerequisites
├─ CreatedAt
├─ UpdatedAt
├─ Recording
├─ Steps[]
└─ Outputs
```

---

## 9.2 TimelineEvent

```text
TimelineEvent
├─ Id
├─ Seq
├─ TimestampMs
├─ Type
└─ Payload
```

MVP対象 Event Type：

```text
mouse.click
mouse.double_click
mouse.right_click

keyboard.text_entry
keyboard.special_key
keyboard.shortcut
```

---

## 9.3 TrainingStep

```text
TrainingStep
├─ Id
├─ Order
├─ StartMs
├─ EndMs
├─ Title
├─ Action
├─ Target
├─ Description
├─ Screenshot
├─ Caution
├─ ExpectedResult
└─ SourceEventIds[]
```

---

# 10. Event / Step 分離方針

Raw Event と Training Step は別データとして管理する。

例：

```text
Raw Events

10.100 Click textbox
10.500 Key
10.620 Key
10.800 Key
11.200 Tab
12.500 Click button
```

↓

```text
Training Steps

Step 1
社員番号を入力します。

Step 2
「登録」ボタンをクリックします。
```

Raw Event は原則変更しない。

Training Step は Review UI から編集可能とする。

---

# 11. 保存構造

```text
projects/
└─ <project-id>/
   ├─ project.json
   ├─ events.jsonl
   │
   ├─ raw/
   │  └─ recording.mp4
   │
   ├─ screenshots/
   │  ├─ original/
   │  └─ edited/
   │
   ├─ manual/
   │  ├─ manual.md
   │  └─ manual.html
   │
   └─ output/
      └─ training_video.mp4
```

### project.json

編集可能な Project / Step の現在状態を保存する。

### events.jsonl

録画中の Raw Event を append-only で保存する。

---

# 12. Solution構成

```text
TrainingContentGenerator/
│
├─ TrainingContentGenerator.sln
│
├─ src/
│  ├─ TrainingContent.App/
│  │  ├─ Views/
│  │  ├─ ViewModels/
│  │  └─ Components/
│  │
│  ├─ TrainingContent.Core/
│  │  ├─ Models/
│  │  ├─ Timeline/
│  │  └─ StepBuilder/
│  │
│  ├─ TrainingContent.Capture/
│  │  ├─ Recording/
│  │  ├─ Mouse/
│  │  ├─ Keyboard/
│  │  ├─ UiAutomation/
│  │  └─ Screenshot/
│  │
│  ├─ TrainingContent.Manual/
│  │  ├─ Markdown/
│  │  └─ Html/
│  │
│  ├─ TrainingContent.Video/
│  │  ├─ Timeline/
│  │  ├─ Overlay/
│  │  ├─ Subtitle/
│  │  └─ Renderer/
│  │
│  └─ TrainingContent.Storage/
│     ├─ ProjectStore.cs
│     └─ ContentIndex.cs
│
├─ tests/
│  ├─ Core.Tests/
│  ├─ Capture.Tests/
│  ├─ Manual.Tests/
│  ├─ Video.Tests/
│  └─ Integration.Tests/
│
├─ docs/
├─ samples/
└─ THIRD_PARTY_NOTICES.md
```

---

# 13. 開発フェーズ

## Phase 0 — Contract Freeze

全員参加。

決定対象：

```text
TrainingProject
TimelineEvent
TrainingStep

Project Directory
Event Type
Timestamp Rule
```

### 完了条件

* Model確定
* Directory確定
* Timestamp単位を `ms` に統一
* Breaking Changeルール確定

---

# 14. Phase 1 — Technical Spike

2本並行して実施する。

## Spike A：Recording

担当：A

検証：

```text
Full Desktop
System Audio
Microphone
Start
Pause
Resume
Stop
MP4
```

ScreenRecorderLib：

```text
v6.6.0
vs
v7.0.1
```

を実機比較する。

### Gate A

* 10分録画成功
* システム音声あり
* システム音声なし
* Mic + System Audio
* Pause / Resume
* アプリ切替
* MP4 seek
* 明確な音ズレなし

---

## Spike B：Operation Capture

担当：B

OpenSteps から以下を最小構成で検証する。

```text
GlobalMouseHook
GlobalKeyboardHook
UiAutomationService
ScreenshotService
```

### Gate B

1つの操作から以下が取得可能であること。

```text
Timestamp
Action
Process
Window
Element
ControlType
Coordinates
Screenshot
```

---

# 15. Phase 2 — Integrated Recording

録画と Event Capture を統合する。

```text
Start
 │
 ├─ RecordingEngine.Start
 ├─ MouseHook.Start
 ├─ KeyboardHook.Start
 └─ MasterClock.Start
```

停止時：

```text
Recording
events.jsonl
screenshots
project.json
```

を同一 Project に保存する。

---

# 16. Recording Engine抽象化

ScreenRecorderLib をUIやCoreから直接呼び出さない。

```csharp
public interface IRecordingEngine
{
    IReadOnlyList<DisplayDevice> GetDisplays();

    IReadOnlyList<AudioDevice> GetMicrophones();

    IReadOnlyList<AudioDevice> GetSystemAudioDevices();

    Task StartAsync(RecordingOptions options);

    Task PauseAsync();

    Task ResumeAsync();

    Task StopAsync();
}
```

実装：

```text
ScreenRecorderRecordingEngine
```

将来別Backendへ交換可能な構造とする。

---

# 17. Phase 3 — Step Builder

Input：

```text
events.jsonl
```

Output：

```text
TrainingStep[]
```

MVPでは AI を利用せず、ルールベースで変換する。

例：

```text
Click Edit
Key
Key
Key
Key
Tab
```

↓

```text
TextEntry
```

UI Automationで：

```text
Name = 登録
ControlType = Button
```

を取得できた場合：

```text
Action = click
Target = 登録
Title = 「登録」をクリック
```

を生成する。

---

# 18. Phase 4 — Review UI

主要画面：

```text
┌───────────────────────────────────────┐
│ Preview                               │
├─────────────────┬─────────────────────┤
│ Step List       │ Step Editor         │
│                 │                     │
│ 1 Login         │ Title               │
│ 2 Input         │ Description         │
│ 3 Register      │ Caution             │
│                 │ Expected Result     │
│                 │ Screenshot          │
└─────────────────┴─────────────────────┘
```

MVP編集：

* Step title
* Description
* Caution
* Expected Result
* Step delete
* Manual step add
* Reorder
* Screenshot replace
* Screenshot redaction

---

# 19. Phase 5 — Manual Generator

Input：

```text
TrainingProject.Steps
```

Output：

```text
manual.md
manual.html
```

構成：

```text
タイトル
学習目標
対象者
事前準備

操作手順

Step 1
Screenshot
Description
Caution
Expected Result

...

完了確認
```

Manual Generator は Raw Event を直接参照しない。

---

# 20. Phase 6 — Video Generator

Input：

```text
Raw Recording
+
TrainingProject.Steps
```

Output：

```text
training_video.mp4
```

MVP構成：

```text
Title Screen
↓
Recorded Desktop Video
↓
Step Title / Caption
↓
Subtitle
↓
Ending
```

Manual と Video の説明文は両方とも：

```text
TrainingStep.Description
```

を使用する。

別々に説明文を生成してはならない。

---

# 21. Phase 7 — Content Manager

MVPでは SQLite を使用せず、Project Directory を一覧化する。

表示項目：

| 項目            |
| ------------- |
| Thumbnail     |
| Title         |
| Updated At    |
| Duration      |
| Step Count    |
| Video Status  |
| Manual Status |

操作：

```text
Open
Rename
Duplicate
Delete
Generate
Export
```

---

# 22. Phase 8 — E2E / Hardening

最終 E2E：

```text
新規Project作成
↓
Display選択
↓
System Audio選択
↓
Mic選択
↓
録画開始
↓
5～8操作
↓
録画終了
↓
Step Review
↓
Description修正
↓
Screenshot Redaction
↓
保存
↓
アプリ終了
↓
再起動
↓
Project再読込
↓
Manual生成
↓
Video生成
↓
Output確認
```

---

# 23. MVP Definition of Done

| 項目                      | 必須 |
| ----------------------- | -: |
| 全デスクトップ録画               |  ○ |
| System Audio            |  ○ |
| Microphone              |  ○ |
| Mouse Event             |  ○ |
| Keyboard Semantic Event |  ○ |
| UI Automation           |  ○ |
| Screenshot              |  ○ |
| Event Timeline          |  ○ |
| Step生成                  |  ○ |
| Step編集                  |  ○ |
| Project保存               |  ○ |
| Project再読込              |  ○ |
| Markdown Manual         |  ○ |
| HTML Manual             |  ○ |
| MP4 Training Video      |  ○ |
| Content Manager         |  ○ |
| Screenshot Redaction    |  ○ |
| E2E成功                   |  ○ |

---

# 24. 4名の担当分担

| 担当 | 主領域                         | 参考Repository                   |
| -- | --------------------------- | ------------------------------ |
| A  | Recording Engine / Video Generator | ScreenRecorderLib / FFmpeg / NessStudio |
| B  | Event Capture / Timeline    | OpenSteps                      |
| C  | Step / Screenshot / Manual  | OpenSteps                      |
| D  | WPF / Project / Integration | OpenSteps SessionStore         |

### A — Recording / Video Generator

成果物：

```text
IRecordingEngine
ScreenRecorderRecordingEngine
Device Enumeration
Start/Pause/Resume/Stop
MP4
TrainingContent.Video（Timeline / Subtitle / Renderer）
Training Video（MP4・FFmpeg + ASS 字幕焼き込み）
```

### B — Event / Timeline

成果物：

```text
Mouse Hook
Keyboard Hook
UI Automation
Master Clock
events.jsonl
```

### C — Training Content

成果物：

```text
TrainingStep
StepBuilder
Screenshot
Redaction
Markdown
HTML
```

### D — App / Integration

成果物：

```text
WPF Shell
Project Create/Open
Recording UI
Review UI
Content Manager
ProjectStore
E2E
```

---

# 25. Shared領域

以下は単独担当者が独自変更しない。

```text
TrainingProject.cs
TimelineEvent.cs
TrainingStep.cs

Event Type
Timestamp rule
schema_version
Project directory structure
```

変更時：

```text
Proposal
↓
Team Review
↓
Merge
```

---

# 26. テスト計画

## Unit Test

対象：

```text
StepBuilder
Timeline
ProjectStore
Path handling
Markdown
HTML
Redaction
Schema
```

## Integration Test

対象：

```text
RecordingEngine
Capture + Timeline
Project Save/Load
Manual generation
Video generation
```

## Manual Smoke Test

最低限：

```text
10分録画
System Audioあり
System Audioなし
System + Mic
Pause/Resume
複数アプリ切替
日本語ファイル名
日本語タイトル
```

---

# 27. セキュリティ方針

MVP：

* 入力文字そのものを Keyboard Hook で保存しない
* Password UI を検知する
* Password入力を Step 情報として保存しない
* Screenshot の手動 Redaction を提供する
* Project はローカル保存
* テスト・デモでは機密情報を使用しない

Post-MVP：

* Token / API Key検出
* Email / Phone等のPII検出
* Screenshot OCRによる自動mask
* AI送信前Security Gate

---

# 28. OSS管理

Repository Root に以下を追加する。

```text
THIRD_PARTY_NOTICES.md
```

最低限記録：

```text
Repository
URL
License
Copyright
利用範囲
参考/コピーしたファイル
Commit SHA
改変有無
```

MIT License のコードをコピー・改変する場合も、著作権表示およびライセンス表示を保持する。ScreenRecorderLib、OpenSteps、NessStudio、Microsoft Skill Recorder は今回確認した時点ですべて MIT License。

---

# 29. Post-MVP Roadmap

| Version | 内容                                       |
| ------- | ---------------------------------------- |
| v0.2    | Recording Reliability / Recovery         |
| v0.3    | Smart Step / Event Aggregation           |
| v0.4    | Smart Frame / Auto Cut Candidate         |
| v0.5    | STT / TTS                                |
| v0.6    | AI Step Enrichment                       |
| v1.0    | Production-ready Training Authoring Tool |

### v0.4

Microsoft Skill Recorder の以下を参考：

```text
dHash
Hamming Distance
Frame Heartbeat
Event / Frame correlation
```

### v0.5

```text
Recorded Voice
↓
STT
↓
Text Review
↓
TTS
```

入力テキストについては：

```text
Text
↓
TTS
```

を先行実装可能とする。

---

# 30. 開発Gate

## G1 — Capture Gate

```text
Desktop + System Audio + Mic
```

を安定して録画できること。

## G2 — Timeline Gate

```text
Video
Mouse
Keyboard
Screenshot
```

が同一時間軸で対応すること。

## G3 — Training Data Gate

Raw Event から編集可能な TrainingStep を生成し、保存・再読込できること。

## G4 — Output Gate

同一 `TrainingStep` から、

```text
Manual
Training Video
```

を生成し、内容が一致すること。

**G1～G4 がすべて PASS した時点を MVP 完了とする。**

---

## 組員向け GitHub 参照先まとめ

| 担当           | 最初に読むRepository                                                         | 優先ファイル                                                                         |
| ------------ | ----------------------------------------------------------------------- | ------------------------------------------------------------------------------ |
| **A**        | [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)       | `TestConsoleAppDotNetCore/Program.cs`, `AudioSources.h`                        |
| **A**        | [NessStudio](https://github.com/micilini/NessStudio)                    | `SessionManifest.cs`, `RecordAssist.cs`, `SystemLoopbackSession.cs`            |
| **B**        | [OpenSteps](https://github.com/ebanez8/openstep)                        | `GlobalMouseHook.cs`, `GlobalKeyboardHook.cs`, `UiAutomationService.cs`        |
| **C**        | [OpenSteps](https://github.com/ebanez8/openstep)                        | `ScreenshotService.cs`, `ScreenshotRedactionService.cs`, `MarkdownExporter.cs` |
| **D**        | [OpenSteps](https://github.com/ebanez8/openstep)                        | `SessionStore.cs`, `MainWindow.xaml.cs`, `SessionEditorWindow.xaml.cs`         |
| **Post-MVP** | [Microsoft Skill Recorder](https://github.com/microsoft/skill-recorder) | `capture-preload.cjs`, `extractor.ts`, `scanner.ts`, `whisper.ts`              |


