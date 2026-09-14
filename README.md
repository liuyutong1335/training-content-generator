# training-content-generator

[![plan](https://img.shields.io/badge/plan-v0.2-orange)](docs/development-plan.md)
[![contract](https://img.shields.io/badge/contract-frozen_v1-blue)](docs/phase0-contract.md)
[![dotnet](https://img.shields.io/badge/.NET-8.0-blue)](TrainingContentGenerator.sln)

デスクトップ上の業務操作（画面・システム音声・マイク音声・マウス/キーボード操作）を記録し、**トレーニング動画**（MP4）と**操作マニュアル**（Markdown / HTML）を生成・管理する **Windows デスクトップアプリ**（WPF）です。詳細は [開発計画書 v0.2](docs/development-plan.md) を参照してください。

## 概要

動画とマニュアルを個別に生成するのではなく、録画・操作イベント・テキストを共通の **TrainingProject** として管理します。

```
画面録画 ＋ システム音声 ＋ マイク音声 ＋ マウス/キーボード操作 ＋ テキスト
        ↓
   TrainingProject（Recording / Timeline / Events / Steps / Assets）
        ├→ Training Video (MP4)
        └→ Manual (Markdown / HTML)
        ↓
   Content Manager（一覧・再読込・再生成・削除・出力）
```

## 必須要件

| ID | 要件 |
|----|------|
| R-01 | 画面録画（デスクトップ全体・任意アプリを区別しない） |
| R-02 | システム音声録画 |
| R-03 | マイク音声録画 |
| R-04 | テキスト入力 |
| R-05 | トレーニング動画生成 |
| R-06 | 操作マニュアル生成 |
| R-07 | コンテンツ管理画面 |

## 技術構成

| 領域 | 採用技術 |
|------|---------|
| Language / Runtime | **C# / .NET 8** |
| Desktop UI | WPF |
| Screen Recording | [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib)（NuGet・MIT） |
| System Audio / Mic | WASAPI Loopback / Capture（ScreenRecorderLib 経由） |
| Mouse / Keyboard Capture | Win32 Low-Level Hook |
| UI 情報取得 | Windows UI Automation |
| Data | JSON / JSONL（ローカルファイルシステム・DB なし） |
| Test | xUnit / `dotnet test` |

OSS 利用方針・ライセンス記録は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照（OBS / ShareX は GPL のためコードを利用しない）。

### 共通データ契約（Phase 0 — FROZEN）

**[docs/phase0-contract.md](docs/phase0-contract.md) が Source of Truth。** 実装・設計・レビュー時は本書を優先する:

- `TrainingProject` / `TimelineEvent` / `TrainingStep` / `RecordingInfo` / `ProjectOutputs` の定義（`src/TrainingContent.Core/Models/` が契約実装）
- Timestamp: `timestampMs`（long・ミリ秒・録画開始 = 0・**Pause 時間は除外**）
- ID は GUID。JSON 内のパスは Project 相対 + `/` 区切り（絶対パス禁止）
- JSON は camelCase（`JsonSerializerDefaults.Web`）。`events.jsonl` は 1 Event = 1 行
- Raw Event（append-only・編集しない）と TrainingStep（Review で編集可）を混同しない
- Manual / Video の説明文は両方とも `TrainingStep.Description`（別々に生成しない）
- 入力文字そのもの・Password を保存しない
- Shared Contract の変更は Proposal → Team Review → 本書更新 → Model/Tests 更新の順（単独変更禁止）

## セットアップ

```powershell
# .NET 8 SDK が必要です
winget install Microsoft.DotNet.SDK.8

dotnet build TrainingContentGenerator.sln
dotnet test TrainingContentGenerator.sln
```

## プロジェクト構成（計画書 §12）

```
TrainingContentGenerator.sln
├─ src/
│  ├─ TrainingContent.App/        D: WPF Shell・Recording/Review UI・Content Manager
│  ├─ TrainingContent.Core/       共有: TrainingProject / TimelineEvent / TrainingStep（変更は4名合議）
│  ├─ TrainingContent.Capture/    A: Recording Engine（ScreenRecorderLib 抽象化・デバイス列挙・Pause/Resume）
│  ├─ TrainingContent.Video/      B: Timeline / Overlay / Subtitle / Renderer
│  ├─ TrainingContent.Manual/     C: Markdown / HTML 生成
│  └─ TrainingContent.Storage/    D: ProjectStore / ContentIndex
├─ tests/                          xUnit（Core/Capture/Manual/Video/Integration）
├─ docs/development-plan.md        開発計画書 v0.2（正）
├─ spike/python-recording/         v0.1 Python spike のアーカイブ（参考資料）
├─ projects/                       教材プロジェクトデータ（gitignore）
└─ specs/                          Tecnos-STRIDE 成果物（Gate 管理中）
```

## 担当分担（計画書 §24）

| 担当 | 主領域 | 参考 Repository |
|------|--------|----------------|
| **A: Recording Engine** | `TrainingContent.Capture` | ScreenRecorderLib / NessStudio |
| B: Event Capture / Timeline | Mouse/Keyboard Hook・UIA・Master Clock・events.jsonl | OpenSteps |
| C: Training Content | TrainingStep / StepBuilder / Screenshot / Redaction / Manual | OpenSteps |
| D: App / Integration | WPF Shell / ProjectStore / Content Manager / E2E | OpenSteps SessionStore |

### 開発 Gate（計画書 §30）

| Gate | 内容 |
|------|------|
| G1 Capture Gate | Desktop + System Audio + Mic を安定録画 |
| G2 Timeline Gate | Video / Mouse / Keyboard / Screenshot が同一時間軸で対応 |
| G3 Training Data Gate | Raw Event → TrainingStep 生成・保存・再読込 |
| G4 Output Gate | 同一 TrainingStep から Manual + Video を生成し内容一致 |

**G1〜G4 がすべて PASS した時点を MVP 完了とする。**

## 開発ルール

1. `TrainingProject.cs` / `TimelineEvent.cs` / `TrainingStep.cs` / Event Type / Timestamp rule / schema_version / ディレクトリ構造は **Shared 領域**。単独変更禁止（Proposal → Team Review → Merge）
2. Timestamp は**ミリ秒**に統一（録画開始基準）
3. Raw Event は原則変更しない。Training Step のみ Review UI から編集
4. Manual / Video の説明文は両方とも `TrainingStep.Description` を使用（別々に生成しない）
5. セキュリティ: 入力文字そのものを Keyboard Hook で保存しない・Password 入力を Step として保存しない・Project はローカル保存

## 現在の状態

- [x] Spike（Python・v0.1 時代）— `spike/python-recording/` にアーカイブ
- [x] Phase 0: Contract Freeze — **`docs/phase0-contract.md` v1.0 FROZEN**（`TrainingContent.Core` が契約実装・`TrainingContent.Core.Tests` が §30 テスト契約）
- [ ] Phase 1: Spike A（Recording: v6.6.0 vs v7.0.1 実機比較・`src/TrainingContent.Capture` が骨格）/ Spike B（Operation Capture）
- [ ] Phase 2〜8: 統合録画 → Step Builder → Review UI → Manual → Video → Content Manager → E2E
