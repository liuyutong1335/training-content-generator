---
artifact: basic_design
template_id: TPL-BASICDESIGN-001
feature_id: FEAT-TRAININGCONTENTGENERATOR-6C9AD51F05425875
feature_name: "training-content-generator"
profile: prototype
created_at: 2026-09-14
---

# training-content-generator — Basic Design

> v0.2 開発計画書（docs/development-plan.md）反映版ドラフト。Gate 1 審議対象。
> **Phase 0 共通データ契約 v1.0 が FROZEN**（docs/phase0-contract.md）— 共有モデルは同書を優先。

## #0 Need (なぜ)
> 解決したい課題と背景。

- RQ-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001: 研修教材の作成では、画面録画・説明文作成・動画編集・手順書作成が別々の作業になっており、①作成工数が大きい、②マニュアルと動画で内容が食い違う、③手順変更のたびに両方を修正し直す必要がある、という課題がある。

## #1 Solution (何を)
> 機能の outline。process.bpmn と対応。

- US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001: 研修担当者として、デスクトップ上の業務操作（画面・システム音声・マイク・マウス/キーボード操作）を 1 回録画し、Raw Event から Training Step を生成・編集するだけで、トレーニング動画 (MP4) と操作マニュアル (Markdown / HTML) を自動生成・管理したい。

## #2 Stakeholders (誰が)
> Primary user / Secondary / Approver / Auditor。RACI+ 設定。

| Role | Person | Phase 関与 | RACI+ |
|---|---|---|---|
| Owner / 研修担当 | 社内研修チーム | All | A |
| Dev | AI agent (Claude Code) | Phase 4 | R |
| 開発メンバー A | Liu Yutong（Recording Engine 担当） | All | R |
| 開発メンバー B/C/D | Event Capture・Timeline / Training Content / App・Integration 担当 | Phase 2〜8 | R |
| Approver | Method Board（Gate 承認者） | All Gates | A |

## #3 Value (何を達成)
> KPI / OKR / 監査要件等 measurable な指標。

- 研修教材 1 本あたりの作成時間を、録画＋Step 確認＋生成確認に短縮する
- マニュアルと動画が同一 `TrainingStep.Description` から生成されることを構造的に保証（内容不一致ゼロ・計画書 §20）
- 手順変更時に Step 編集→再生成だけで教材を更新できる（再録画不要）

## #4 Context (前提条件)
> 規制 / 既存システム / 制約。

- Windows 10 / 11・ローカル完結・単一ユーザー・社内利用（クラウド配信なし・DB 導入なし）
- 技術構成（計画書 §3）: **C# / .NET 8 / WPF**、ScreenRecorderLib（MIT・NuGet）、WASAPI Loopback/Capture、Win32 Low-Level Hook、UI Automation、JSON/JSONL、xUnit
- セキュリティ方針（計画書 §27）: 入力文字そのものを保存しない・Password 入力を Step として保存しない・Project はローカル保存
- OSS 活用方針（計画書 §4〜8）: ScreenRecorderLib は NuGet 直接利用、OpenSteps は部分移植、NessStudio は設計参考、Skill Recorder は Post-MVP 参考
- 開発計画書: `docs/development-plan.md`（v0.2・正）/ OSS ライセンス記録: `THIRD_PARTY_NOTICES.md`

## #5 Change (変える状態)
> 現状 → ローンチ後の差分。

- 現状: 研修教材は手作業（録画ツール・エディタ・ワープロの併用）で作成
- 目標: 1 回の録画 → 共通 TrainingProject（Raw Event は append-only）→ 動画・マニュアルを決定論的に自動生成・管理

## Traceability Matrix

| RQ | US | AC | BPMN-TASK | CT | Test | Task |
|----|----|----|-----------|-----|------|------|
| RQ-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001 | US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001 | AC-US-…-001-01〜07 | BPMN-TASK-001〜005 | CT-MODEL-…-001 | TS-UNIT/INT/E2E | WI-001〜 |

## Delivery context

```yaml
basic_design:
  feature_id: FEAT-TRAININGCONTENTGENERATOR-6C9AD51F05425875
  feature_name: training-content-generator
  profile: prototype
  scale: starter
  gate_mode: standard
  requirements:
    - R-01: 画面録画（デスクトップ全体・任意アプリ区別なし）
    - R-02: システム音声録画
    - R-03: マイク音声録画
    - R-04: テキスト入力
    - R-05: トレーニング動画生成
    - R-06: 操作マニュアル生成
    - R-07: コンテンツ管理画面
  delivery_model: desktop_app_local_single_user
  tech_stack: "C# / .NET 8 / WPF / ScreenRecorderLib / WASAPI / JSON-JSONL / xUnit"
  bpmn_descriptions:
    - id: BPMN-TASK-001
      name: "録画（画面・システム音声・マイク）と操作イベント取得"
    - id: BPMN-TASK-002
      name: "Raw Event から Training Step 生成・Review で編集"
    - id: BPMN-TASK-003
      name: "トレーニング動画の生成 (TrainingStep.Description を使用)"
    - id: BPMN-TASK-004
      name: "操作マニュアルの生成 (TrainingStep.Description を使用)"
    - id: BPMN-TASK-005
      name: "コンテンツ管理画面で確認・再読込・再生成・削除"
  traceability_rows:
    - rq: RQ-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001
      us: US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001
      ac:
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-01
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-02
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-03
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-04
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-05
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-06
        - AC-US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001-07
      bpmn_task:
        - BPMN-TASK-001
        - BPMN-TASK-002
        - BPMN-TASK-003
        - BPMN-TASK-004
        - BPMN-TASK-005
      ct:
        - CT-MODEL-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001
      test:
        - TS-INT-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001
        - TS-E2E-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001
      task:
        - WI-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001
```

## Caller-supplied intake context

The following answers are supplied context, not approved requirements. Resolve remaining TODOs against these answers.

> # 変更履歴
>
> - 2026-09-14: 開発計画書 v0.1（Python + PySide6 + FFmpeg + PyAudioWPatch）→ **v0.2（C# / .NET 8 / WPF + ScreenRecorderLib）** に技術スタック変更。
> - v0.1 期の Python Spike は `spike/python-recording/` にアーカイブ（3 ソース同時録画実機成功・DPI/音声デバイス/pyaudiowpatch blocking read の知見は v0.2 Spike A に入力）。
> - 担当 A の領域は「Recording Engine」に集約（IRecordingEngine / ScreenRecorderRecordingEngine / Device Enumeration / Start-Pause-Resume-Stop / MP4）。Mouse/Keyboard Hook は担当 B へ。
> - Pause/Resume は Gate A の必須検証項目（10 分録画・音ズレ確認・v6.6.0 vs v7.0.1 実機比較）。
