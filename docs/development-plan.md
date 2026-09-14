# 研修コンテンツ作成ツール 開発計画書

- **文書バージョン**: 0.1
- **作成日**: 2026-09-14
- **対象範囲**: MVP ～ v1.0
- **開発体制**: 4 名
- **位置づけ**: 本リポジトリの正計画書。README は本書の要約。

---

## 1. プロジェクト概要

| 項目 | 内容 |
|------|------|
| プロジェクト名 | Training Content Generator |
| 目的 | デスクトップ上の操作・音声・入力テキストを記録し、トレーニング動画と操作マニュアルを生成・管理する |
| 対象環境（MVP） | Windows デスクトップ |
| 主要成果物 | トレーニング動画（MP4）、操作マニュアル（Markdown / HTML）、コンテンツ管理画面 |
| 基本方針 | 録画素材・テキスト・タイムライン情報を共通プロジェクトデータとして保持し、動画とマニュアルの双方から利用する |

**重要方針**: MVP の最優先は「全画面 + システム音声 + マイク音声」の安定録画。録画技術検証（Technical Spike）を本開発より先に実施する。

## 2. 要件・スコープ

### 2.1 必須要件

| ID | 必須要件 | MVP 実装方針 |
|----|---------|-------------|
| R-01 | 画面録画 | デスクトップ全体／任意アプリケーションを対象に録画する |
| R-02 | 音声録画 | システム音声とマイク音声の双方を録音する |
| R-03 | テキスト入力 | 教材情報・補足説明・ステップ説明を入力可能とする |
| R-04 | トレーニング動画生成 | R-01～R-03 の情報を統合し MP4 を生成する |
| R-05 | 操作マニュアル作成 | 共通プロジェクトデータから Markdown / HTML マニュアルを生成する |
| R-06 | コンテンツ管理画面 | 作成済み教材の一覧・閲覧・再生成・削除・出力を行う |

### 2.2 あれば嬉しい機能（MVP 後）

| ID | 機能 | 予定バージョン |
|----|------|--------------|
| E-01 | 無駄な区間の自動削除による動画短縮 | v0.3 |
| E-02 | 録画音声の機械音声化（STT → 確認 → TTS） | v0.5 |
| E-03 | 入力テキストの機械音声化（TTS） | v0.4 |

### 2.3 MVP 対象外

| 対象外項目 | 理由 |
|-----------|------|
| AI による操作ステップ自動認識 | 録画基盤・タイムライン確立後に追加する |
| 複雑な動画編集タイムライン | MVP は自動生成を優先する |
| クラウド配信／多ユーザー権限 | ローカル個人利用の検証後に判断する |
| SAP 専用自動操作 | 任意デスクトップ操作の録画を優先する |
| PDF / DOCX 出力 | HTML / Markdown の安定後に追加可能 |

## 3. システム構成・設計方針

### 3.1 論理構成

```
Desktop Operation
  ├─ Screen Capture
  ├─ System Audio
  ├─ Microphone
  └─ Text / Step Marker
          │
          ▼
   Training Project
  ├─ Raw Media
  ├─ Timeline / Markers
  ├─ Metadata
  └─ Extracted Frames
          │
      ┌───┴───────────┐
      ▼               ▼
 Video Generator   Manual Generator
      │               │
      └──────┬────────┘
             ▼
      Content Manager
```

### 3.2 設計原則

| ID | 原則 | 内容 |
|----|------|------|
| P-01 | Single Source of Truth | 動画・マニュアルは共通プロジェクトデータを参照する |
| P-02 | Raw / Work / Output 分離 | 原素材、加工データ、成果物をディレクトリで分離する |
| P-03 | Timestamp 統一 | 録画開始からの経過時間を共通基準とし、秒またはミリ秒に統一する |
| P-04 | Deterministic Generation | MVP の成果物生成はルールベースで再現可能とする |
| P-05 | Desktop First | 全アプリ・システム音声要件のため、Web UI ではなくデスクトップアプリを採用する |
| P-06 | Manual 生成方式 | 完成 MP4 の再解析は必須とせず、同一プロジェクトデータを使用する |

## 4. 技術構成

