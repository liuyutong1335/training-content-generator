# Training Content Generator — Phase 0 共通データ契約定義

**Document Version:** 1.0
**Contract Version:** 1
**Status:** FROZEN for MVP development
**Target:** Windows 10 / 11
**Runtime:** .NET 8
**UI:** WPF
**Purpose:** チーム共通仕様・AI参照用

---

## 0. この文書の扱い

本書は、MVP開発における **Phase 0 の共通データ契約（Source of Truth）** である。

チームメンバーおよび各担当AIは、実装・設計・レビュー時に以下を遵守すること。

1. 本書に定義された `TrainingProject` / `TimelineEvent` / `TrainingStep` / Timestamp / Directory / Path / Schema の意味を独自変更しない。
2. 本書と各担当実装が矛盾する場合、本書を優先する。
3. 未定義事項を勝手に必須仕様として追加しない。
4. Breaking Change が必要な場合、実装前にチームレビューを行い、本書を更新する。
5. Raw Event と Training Step を混同しない。
6. Manual / Video は同一 `TrainingProject` を入力とし、別々の説明文を生成しない。
7. Password 等の実入力文字を保存しない。

---

# 1. Phase 0 目的

Phase 0 では機能開発を行わず、後続の並行開発に必要な共通契約を確定する。

## 1.1 完了条件

| ID | 決定事項 | 状態 |
|---|---|---|
| P0-01 | TrainingProject 定義 | FIXED |
| P0-02 | TimelineEvent 定義 | FIXED |
| P0-03 | TrainingStep 定義 | FIXED |
| P0-04 | Event Type | FIXED |
| P0-05 | Timestamp Rule | FIXED |
| P0-06 | Project Directory | FIXED |
| P0-07 | ID Rule | FIXED |
| P0-08 | JSON Serialization Rule | FIXED |
| P0-09 | Schema Version Rule | FIXED |
| P0-10 | Breaking Change Rule | FIXED |

**Phase 0 判定:** PASS

---

# 2. システム基本原則

システム内部は以下の3層を明確に分離する。

```text
Raw Recording
     +
Timeline Events
        │
        ▼
  Training Steps
        │
        ▼
Canonical TrainingProject
       │
   ┌───┴───┐
   ▼       ▼
 Manual   Video
```

## 2.1 Recording

実際に取得した画面・システム音声・マイク音声。

## 2.2 TimelineEvent

録画中に実際に発生した操作事実。

- Raw Event として扱う。
- 原則として記録後に編集しない。
- `events.jsonl` に append-only で保存する。

## 2.3 TrainingStep

TimelineEvent から生成された教材用の手順データ。

- Review UI で編集可能。
- Manual / Video の共通入力となる。
- Raw Event とは別物。

```text
TimelineEvent != TrainingStep
```

---

# 3. Source of Truth

| 対象 | Source of Truth |
|---|---|
| 原始媒体 | `raw/recording.mp4` |
| 操作事実 | `events.jsonl` |
| 教材として確定した内容 | `project.json` 内 `TrainingProject` |
| 操作手順 | `TrainingProject.Steps` |
| Manual | `TrainingProject` から生成 |
| Training Video | Recording + `TrainingProject` から生成 |

禁止事項:

```text
Manual
↓
完成済みVideoを再解析して独自のStepを作る
```

```text
Video Generator
↓
Manualとは別のDescriptionを独自生成する
```

---

# 4. ID規約

すべての永続オブジェクトは GUID を使用する。

| 対象 | 型 | 変更可否 |
|---|---|---|
| Project | GUID string | 不可 |
| Event | GUID string | 不可 |
| Step | GUID string | 不可 |

JSON例:

```json
{
  "id": "ac01e763-f98b-4458-bb63-68de71252121"
}
```

`TrainingStep.Order` は表示順でありIDではない。Stepを並べ替えても `Step.Id` は変更しない。

---

# 5. Timestamp規約

## 5.1 Canonical Timeline

