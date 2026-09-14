# training-content-generator

トレーニング動画・操作マニュアル生成ツール（MVP v0.1）

操作記録 JSON ＋ スクリーンショット から、操作マニュアル（Markdown / HTML）とトレーニング動画（MP4）を生成する。

## ディレクトリ構成

```
training-content-generator/
├─ README.md                  ← このファイル
├─ pyproject.toml             ← Python プロジェクト定義
├─ app.py                     ← 起動入口（D が担当）
├─ shared/                    ← ★共通契約（唯一の正本・全員で共有）
│  ├─ contract.md             ← データ契約書（フィールド定義・ルール）
│  ├─ operation.schema.json   ← JSON Schema
│  └─ sample_operation.json   ← 標準デモデータ（全モジュール共通で使用）
├─ recorder/                  ← A：操作記録の読込・正規化・検証・脱敏
├─ manual_generator/          ← B：マニュアル生成（Markdown / HTML）
│  └─ templates/
├─ video_generator/           ← C：動画生成（MP4）
├─ ui/                        ← D：Web UI・API・統合
├─ tests/                     ← 各モジュールのテスト
├─ input/                     ← 入力（operation.json ＋ screenshots/）
│  └─ screenshots/
└─ output/                    ← 生成物（manual.md / manual.html / training_video.mp4）
```

## データフロー（MVP）

```
input/operation.json + input/screenshots/*.png
        ↓ (A: recorder)
workdata/normalized_steps.json   ← ★唯一の共通中間データ
        ├→ (B: manual_generator) → output/manual.md / manual.html
        └→ (C: video_generator)  → output/training_video.mp4
```

**ルール：**
- マニュアルと動画は必ず同じ `normalized_steps.json` から生成する（内容の不一致を防ぐ）
- 動画の字幕はステップ JSON の `description` をそのまま使う
- フィールドを追加・変更する場合は、必ず先に `shared/contract.md` と `operation.schema.json` を修正し、全員の確認を取ってから実装する

## 分工

| 担当 | 領域 | 最終成果物 |
|------|------|-----------|
| A | 操作記録・データ標準化（`recorder/`） | `workdata/normalized_steps.json` |
| B | マニュアル生成（`manual_generator/`） | `output/manual.md` / `manual.html` |
| C | 動画生成（`video_generator/`） | `output/training_video.mp4` |
| D | UI・API・統合（`app.py` / `ui/`） | 起動入口・E2E デモ |

## セキュリティ

パスワード・個人情報・Token は**そのまま保存しない**。入力値は必ず脱敏する（詳細は `shared/contract.md`）。