| 領域 | 採用候補 | 用途／備考 |
|------|---------|-----------|
| Desktop UI | PySide6 | 新規作成、録画、レビュー、生成、管理画面 |
| 画面・音声録画 | FFmpeg + Windows Capture / WASAPI | デスクトップ、システム音声、マイク録音。Technical Spike で確定 |
| 音動画処理 | FFmpeg | 音声ミックス、タイトル・字幕合成、MP4 出力 |
| 画像処理 | Pillow / OpenCV | フレーム抽出、縮尺調整、画像処理 |
| データモデル | Pydantic | Project / Timeline / Marker の型定義・バリデーション |
| マニュアル生成 | Jinja2 | Markdown / HTML テンプレート |
| データ管理 | SQLite | プロジェクト一覧、状態、成果物パスの管理 |
| テスト | pytest | 単体・結合・E2E |
| 配布 | PyInstaller（MVP 後） | Windows 実行形式へのパッケージング |

**技術選定確定条件**: 録画方式は 30～60 秒および 5 分程度の実機検証で、画面・2 系統音声・同期・停止処理を確認してから確定する。

## 5. データ・ディレクトリ設計

### 5.1 Training Project

| データ | 主な内容 |
|--------|---------|
| Project Metadata | project_id、title、objective、target_audience、created_at、status |
| Recording | 画面録画、システム音声、マイク音声、録画設定 |
| Timeline | 録画開始基準の timestamp、Step Marker |
| Step Marker | title、description、caution、expected_result、timestamp |
| Extracted Frames | 各 Step Marker に対応する代表フレーム |
| Outputs | training_video.mp4、manual.md、manual.html |

### 5.2 推奨ディレクトリ構成

```
training-content-generator/
├─ README.md
├─ pyproject.toml
├─ app.py
├─ core/
│  ├─ models.py
│  ├─ project.py
│  ├─ timeline.py
│  └─ config.py
├─ capture/
│  ├─ screen.py
│  ├─ system_audio.py
│  ├─ microphone.py
│  ├─ recorder.py
│  └─ devices.py
├─ video_generator/
│  ├─ storyboard.py
│  ├─ renderer.py
│  ├─ audio.py
│  └─ subtitle.py
├─ manual_generator/
│  ├─ generator.py
│  ├─ frame_extractor.py
│  └─ templates/
├─ storage/
│  ├─ database.py
│  ├─ repository.py
│  └─ files.py
├─ ui/
│  ├─ main_window.py
│  ├─ project_page.py
│  ├─ record_page.py
│  ├─ review_page.py
│  ├─ generate_page.py
│  └─ contents_page.py
├─ tests/
└─ projects/
```

## 6. 機能計画

| 機能ID | 機能 | 主要内容 | 主成果物 |
|--------|------|---------|---------|
| F-01 | Project 管理 | 新規作成、保存、再オープン、状態管理 | project.json / DB |
| F-02 | 録画デバイス設定 | ディスプレイ、システム音声、マイク選択 | RecordingConfig |
| F-03 | 録画制御 | 開始、停止、必要に応じ一時停止 | Raw Media |
| F-04 | テキスト／Step Marker | 録画中・レビュー時に説明と timestamp を登録 | Timeline / Markers |
| F-05 | レビュー | 動画確認、Step 追加・削除・修正、フレーム再取得 | Reviewed Project |
| F-06 | 動画生成 | 音声ミックス、タイトル、字幕、終了画面、MP4 出力 | training_video.mp4 |
| F-07 | マニュアル生成 | Step、代表画像、注意事項、期待結果をテンプレート化 | manual.md / html |
| F-08 | コンテンツ管理 | 一覧、閲覧、再生成、削除、出力 | Content Manager |

### 6.1 MVP 画面構成

| 画面 | 主要機能 |
|------|---------|
| Project | 教材名称、学習目標、対象者の入力／既存プロジェクト選択 |
| Record | 録画デバイス選択、録画開始・停止、Step Marker 追加 |
| Review | 録画プレビュー、Step 編集、代表フレーム確認 |
| Generate | 動画／マニュアル生成、進捗・エラー表示 |
| Contents | 作成済み教材一覧、閲覧、再生成、削除、出力 |