全モジュールの同期基準は `timestampMs` とする。

```text
field : timestampMs
type  : long
unit  : milliseconds
origin: recording start = 0 ms
```

### 強制ルール

- 録画開始 = `0 ms`
- 単位 = millisecond
- `timestampMs >= 0`
- Video / Event / Step / Screenshot / Subtitle は同一論理時間軸を使用する
- UTC時刻を同期用途に使用しない

## 5.2 Pause

Pause 中の実時間は Canonical Timeline に含めない。

例:

```text
Real Time

00:00 Start
00:20 Pause
00:50 Resume
01:10 Stop
```

実経過時間: 70 sec
Canonical Timeline: 40 sec

```text
0 ─────────20────────────40 sec
           ↑
      Pause時間は除外
```

最終 Recording の論理 `durationMs` は約 `40000`。

## 5.3 同一 Timestamp

複数Eventが同一 `timestampMs` を持つことを許可する。順序は `seq` で保証する。

```text
timestampMs = 時間位置
seq         = 発生順
```

## 5.4 UTC日時

人間向け日時は UTC で保存する。

```json
{
  "createdAtUtc": "2026-09-14T05:30:00Z"
}
```

対象:

- `createdAtUtc`
- `updatedAtUtc`
- `startedAtUtc`
- `generatedAtUtc`

UI表示時のみローカル時刻へ変換する。

---

# 6. TrainingProject Contract

```csharp
public sealed class TrainingProject
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Objective { get; set; }
    public string? TargetAudience { get; set; }
    public List<string> Prerequisites { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int Revision { get; set; } = 1;
    public RecordingInfo? Recording { get; set; }
    public List<TrainingStep> Steps { get; set; } = [];
    public ProjectOutputs Outputs { get; set; } = new();
}
```

## 6.1 Required

- `SchemaVersion`
- `Id`
- `Title`
- `CreatedAtUtc`
- `UpdatedAtUtc`
- `Revision`
- `Steps`
- `Outputs`

## 6.2 Optional

- `Objective`
- `TargetAudience`
- `Recording`

## 6.3 Array

以下は null にしない。

- `Prerequisites`
- `Steps`
- `SourceEventIds`

空の場合は `[]`。

---

# 7. RecordingInfo Contract

```csharp
public sealed class RecordingInfo
{
    public string MediaPath { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public bool HasSystemAudio { get; set; }
    public bool HasMicrophone { get; set; }
    public string? DisplayId { get; set; }
    public string? DisplayName { get; set; }
    public string? SystemAudioDeviceId { get; set; }
    public string? SystemAudioDeviceName { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string? MicrophoneDeviceName { get; set; }
}
```

### Device Rule

`DeviceName` は UI / Debug 用。再識別では `DeviceId` を優先する。

---

# 8. TimelineEvent Contract

```csharp
public sealed class TimelineEvent
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public long Seq { get; set; }
    public long TimestampMs { get; set; }
    public string Type { get; set; } = "";
    public JsonElement Payload { get; set; }
}
```

## 8.1 Constraints

```text
Id          : immutable
Seq         : monotonically increasing
Seq start   : 1
TimestampMs : >= 0
Type        : defined event type
Payload     : event-specific
```

---

# 9. MVP Event Type

## 9.1 User Operation

| Event Type | 意味 |
|---|---|
| `mouse.click` | 左クリック |
| `mouse.doubleClick` | ダブルクリック |
| `mouse.rightClick` | 右クリック |
| `keyboard.textEntry` | 文字入力 |
| `keyboard.specialKey` | Enter / Tab 等 |
| `keyboard.shortcut` | Ctrl+C 等 |

## 9.2 Recording Lifecycle

| Event Type | 意味 |
|---|---|
| `recording.started` | 録画開始 |
| `recording.paused` | 一時停止 |
| `recording.resumed` | 録画再開 |
| `recording.stopped` | 録画終了 |

Lifecycle Event は TrainingStep に変換しない。

