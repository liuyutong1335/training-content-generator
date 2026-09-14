# 共通データ契約（contract）

- **契約バージョン**: 1.0.0
- **スキーマ正本**: `shared/operation.schema.json`
- **標準デモデータ**: `shared/sample_operation.json`
- **更新ルール**: フィールドの追加・変更は必ずこのファイルとスキーマを先に修正し、メンバー全員の確認を取ってから実装する。各モジュールで独自にフィールドを追加してはならない。

---

## 1. データフローとモジュール間入出力

```
Recorder (A)                    Manual Generator (B)           Video Generator (C)
  入力: input/operation.json      入力: workdata/normalized_steps.json
        input/screenshots/*.png   出力: output/manual.md           入力: workdata/normalized_steps.json
  出力: workdata/normalized_steps.json                                  出力: output/training_video.mp4
                     ↓
              UI (D) — 上記すべてを読み、生成ボタン・ダウンロードを提供
```

- **唯一の共通中間データ**: `workdata/normalized_steps.json`（B と C は同じファイルを読む）
- B と C が各自で文案を生成することは禁止。字幕・説明文は `normalized_steps.json` の `description` をそのまま使う

---

## 2. 入力ファイル規則

| 項目 | ルール |
|------|--------|
| 操作記録 | `input/operation.json`（UTF-8、LF 推奨） |
| スクリーンショット | `input/screenshots/step01.png` … `step{step_no:02d}.png`（PNG 固定） |
| パス区切り | コード内では `pathlib` を使用（Windows / macOS 互換） |
| 日本語ファイル名 | スクリーンショット名は英数字のみ。教材タイトル等の日本語は JSON 内の文字列のみに使用 |
| 文字コード | すべてのファイル入出力は UTF-8（BOM なし） |

---

## 3. 操作記録 JSON フィールド定義

### 3.1 トップレベル

| フィールド | 型 | 必須 | 説明 |
|-----------|----|------|------|
| `schema_version` | string | ✔ | 契約バージョン（現行 `"1.0.0"`） |
| `title` | string | ✔ | 教材名称（例: 「経費申請の登録方法」） |
| `purpose` | string | ✔ | 学習目標・操作目的 |
| `target` | string | ✔ | 対象学習者（例: 「新入社員」） |
| `prerequisites` | string | − | 事前準備（省略時は空文字として扱う） |
| `notes` | string[] | − | 全体の注意事項 |
| `steps` | Step[] | ✔ | 操作ステップ（1件以上必須） |

### 3.2 Step オブジェクト

| フィールド | 型 | 必須 | 説明 |
|-----------|----|------|------|
| `step_no` | integer | ✔ | ステップ番号。1 起点の連番（重複・欠番は不正） |
| `title` | string | ✔ | ステップタイトル |
| `action` | string | ✔ | 操作種別。`click` / `input` / `select` / `confirm` / `navigate` のいずれか |
| `target` | string | ✔ | 操作対象（画面上の要素名） |
| `input_value` | string | △ | 入力値（`action=input` の場合のみ）。**要脱敏**（後述） |
| `description` | string | ✔ | 操作説明（マニュアル本文・動画字幕にそのまま使う） |
| `screenshot` | string | − | スクリーンショットファイル名（`step01.png` 形式）。省略可だが欠落時は警告を出す |
| `caution` | string | − | このステップ固有の注意事項 |
| `expected_result` | string | − | 操作後の期待結果 |

---

## 4. 正規化後データ（normalized_steps.json）の追加規則

A が入力 JSON から出力する正規化データは、上記フィールドに加えて監査情報を持つ:

| フィールド | 説明 |
|-----------|------|
| `created_at` / `updated_at` | ISO 8601（タイムゾーン付き） |
| `source` | `"json_import"`（v0.1 では固定。v0.3 で `"playwright_recording"` を追加予定） |
| `generator_version` | recorder のバージョン（`"0.1.0"`） |

---

## 5. 脱敏（サニタイズ）ルール

入力値・説明文に以下を含む場合、**生成物にそのまま出力してはならない**:

| 種別 | 扱い |
|------|------|
| パスワード・Token・API キー | `"****"` に置換（`input_value` は空にせず必ずマスク文字を入れる） |
| 氏名・メールアドレス・電話番号 | `"○○"` または `sample-user` 等のダミー値に置換 |
| 判定方法 | v0.1 は `password` / `token` / `secret` 等のキーワード判定。AI 判定は v0.4 以降 |

---

## 6. エラー型と終了コード

| エラー型 | 意味 | 挙動 |
|---------|------|------|
| `MISSING_FIELD` | 必須フィールド欠落 | エラー表示、生成中断 |
| `INVALID_FIELD` | 型・値不正（例: `step_no` 重複、`action` 不明） | エラー表示、生成中断 |
| `STEP_ORDER_BROKEN` | `step_no` が連番でない | 自動ソートして続行可。警告を出す |
| `SCREENSHOT_NOT_FOUND` | スクリーンショットファイルが存在しない | **続行可**。手順書には代替プレースホルダ、動画ではダミー画を使用 |
| `SENSITIVE_VALUE_DETECTED` | 脱敏対象の可能性 | 警告表示、自動でマスク |

- 空ステップ（`description` が空）は `MISSING_FIELD` として扱う
- 重複操作（同一 `action` + `target` が連続）は v0.1 では警告のみ（自動統合はしない）

---

## 7. 出力物

| 成果物 | パス | 生成者 |
|--------|------|--------|
| 正規化データ | `workdata/normalized_steps.json` | A |
| マニュアル（Markdown） | `output/manual.md` | B |
| マニュアル（HTML） | `output/manual.html`（画像は `screenshots/` を相対参照） | B |
| トレーニング動画 | `output/training_video.mp4` | C |
| 代替画像 | `output/assets/placeholder.png` | C（欠落スクリーンショット用） |

- 出力ディレクトリが存在しない場合は自動作成する
- 上書きは常に許可する（確認ダイアログなし）

---

## 8. MVP でやらないこと

- Playwright による自動記録（v0.3）
- AI による文章生成（v0.4）
- TTS / PDF（v0.5 / v0.6）
- ブラウザ操作以外（デスクトップアプリ・SAP GUI）の記録
- マルチユーザ・クラウド配布・DB