## 7. 開発フェーズ・マイルストーン

| Phase | 期間目安 | 内容 | 完了条件 |
|-------|---------|------|---------|
| Phase 0 | 0.5～1日 | Technical Spike：画面 + システム音声 + マイク同時録画 | 30～60秒／5分の録画・再生・同期確認 |
| Phase 1 | 0.5日 | Project / Timeline / Marker 契約確定、骨格作成 | 共有モデル・インターフェース確定 |
| Phase 2 | 2～3日 | 4担当の並行実装 | 各モジュール単体テスト通過 |
| Phase 3 | 1～2日 | UI・録画・生成・管理の統合 | 主要フロー結合テスト通過 |
| Phase 4 | 1日 | E2E・不具合修正・README 整備 | Definition of Done を全項目満たす |

**MVP 初期見積もり**: 4 名体制で約 5～8 営業日。Phase 0 の結果により録画方式・工数を見直す。

### 7.1 開発優先順位

| 優先 | 対象 |
|------|------|
| P0 | 録画不能、ファイル破損、E2E 不通 |
| P1 | 音画同期、成果物内容不一致、データ破損 |
| P2 | 入力チェック、エラー表示、再生成・削除の不整合 |
| P3 | UI 装飾、アニメーション、非必須改善 |

## 8. 4名の担当分担

| 担当 | 所有モジュール | 主責務 | 主要成果物 |
|------|--------------|--------|-----------|
| A：Capture | `capture/` | デバイス列挙、画面録画、システム音声、マイク、開始／停止、同期 | Raw Media / Recorder API |
| B：Timeline / Video | `core/timeline.py`、`video_generator/` | Marker、timestamp、音声ミックス、字幕、MP4 生成 | training_video.mp4 |
| C：Manual | `manual_generator/` | 代表フレーム抽出、Step 整理、Markdown / HTML テンプレート | manual.md / manual.html |
| D：UI / Storage / Integration | `ui/`、`storage/`、`app.py` | Project CRUD、画面、SQLite、各モジュール呼出し、E2E | Desktop App / Content Manager |

### 8.1 共同管理対象

| 対象 | ルール |
|------|--------|
| `core/models.py` / 共有契約 | 変更前に 4 名で合意。独自フィールド追加禁止 |
| timestamp 定義 | 全モジュールで同一単位・基準を使用 |
| Sample Recording | A 完了待ちを避けるため、共通の 1～2 分録画素材を準備 |
| README / pyproject.toml | 統合担当を窓口とし、依存関係・起動手順を統一 |

## 9. テスト・受入計画

### 9.1 テストレベル

| レベル | 主な確認内容 |
|--------|-------------|
| Technical Spike | 画面・システム音声・マイク・同期・停止処理 |
| Unit | Project model、Timeline、Frame Extractor、Generator、Repository |
| Integration | Capture → Project、Project → Video、Project → Manual、UI → 各モジュール |
| E2E | 新規作成からコンテンツ管理画面での再オープンまで |

### 9.2 要件トレーサビリティ

| 要件 | 主テスト | 受入基準 |
|------|---------|---------|
| R-01 画面録画 | Capture / E2E | 任意デスクトップアプリを録画し、映像を再生できる |
| R-02 音声録画 | Capture / E2E | システム音声・マイク音声の双方が記録される |
| R-03 テキスト入力 | UI / Project | 入力内容と Step Marker が保存・再表示される |
| R-04 動画生成 | Video / E2E | MP4 が生成され、映像・音声・字幕を確認できる |
| R-05 マニュアル | Manual / E2E | Step・画像・説明を含む Markdown / HTML が生成される |
| R-06 管理画面 | UI / Storage | 一覧・閲覧・再生成・削除・出力が可能 |

### 9.3 E2E シナリオ

1. 新規教材を作成
2. 画面／システム音声／マイクを選択
3. 録画開始
4. 任意デスクトップアプリを操作
5. Step Marker と説明を入力
6. 録画終了
7. Review で Step を確認・修正
8. トレーニング動画を生成・再生
9. 操作マニュアルを生成・表示
10. Content Manager に戻る
11. 同一教材を再オープン
12. 再生成／削除を確認