---

# 10. Mouse Event Payload

```json
{
  "x": 1240,
  "y": 716,
  "button": "left",
  "clickCount": 1,
  "processName": "sample.exe",
  "windowTitle": "申請登録",
  "uiElement": {
    "name": "登録",
    "automationId": "BTN_REGISTER",
    "controlType": "Button",
    "className": "Button",
    "isEditable": false,
    "isPassword": false,
    "bounds": {
      "x": 1180,
      "y": 680,
      "width": 130,
      "height": 52
    }
  },
  "screenshotPath": "screenshots/original/event-000012.png"
}
```

UI Automation 失敗時は `"uiElement": null` としてよい。UI Automation の失敗を理由に Event 自体を破棄しない。

---

# 11. Keyboard Event Payload

## 11.1 keyboard.textEntry

実入力文字を保存しない。

```json
{
  "keyCount": 8,
  "processName": "sample.exe",
  "windowTitle": "ログイン",
  "target": {
    "name": "社員番号",
    "automationId": "EMPLOYEE_ID",
    "controlType": "Edit"
  },
  "isSensitive": false
}
```

Password等と判定した場合:

```json
{
  "keyCount": null,
  "isSensitive": true
}
```

禁止:

- 実際の入力文字
- Password文字列
- Password入力文字数の保存
- 推測した認証情報

## 11.2 keyboard.specialKey

```json
{
  "key": "Enter"
}
```

MVP対象:

```text
Enter
Tab
Escape
Backspace
Delete
Left
Right
Up
Down
```

## 11.3 keyboard.shortcut

```json
{
  "shortcut": "Ctrl+S"
}
```

MVP対象:

```text
Ctrl+A
Ctrl+C
Ctrl+V
Ctrl+S
Ctrl+Z
```

---

# 12. TrainingStep Contract

```csharp
public sealed class TrainingStep
{
    public Guid Id { get; set; }
    public int Order { get; set; }
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Caution { get; set; }
    public string? ExpectedResult { get; set; }
    public string? ScreenshotPath { get; set; }
    public List<Guid> SourceEventIds { get; set; } = [];
}
```

---

# 13. TrainingStep Action

MVP:

```text
click
doubleClick
rightClick
textEntry
specialKey
shortcut
manual
```

`manual` は Review UI からユーザーが追加した Step。その場合 `sourceEventIds = []`。

---

# 14. StartMs / EndMs Rule

## 単発操作

対象:

- click
- doubleClick
- rightClick
- specialKey
- shortcut

```json
{
  "startMs": 12500,
  "endMs": null
}
```

## 継続操作

例: textEntry

```json
{
  "startMs": 12500,
  "endMs": 14800
}
```

継続時間がない場合は `endMs = null`。意味のない `startMs == endMs` は使用しない。

---

# 15. SourceEventIds

TrainingStep がどの Raw Event から生成されたか追跡可能にする。

```text
Click Input
Key
Key
Key
Tab
```

↓

```json
{
  "action": "textEntry",
  "sourceEventIds": [
    "guid-click",
    "guid-key-1",
    "guid-key-2",
    "guid-key-3",
    "guid-tab"
  ]
}
```

用途:

- Debug
- Audit
- StepBuilder再実行
- 将来のAI再解析
- Algorithm変更時の追跡

---

# 16. Project Revision / Output Contract

```csharp
public sealed class ProjectOutputs
{
    public GeneratedArtifact? ManualMarkdown { get; set; }
    public GeneratedArtifact? ManualHtml { get; set; }
    public GeneratedArtifact? TrainingVideo { get; set; }
}

public sealed class GeneratedArtifact
{
    public string Path { get; set; } = "";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public int SourceRevision { get; set; }
}
```

TrainingStep や教材内容が編集された場合:

```text
TrainingProject.Revision++
```

例:

```text
Project.Revision = 8
TrainingVideo.SourceRevision = 6
```

この場合、Video は stale と判定し、UIで「再生成が必要」と表示可能。

