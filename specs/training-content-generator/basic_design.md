---
artifact: basic_design
template_id: TPL-BASICDESIGN-001
feature_id: FEAT-TRAININGCONTENTGENERATOR-6C9AD51F05425875
feature_name: "training-content-generator"
profile: prototype
created_at: 2026-09-14
---

# training-content-generator — Basic Design

## #0 Need (なぜ)
> 解決したい課題と背景。

- RQ-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001: 研修教材の作成では、画面録画・説明文作成・動画編集・手順書作成が別々の作業になっており、①作成工数が大きい、②マニュアルと動画で内容が食い違う、③手順変更のたびに両方を修正し直す必要がある、という課題がある。

## #1 Solution (何を)
> 機能の outline。process.bpmn と対応。

- US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001: 研修担当者として、デスクトップ操作を 1 回録画し Step Marker（操作説明・注意事項・期待結果）を登録するだけで、トレーニング動画 (MP4) と操作マニュアル (Markdown / HTML) を自動生成・管理したい。

## #2 Stakeholders (誰が)
> Primary user / Secondary / Approver / Auditor。RACI+ 設定。

| Role | Person | Phase 関与 | RACI+ |
|---|---|---|---|
| Owner / 研修担当 | 社内研修チーム | All | A |
| Dev | AI agent (Claude Code) | Phase 4 | R |
| 開発メンバー A | Liu Yutong（Capture 担当） | All | R |
| 開発メンバー B/C/D | Timeline・Video / Manual / UI・Storage 担当 | Phase 2〜4 | R |
| Approver | Method Board（Gate 承認者） | All Gates | A |

## #3 Value (何を達成)
> KPI / OKR / 監査要件等 measurable な指標。

- 研修教材 1 本あたりの作成時間を、録画＋Step 入力＋生成確認に短縮する
- マニュアルと動画が同一プロジェクトデータから生成されることを構造的に保証（内容不一致ゼロ）
- 手順変更時に Step 編集→再生成だけで教材を更新できる（再録画不要）

## #4 Context (前提条件)
> 規制 / 既存システム / 制約。

- Windows デスクトップ・ローカル完結・単一ユーザー・社内利用（クラウド配信なし）
- 録画に機密情報が含まれ得るためローカル保存・出力前確認（§11 リスク管理）
- 技術構成: PySide6 / FFmpeg (gdigrab + WASAPI) / Pydantic / Jinja2 / OpenCV / SQLite / pytest
- 開発計画書: `docs/development-plan.md`（正）

## #5 Change (変える状態)
> 現状 → ローンチ後の差分。

- 現状: 研修教材は手作業（録画ツール・エディタ・ワープロの併用）で作成
- 目標: 1 回の録画 → 共通 Training Project → 動画・マニュアルを決定論的に自動生成・管理

## Traceability Matrix

| RQ | US | AC | BPMN-TASK | CT | Test | Task |
|----|----|----|-----------|-----|------|------|
| RQ-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001 | US-TRAININGCONTENTGENERATOR-6C9AD51F05425875-001 | AC-US-…-001-01〜06 | BPMN-TASK-001〜005 | CT-MODEL-…-001 | TS-UNIT/INT/E2E | WI-001〜007 |

## Delivery context

```yaml
basic_design:
  feature_id: FEAT-TRAININGCONTENTGENERATOR-6C9AD51F05425875
  feature_name: training-content-generator
  profile: prototype
  scale: starter
  gate_mode: standard
  requirements:
    - R-01: 画面録画
    - R-02: 音声録画（システム音声＋マイク）
    - R-03: テキスト入力
    - R-04: トレーニング動画生成
    - R-05: 操作マニュアル生成
    - R-06: コンテンツ管理画面
  delivery_model: desktop_app_local_single_user
  bpmn_descriptions:
    - id: BPMN-TASK-001
      name: "画面・音声の録画と Step Marker 登録 (R-01/R-02/R-03)"
    - id: BPMN-TASK-002
      name: "レビュー: Step の確認・修正 (F-05)"
    - id: BPMN-TASK-003
      name: "トレーニング動画の生成 (R-04)"
    - id: BPMN-TASK-004
      name: "操作マニュアルの生成 (R-05)"
    - id: BPMN-TASK-005
      name: "コンテンツ管理画面で確認・再生成・削除 (R-06)"
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
      bpmn_task:
        - BPMN-TASK-001
        - BPMN-TASK-002
        - BPMN-TASK-003
        - BPMN-TASK-004
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

> # basic_design_intake（開発計画書 v0.1 docs/development-plan.md から転記）
>
> ## プロジェクト概要
> - 名称: Training Content Generator
> - 目的: デスクトップ上の操作・音声・入力テキストを記録し、トレーニング動画と操作マニュアルを生成・管理する
> - 対象環境: Windows デスクトップ（ローカル完結・単一ユーザー・社内利用）
> - 開発体制: 4 名（A: Capture 録画 / B: Timeline・Video 生成 / C: Manual 生成 / D: UI・Storage・統合）
> - 期間目安: MVP 5〜8 営業日
>
> ## 必須要件
> - R-01 画面録画: デスクトップ全体／任意アプリケーションを録画
> - R-02 音声録画: システム音声とマイク音声の双方を録音
> - R-03 テキスト入力: 教材情報・補足説明・Step Marker の入力
> - R-04 動画生成: R-01〜R-03 を統合し MP4 生成
> - R-05 マニュアル生成: 共通プロジェクトデータから Markdown / HTML 生成
> - R-06 管理画面: 一覧・閲覧・再生成・削除・出力
>
> ## あれば嬉しい機能（MVP 後）
> - E-01 無駄区間の自動削除 (v0.3) / E-02 録音音声の機械音声化 (v0.5) / E-03 テキストの機械音声化 (v0.4)
>
> ## 設計原則
> - P-01 Single Source of Truth（動画・マニュアルは共通プロジェクトデータを参照）
> - P-02 Raw / Work / Output 分離
> - P-03 Timestamp 統一（録画開始からの経過 ms）
> - P-04 Deterministic Generation
> - P-05 Desktop First（PySide6）
> - P-06 Manual 生成は完成 MP4 再解析を必須としない
>
> ## 技術構成
> - PySide6 / FFmpeg (gdigrab + WASAPI, imageio-ffmpeg フォールバック) / Pydantic / Jinja2 / OpenCV / Pillow / SQLite / pytest
>
> ## MVP 完了条件 (Definition of Done)
> - D-01〜D-12: 任意デスクトップ録画、システム音声・マイク録音、音画同期、Step Marker 保存・編集、MP4 再生、MD/HTML マニュアル＋Step 対応画像、Project 保存・再オープン、管理画面、エラー表示、E2E 通過
>
> ## 実装済みの状況
> - 担当 A の capture モジュール（画面+システム音声+マイク同時録画・RecordingManifest 出力）を feature/capture ブランチに実装済み。実機スパイク 4 秒録画成功。core/models.py は Phase 1 契約ドラフト（4名合議待ち）。