## 10. MVP 完了条件（Definition of Done）

| ID | 完了条件 | 区分 |
|----|---------|------|
| D-01 | 任意デスクトップアプリを録画できる | 必須 |
| D-02 | システム音声を録音できる | 必須 |
| D-03 | マイク音声を録音できる | 必須 |
| D-04 | 画面と音声が実用上同期している | 必須 |
| D-05 | テキスト／Step Marker を保存・編集できる | 必須 |
| D-06 | MP4 を生成し正常再生できる | 必須 |
| D-07 | Markdown / HTML マニュアルを生成できる | 必須 |
| D-08 | マニュアルに Step 対応画像を表示できる | 必須 |
| D-09 | Project を保存・再オープンできる | 必須 |
| D-10 | 管理画面で一覧・閲覧・再生成・削除ができる | 必須 |
| D-11 | 主要エラーがユーザーに表示される | 必須 |
| D-12 | E2E シナリオが通過する | 必須 |

## 11. リスク管理

| リスク | 影響 | 対策 | 優先度 |
|--------|------|------|--------|
| システム音声キャプチャが環境依存 | R-02 / R-04 が成立しない | Phase 0 で WASAPI / FFmpeg を実機検証。代替方式を早期判断 | High |
| 長時間録画で音画ドリフト | 動画品質低下 | 5 分以上の検証、共通タイムベース、再エンコード時の同期補正 | High |
| 録画停止時のファイル破損 | 成果物消失 | 一時ファイル、正常終了処理、異常終了時の復旧方針 | High |
| 録画ファイル肥大化 | 容量・処理時間増大 | 録画設定上限、容量警告、不要素材削除 | Medium |
| Step が不足し手順書品質が低下 | R-05 品質低下 | MVP は手動 Marker を必須化し、AI 推定は後続 | High |
| 共有モデルの独自変更 | 統合不具合 | core 契約の変更手順を固定 | Medium |
| 機密情報が録画に含まれる | 情報漏えい | ローカル保存、出力前確認、将来マスキング機能を追加 | High |

## 12. MVP 後ロードマップ

| Version | テーマ | 主要機能 | Gate |
|---------|--------|---------|------|
| v0.2 | Recording Reliability | デバイスエラー、録画復旧、Timeline 編集、安定化 | 複数環境・複数回録画で安定 |
| v0.3 | Auto Shortening | 静止／無操作／長待機区間の検出と削除候補 | 短縮前後を比較・復元可能 |
| v0.4 | Text to TTS | 入力テキストの機械音声化、字幕同期 | 複数文の自然な音声合成 |
| v0.5 | Voice to TTS | 録画音声 → STT → 確認 → TTS → 再同期 | 認識結果を確認して置換可能 |
| v0.6 | AI Step Detection | 画面変化・イベント・音声から Step 候補を自動生成 | 手動 Marker なしでも候補生成 |
| v0.7 | AI Content Enrichment | Step タイトル、説明、注意事項、期待結果の提案 | 人による承認フローを実装 |
| v1.0 | Productization | パッケージ化、設定、品質 Gate、運用性強化 | 継続利用可能なデスクトップ製品 |

### 12.1 拡張後の最終データフロー

```
Desktop Recording
        ↓
Raw Media / Events / Text
        ↓
Normalization
        ↓
AI Step Detection / Enrichment
        ↓
Human Review
        ↓
Canonical Training Project
        ↓
Quality Gate
   ┌────┼────────┐
   ↓    ↓        ↓
 Video  Manual   TTS / Other Outputs
```

**開発順序**: 録画基盤 → 共通データ → 生成 → 管理 → 自動短縮／TTS → AI。AI 機能は録画・タイムライン・成果物生成が安定してから追加する。

---

## 確認事項と決定（2026-09-14）

- **Q1. 画面録画の対象** → **すべて対象**（ブラウザのみに限らず、デスクトップ全体・他アプリを含む）
- **Q2. 音声録画の対象** → **マイク音声と PC のシステム音声の両方**
- **Q3. マニュアルの作成方式** → **最終アウトプットが同じであればどちらでも可**（計画書どおり共通プロジェクトデータから生成する）