---

# 17. Project Directory Contract

固定構造:

```text
projects/
└─ <project-guid>/
   │
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

---

# 18. Path Rule

JSON内に絶対パスを保存しない。

正:

```json
{
  "mediaPath": "raw/recording.mp4"
}
```

誤:

```json
{
  "mediaPath": "C:\\Users\\user\\AppData\\Local\\..."
}
```

JSON上の path separator は `/` に統一する。

目的:

- Project Copy
- Move
- Backup
- Export
- 別PCへの移動

---

# 19. project.json Example

```json
{
  "schemaVersion": 1,
  "id": "ac01e763-f98b-4458-bb63-68de71252121",
  "title": "経費申請登録",
  "objective": "経費申請を登録できるようになる",
  "targetAudience": "新入社員",
  "prerequisites": [],
  "createdAtUtc": "2026-09-14T05:30:00Z",
  "updatedAtUtc": "2026-09-14T05:42:00Z",
  "revision": 3,
  "recording": {
    "mediaPath": "raw/recording.mp4",
    "startedAtUtc": "2026-09-14T05:31:00Z",
    "durationMs": 84210,
    "hasSystemAudio": true,
    "hasMicrophone": true,
    "displayId": "DISPLAY1",
    "displayName": "1920x1080",
    "systemAudioDeviceId": "audio-output-id",
    "systemAudioDeviceName": "Speakers",
    "microphoneDeviceId": "mic-id",
    "microphoneDeviceName": "Microphone"
  },
  "steps": [
    {
      "id": "7b95f60e-f33c-4bf9-ac93-b6a4b5f71753",
      "order": 1,
      "startMs": 5210,
      "endMs": null,
      "action": "click",
      "target": "新規申請",
      "title": "「新規申請」をクリックします",
      "description": "新しい申請を作成します。",
      "caution": null,
      "expectedResult": "申請入力画面が表示されます。",
      "screenshotPath": "screenshots/edited/step-001.png",
      "sourceEventIds": [
        "ec53e460-f55f-49c7-8a43-95825b21e7ef"
      ]
    }
  ],
  "outputs": {
    "manualMarkdown": {
      "path": "manual/manual.md",
      "generatedAtUtc": "2026-09-14T05:41:00Z",
      "sourceRevision": 3
    },
    "manualHtml": {
      "path": "manual/manual.html",
      "generatedAtUtc": "2026-09-14T05:41:00Z",
      "sourceRevision": 3
    },
    "trainingVideo": null
  }
}
```

---

# 20. events.jsonl Example

1 Event = 1 Line。

```jsonl
{"schemaVersion":1,"id":"11111111-1111-1111-1111-111111111111","seq":1,"timestampMs":0,"type":"recording.started","payload":{}}
{"schemaVersion":1,"id":"22222222-2222-2222-2222-222222222222","seq":2,"timestampMs":5210,"type":"mouse.click","payload":{"x":1240,"y":716,"button":"left","clickCount":1,"processName":"sample.exe","windowTitle":"申請一覧","uiElement":{"name":"新規申請","automationId":"BTN_NEW","controlType":"Button","className":"Button","isEditable":false,"isPassword":false,"bounds":{"x":1180,"y":680,"width":130,"height":52}},"screenshotPath":"screenshots/original/event-000002.png"}}
{"schemaVersion":1,"id":"33333333-3333-3333-3333-333333333333","seq":3,"timestampMs":12480,"type":"keyboard.textEntry","payload":{"keyCount":8,"windowTitle":"申請登録","target":{"name":"社員番号","automationId":"EMPLOYEE_ID","controlType":"Edit"},"isSensitive":false}}
{"schemaVersion":1,"id":"44444444-4444-4444-4444-444444444444","seq":4,"timestampMs":84210,"type":"recording.stopped","payload":{}}
```

---

# 21. JSON Serialization Rule

C#:

```text
PascalCase
```

JSON:

```text
camelCase
```

例:

```text
TimestampMs
↓
timestampMs
```

推奨:

```csharp
new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    WriteIndented = true
};
```

`events.jsonl` は1イベント1行とし、pretty printしない。

---

# 22. Null / Empty Rule

## Array

null禁止。

```json
{
  "prerequisites": [],
  "sourceEventIds": []
}
```

## Optional Text

未設定は `null`。

```json
{
  "caution": null
}
```

不要な空文字 `""` は使わない。

## Required Text

以下は null / blank 禁止。

- Project `Title`
- Step `Title`
- Step `Action`
- Event `Type`

---

# 23. Schema Version Rule

現在:

```text
schemaVersion = 1
```

Schema Version は Breaking Change 時のみ増加する。

Version Up 不要:

- optional field追加
- 新Event Type追加
- 無視可能なmetadata追加

ただし、旧Readerが安全に無視できること。

---

# 24. Breaking Change

| 変更 | Breaking |
|---|---:|
| Field rename | YES |
| Field delete | YES |
| Field type change | YES |
| Required field追加 | YES |
| timestampMs意味変更 | YES |
| Path rule変更 | YES |
| 既存Event意味変更 | YES |
| Optional field追加 | NO |
| 新Event Type追加 | NO |

例: `timestampMs` を Pause除外時間から Pause込み時間へ変更する場合、Field名が同じでも Breaking Change。

---

# 25. Migration Rule

将来 `schemaVersion 1 → 2` となった場合:

```text
V1 Project
   ↓
