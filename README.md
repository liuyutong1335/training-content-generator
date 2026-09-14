# training-content-generator

[![contract](https://img.shields.io/badge/contract-1.0.0-blue)](shared/contract.md)
[![python](https://img.shields.io/badge/python-3.10%2B-green)](pyproject.toml)

操作記録（JSON ＋ スクリーンショット）から、**操作マニュアル**（Markdown / HTML）と**トレーニング動画**（MP4・字幕焼き込み）を自動生成するツールです。研修教材の作成工数を削減し、マニュアルと動画の内容を常に同一のデータソースから保証します。

## 概要

システム操作の研修教材を手作業で作成すると、マニュアルと動画で説明が食い違ったり、工程の追加のたびに両方を修正する必要があります。本ツールは「操作ステップデータ」を唯一の正本（single source of truth）とし、そこから複数の成果物を決定論的にレンダリングする構成を採っています。

```
操作記録 JSON ＋ スクリーンショット
        ↓ 正規化・検証・脱敏
  normalized_steps.json   ← ★唯一の共通中間データ
        ├→ マニュアル生成 (Markdown / HTML)
        └→ 動画生成 (MP4・字幕焼き込み)
```

## 主な機能

| 機能 | 説明 |
|------|------|
| 操作記録の取込 | 事前準備した `operation.json` ＋ スクリーンショットを読み込み、検証・正規化 |
| データ検証 | 必須フィールド欠落・ステップ番号不連続・スクリーンショット欠落等を検出 |
| センシティブ情報の脱敏 | パスワード・トークン等を自動マスクして生成物への混入を防止 |
| マニュアル生成 | テンプレートベースで Markdown / HTML を出力 |
| 動画生成 | ステップごとのスライド＋字幕を焼き込んだ MP4 を出力 |

## アーキテクチャ

```
recorder/            入力の読込・正規化・検証・脱敏
manual_generator/    マニュアルのレンダリング (Markdown / HTML)
video_generator/     動画のレンダリング (MP4)
ui/                  Web UI・API・統合
shared/              データ契約（スキーマ・サンプル・ルール）
```

設計上の原則:

- **契約ファースト** — モジュール間のデータ形式は `shared/contract.md` と `shared/operation.schema.json` で一元管理し、変更は必ず契約の修正から行う
- **共通データソース** — マニュアルと動画は同じ `normalized_steps.json` から生成され、字幕は `description` フィールドをそのまま使用する
- **決定論的レンダリング** — 生成物に自由な文章生成を挟まず、入力データに完全に対応付ける
- **レンダラーの純粋性** — PDF 等の出力形式を追加する場合も、業務ロジックを持たないレンダラーとして実装する

## 技術スタック

- Python 3.10+
- [jsonschema](https://pypi.org/project/jsonschema/) — 入力検証
- [Jinja2](https://pypi.org/project/jinja2/) — マニュアルテンプレート
- [Pillow](https://pypi.org/project/pillow/) — 画像処理（リサイズ・ハイライト）
- [MoviePy](https://pypi.org/project/moviepy/)（FFmpeg ベース）— 動画合成
- [Streamlit](https://pypi.org/project/streamlit/) — Web UI

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

FFmpeg は MoviePy の実行に必要です。環境にない場合は [ffmpeg.org](https://www.ffmpeg.org/download.html) からインストールしてください。

## 使い方

```bash
# 1. 入力を配置
#    input/operation.json           操作記録
#    input/screenshots/step01.png … スクリーンショット（step{番号:02d}.png）

# 2. アプリを起動
streamlit run app.py
```

Web UI から「アップロード → ステップ確認 → マニュアル生成 → 動画生成 → ダウンロード」の流れで操作します。

CLI での利用（モジュールを直接呼ぶ場合）:

```bash
python -m recorder.loader input/operation.json   # 例: 正規化データの生成
```

成果物は `output/` に出力されます:

| 成果物 | パス |
|--------|------|
| マニュアル (Markdown) | `output/manual.md` |
| マニュアル (HTML) | `output/manual.html` |
| トレーニング動画 | `output/training_video.mp4` |

## プロジェクト構成

```
training-content-generator/
├─ app.py                     起動入口 (Streamlit)
├─ shared/
│  ├─ contract.md             データ契約書 ★変更は必ずここから
│  ├─ operation.schema.json   JSON Schema
│  └─ sample_operation.json   標準デモデータ
├─ recorder/                  A: 読込・正規化・検証・脱敏
├─ manual_generator/          B: マニュアル生成
│  └─ templates/              Jinja2 テンプレート
├─ video_generator/           C: 動画生成
├─ ui/                        D: Web UI・API
├─ tests/                     テスト
├─ input/                     入力 (operation.json + screenshots/)
├─ workdata/                  正規化データ (normalized_steps.json)
└─ output/                    生成物
```

## 開発

```bash
# テスト
python -m pytest tests/ -v
```

### コントリビューションルール

1. フィールドの追加・変更は**先に** `shared/contract.md` と `shared/operation.schema.json` を修正し、チームの合意を取る
2. 各モジュールで独自にフィールドを追加しない
3. マニュアル・動画の文案を生成時に自由に書き換えない（`normalized_steps.json` の値をそのまま使う）

## ロードマップ

| バージョン | テーマ | 主要成果 |
|-----------|--------|---------|
| v0.1 | MVP | JSON ＋ スクリーンショット → マニュアル / 動画 |
| v0.2 | Reliability | プロジェクト管理・編集・エラー処理強化 |
| v0.3 | Capture | Playwright による操作の自動記録 |
| v0.4 | Intelligence | AI によるステップ整理・文案補完（人間レビュー前提） |
| v0.5 | Media | TTS・ハイライトアニメーション・動画テンプレート |
| v0.6 | Documents | PDF / DOCX / 社内テンプレート対応 |
| v0.7+ | Learning / Quality | 学習目標・Checkpoint、教材の自動品質検査 |

## セキュリティ

パスワード・個人情報・トークンは**そのまま保存・出力しません**。入力値は脱敏ルール（`shared/contract.md` §5）に従い、生成前に自動マスクされます。
