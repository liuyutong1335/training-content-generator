# training-content-generator

[![plan](https://img.shields.io/badge/plan-v0.1-orange)](docs/development-plan.md)
[![python](https://img.shields.io/badge/python-3.10%2B-green)](pyproject.toml)

デスクトップ上の操作・音声・入力テキストを記録し、**トレーニング動画**（MP4）と**操作マニュアル**（Markdown / HTML）を生成・管理する Windows デスクトップアプリです。詳細は [開発計画書](docs/development-plan.md) を参照してください。

## 概要

研修教材を手作業で作ると、録画・説明文の作成・動画編集・手順書作成がすべて別々の作業になり、内容の食い違いや再作成コストが発生します。本ツールは「画面＋システム音声＋マイク音声＋Step Marker」を**共通の Training Project データ**として 1 回記録し、動画とマニュアルの双方をそこから決定論的に生成します。

```
画面録画 ＋ システム音声 ＋ マイク音声 ＋ Step Marker
        ↓
   Training Project   ← ★唯一の共通データ（Raw Media / Timeline / Metadata / Frames）
        ├→ Video Generator  → training_video.mp4
        └→ Manual Generator → manual.md / manual.html
        ↓
   Content Manager（一覧・閲覧・再生成・削除・出力）
```

## 主な機能

| 要件 | 機能 | 説明 |
|------|------|------|
| R-01 | 画面録画 | デスクトップ全体／任意アプリケーションを録画 |
| R-02 | 音声録画 | システム音声とマイク音声の双方を録音 |
| R-03 | テキスト入力 | 教材情報・補足説明・Step Marker（title / description / caution / expected_result）を入力 |
| R-04 | 動画生成 | 録画素材に音声ミックス・タイトル・字幕を合成して MP4 出力 |
| R-05 | マニュアル生成 | Step・代表フレーム・注意事項・期待結果から Markdown / HTML を生成 |
| R-06 | コンテンツ管理画面 | 作成済み教材の一覧・閲覧・再生成・削除・出力 |

## 設計原則

- **Single Source of Truth** — 動画・マニュアルは共通プロジェクトデータを参照する（完成 MP4 の再解析はしない）
- **Raw / Work / Output 分離** — 原素材・加工データ・成果物をディレクトリで分離
- **Timestamp 統一** — 録画開始からの経過時間を全モジュールで同一単位・基準とする
- **Deterministic Generation** — MVP の成果物生成はルールベースで再現可能とする
- **Desktop First** — 全アプリ・システム音声要件のため Web UI ではなくデスクトップアプリ（PySide6）

## 技術スタック

| 領域 | 採用 | 用途 |
|------|------|------|
| Desktop UI | [PySide6](https://pypi.org/project/PySide6/) | 録画・レビュー・生成・管理画面 |
| 録画 | FFmpeg（Windows Capture / WASAPI） | 画面・2 系統音声の同時録音 ※Technical Spike で確定 |
| 音動画処理 | FFmpeg | 音声ミックス、字幕合成、MP4 出力 |
| 画像処理 | Pillow / OpenCV | フレーム抽出・画像処理 |
| データモデル | Pydantic | Project / Timeline / Marker の型定義・バリデーション |
| マニュアル生成 | Jinja2 | Markdown / HTML テンプレート |
| データ管理 | SQLite | プロジェクト一覧・状態・成果物パス |
| テスト | pytest | 単体・結合・E2E |

## セットアップ

```bash
git clone https://github.com/liuyutong1335/training-content-generator.git
cd training-content-generator

python -m venv .venv
# Windows (PowerShell)
.venv\Scripts\Activate.ps1
# macOS / Linux
source .venv/bin/activate

pip install -e .
```

FFmpeg は別途インストールが必要です（[ffmpeg.org](https://www.ffmpeg.org/download.html)、PATH を通してください）。

## 使い方

```bash
python app.py
```

アプリ内の画面フロー:

```
Project（教材情報入力） → Record（録画＋Step Marker） → Review（確認・修正）
      → Generate（動画／マニュアル生成） → Contents（一覧・再生成・削除・出力）
```

プロジェクトデータは `projects/<project_id>/` 配下に保存され、再オープン・再生成が可能です。

## プロジェクト構成

```
training-content-generator/
├─ app.py                  起動入口 (PySide6) — D
├─ core/                   共有データモデル（Pydantic）★変更は4名合議
│  ├─ models.py            Project / Timeline / Marker の型定義
│  ├─ project.py           Project の読み書き
│  ├─ timeline.py          Timeline / Marker 操作
│  └─ config.py            設定
├─ capture/                A: 録画（画面・システム音声・マイク・同期）
│  ├─ screen.py / system_audio.py / microphone.py
│  ├─ recorder.py          録画制御（開始・停止・一時停止）
│  └─ devices.py           デバイス列挙
├─ video_generator/        B: 動画生成（storyboard / renderer / audio / subtitle）
├─ manual_generator/       C: マニュアル生成（generator / frame_extractor / templates）
├─ storage/                D: SQLite・ファイル管理
├─ ui/                     D: PySide6 画面（project / record / review / generate / contents）
├─ tests/                  pytest（単体・結合・E2E）
├─ projects/               教材プロジェクトデータ（Raw Media / Timeline / Outputs）
└─ docs/
   └─ development-plan.md  開発計画書（正）
```

## 開発

```bash
python -m pytest tests/ -v
```

### コントリビューションルール

1. `core/models.py`（共有契約）の変更は**先にチーム 4 名の合意**を取る。独自フィールドを追加しない
2. timestamp は全モジュールで同一単位・基準（録画開始からの経過時間）を使用する
3. A の録画完了を待たずに全員が作業できるよう、共通の 1～2 分録画サンプルを用意する
4. README / pyproject.toml の変更は統合担当（D）を窓口とする

### 開発優先順位

| 優先 | 対象 |
|------|------|
| P0 | 録画不能、ファイル破損、E2E 不通 |
| P1 | 音画同期、成果物内容不一致、データ破損 |
| P2 | 入力チェック、エラー表示、再生成・削除の不整合 |
| P3 | UI 装飾、アニメーション、非必須改善 |

## ロードマップ

| バージョン | テーマ | 主要機能 |
|-----------|--------|---------|
| MVP | Recording 基盤 | 録画（画面＋2 系統音声）→ 動画／マニュアル生成 → 管理画面 |
| v0.2 | Recording Reliability | デバイスエラー対応、録画復旧、Timeline 編集 |
| v0.3 | Auto Shortening | 無駄区間の自動検出・削除候補 |
| v0.4 | Text to TTS | 入力テキストの機械音声化 |
| v0.5 | Voice to TTS | 録画音声 → STT → 確認 → TTS |
| v0.6 | AI Step Detection | Step 候補の自動生成 |
| v0.7 | AI Content Enrichment | Step タイトル・説明等の提案（人間承認フロー） |
| v1.0 | Productization | パッケージ化・設定・品質 Gate |

## セキュリティ

録画には画面上の機密情報が含まれ得ます。MVP ではローカル保存と出力前確認を行い、マスキング機能は後続バージョンで追加します（開発計画書 §11 のリスク管理を参照）。