V1 Reader
   ↓
Migration
   ↓
V2 Model
```

Migration 後の初回保存前に:

```text
project.v1.backup.json
```

を作成する。

新版コードが旧JSONを「最新版として直接解釈する」実装は禁止。

---

# 26. Unknown Event Rule

未知Eventを読んだ場合:

```json
{
  "type": "future.newEvent"
}
```

以下とする。

```text
Do not crash
Do not delete
Do not convert to TrainingStep
Log warning
Continue processing
```

---

# 27. Shared Contract Change Rule

以下は Shared Contract。

- `TrainingProject`
- `RecordingInfo`
- `TimelineEvent`
- `TrainingStep`
- `ProjectOutputs`
- Event Type
- Timestamp Rule
- Directory Layout
- Path Rule
- Schema Version

変更フロー:

```text
Proposal
   ↓
Impact確認
   ↓
Team Review
   ↓
本Document更新
   ↓
Model更新
   ↓
Tests更新
   ↓
Merge
```

担当者または担当AIが単独で変更してはならない。

---

# 28. Module Boundary

| Module | Input | Output |
|---|---|---|
| Capture | User operation | Recording + TimelineEvent |
| StepBuilder | TimelineEvent[] | TrainingStep[] |
| Review | TrainingStep[] | Edited TrainingStep[] |
| Manual | TrainingProject | Markdown / HTML |
| Video | Recording + TrainingProject | MP4 |
| Storage | TrainingProject | project.json |
| Manager | Project folders | Project summary |

---

# 29. Validation

Project保存前に最低限以下を検証する。

```text
schemaVersion == supported
Project.Id valid
Project.Title not blank
Project.Revision >= 1
Recording.DurationMs >= 0
Step.Id unique
Step.Order unique
Step.Order normalized to 1..N
Step.StartMs >= 0
Step.StartMs <= Recording.DurationMs
EndMs == null OR EndMs >= StartMs
SourceEventIds unique per Step
```

---

# 30. Phase 0 Core Test Contract

| Test | Expected |
|---|---|
| Project serialize → deserialize | PASS |
| Relative path保存 | PASS |
| Absolute path拒否 | PASS |
| Duplicate Step ID | FAIL |
| Duplicate Step Order | FAIL |
| Negative Timestamp | FAIL |
| End < Start | FAIL |
| Unknown Event Type | Warning + Continue |
| schemaVersion > supported | Reject |
| null array | Normalize or Reject |
| Password raw text storage | Must not exist |

---

# 31. Phase 1 への引き継ぎ

## A — Recording Spike

Reference:

- ScreenRecorderLib
  https://github.com/sskodje/ScreenRecorderLib
- NessStudio
  https://github.com/micilini/NessStudio

目的:

```text
Desktop
+
System Audio
+
Microphone
↓
Stable MP4
```

## B — Operation Capture Spike

Reference:

- OpenSteps
  https://github.com/ebanez8/openstep

重点確認:

```text
src/OpenSteps.Capture/GlobalMouseHook.cs
src/OpenSteps.Capture/GlobalKeyboardHook.cs
src/OpenSteps.Capture/UiAutomationService.cs
src/OpenSteps.Capture/ScreenshotService.cs
```

目的:

```text
User Operation
↓
Timestamp
Action
Process
Window
UI Element
Screenshot
↓
TimelineEvent
```

## C — Step / Manual

Reference:

- OpenSteps
  https://github.com/ebanez8/openstep

重点確認:

```text
src/OpenSteps.Capture/ScreenshotRedactionService.cs
src/OpenSteps.Core/Services/MarkdownExporter.cs
src/OpenSteps.Core/Services/SessionStore.cs
```

入力は Phase 0 Contract に従った fixture を使用し、A/B完了を待たない。

## D — App / Integration

Reference:

- OpenSteps
  https://github.com/ebanez8/openstep

担当:

```text
WPF Shell
Project Create/Open
ProjectStore
Review UI Skeleton
Content Manager Skeleton
```

---

# 32. Post-MVP 参考Repository

AI Step / Smart Frame / STT / Sensitive Scan 等の将来機能は以下を参考とする。

- Microsoft Skill Recorder
  https://github.com/microsoft/skill-recorder

重点確認:

```text
electron/video/capture-preload.cjs
electron/frames/extractor.ts
electron/recorder/session-store.ts
electron/sensitive/scanner.ts
electron/narration/whisper.ts
```

MVPでは直接依存しない。

---

# 33. OSS License

今回の参考対象:

| Repository | License |
|---|---|
| ScreenRecorderLib | MIT |
| OpenSteps | MIT |
| NessStudio | MIT |
| Microsoft Skill Recorder | MIT |

コードをコピー・改変する場合、Repository Root に:

```text
THIRD_PARTY_NOTICES.md
```

を作成し、最低限以下を記録する。

```text
Repository
URL
License
Copyright
Reference / copied files
Commit SHA
Modification
```

---

# 34. Frozen Decisions Summary

```text
Project Root
│
├─ project.json
├─ events.jsonl
├─ raw/
├─ screenshots/
├─ manual/
└─ output/
```

```text
TimelineEvent
     ↓
StepBuilder
     ↓
TrainingStep
     ↓
Review
     ↓
TrainingProject
     ↓
Manual / Video
```

```text
timestampMs
= recording start からの milliseconds
= Pause時間を除外
```

```text
Keyboard Capture
≠ Keylogger
```

```text
JSON Path
= Project-relative
```

```text
schemaVersion
= 1
```

```text
Manual Description
=
Video Description
=
TrainingStep.Description
```

---

# 35. AI向け最終指示

この文書を参照するAIは以下を守ること。

- Contractにない必須Fieldを勝手に追加しない。
- Raw Event を直接Manualへ出力しない。
- TrainingStepをRaw Eventとして扱わない。
- Timestamp単位を秒・frame・DateTimeへ変更しない。
- Pauseを含む別Timelineを作らない。
- Absolute Pathを永続化しない。
- 実際のKeyboard入力文字を保存しない。
- ManualとVideoで別々のDescriptionを作らない。
- Shared Contract変更が必要な場合、変更案として提示し、既存Contractを書き換えた前提で実装を進めない。
- OSSコードを利用する場合、参照元Repository/File/Commitを記録する。

**End of Phase 0 Contract v1.0**
