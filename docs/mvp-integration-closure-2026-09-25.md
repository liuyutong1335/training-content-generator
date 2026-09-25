# MVP Integration Closure — 2026-09-25

MVP の desktop integration（担当 D + 受領した A / B の修正）が production workflow として閉じた時点の
snapshot。**この文書は記録のみ**で、product code の変更は含まない。

- baseline `feature/WPF-integration`: `8e315beb23f87d3f49045c3f124094b30ceb6b6e`
- baseline `main`: `5a5026c1cf9069b66f4f26d5276caf51dbe90c49`

---

## 1. Closure Status

| Area | Status |
| --- | --- |
| Recording / Capture | CLOSED |
| EventCapture / Timeline | CLOSED |
| StepBuilder | CLOSED |
| Review MVP | COMPLETE |
| Screenshot Redaction | CLOSED |
| Manual Generation | CLOSED |
| Video Generation | CLOSED |
| Progress / Cancel | CLOSED |
| Full E2E | PASS |
| MVP Integration Gate | CLOSED |

Review MVP の scope: 既存 Step の text edit / reorder / delete、manual Step 追加、screenshot の
replacement・import、BlackBox redaction。

---

## 2. Production Workflow

以下が production UI / storage boundary を通して E2E PASS 済み:

```
Project Create
  → Recording
  → Timeline Events（events.jsonl, seq = 物理発生順）
  → StepBuilder
  → Review（既存 Step 編集 / manual Step 追加 / screenshot 置換・import / redaction）
  → Canonical TrainingProject（project.json = SSOT）
  → Manual（manual.md + manual.html, pair transaction）
  → Training Video（output/training_video.mp4, artifact transaction）
  → stale detection（SourceRevision != Revision）
  → regenerate
  → reopen / restart（Contents から明示 Open）
```

---

## 3. Final Verification

**Automated**（code 不変のため再実行せず、直前に実施した結果を記録）:

| 対象 | 結果 |
| --- | --- |
| Video.Tests | 31 / 31 PASS |
| App.Tests | 197 / 197 PASS |
| full solution | 836 / 836 PASS |
| build | 0 errors（既知 xUnit1031 warning 1 件のみ） |

**Runtime — H Full E2E**: PASS（Project `b7032400-1ed9-4a38-bdc4-ab5a12eb92ab`）

**Runtime — Video final targeted acceptance**:

| 項目 | 結果 |
| --- | --- |
| AnalyzingInput | responsive（before: 26〜31 秒 freeze → after: progress panel 約 1 秒 / UI interaction 数十 ms） |
| early probe cancel | PASS（cancel latency 約 1.2 秒 / 結果 Cancelled） |
| early cancel の不変条件 | canonical video・metadata・Revision とも unchanged |
| ffprobe / ffmpeg zombie | none |
| normal regenerate | PASS（output playable / VideoStatus Current / ManualStatus Current） |
| transaction leftovers | 0 |

---

## 4. Important Integration Decisions

- `TrainingProject` が canonical SSOT（manual / video とも同じ reviewed Steps を入力にする）。
- raw Event と `TrainingStep` は分離する（Raw Event は教材データではない）。
- `seq` は physical occurrence order を表す（保留クリック確定と後発 Event の逆転を起こさない）。
- recording finalization は transaction boundary 経由（staging → commit → rollback）。
- Manual / Video は reviewed `TrainingProject.Steps` から生成する。
- teaching content の mutation で `Revision` を進める。
- artifact generation 自体では `Revision` を増やさない（metadata の `SourceRevision` で管理する）。
- `SourceRevision` により artifact の Current / Stale を判定する。
- `screenshots/original/` は immutable（EventCapture が録画中に取得した raw screenshot 専用）。
- user が持ち込む screenshot は import / replacement / redaction とも `screenshots/edited/` へ fresh 名で作る。
- manual Step は `Action = "manual"` / `SourceEventIds = []`。
- Video の ffprobe は async + cancellable。

---

## 5. MVP Intentional Limits

以下は defect ではなく MVP の意図的な範囲:

- manual Step を追加できるのは Recording のある Project のみ。
- manual Step の挿入には timeline 上 2ms 以上の space が必要（既存 timestamp は動かさない）。
- screenshot import の対応形式は PNG / JPG / JPEG / BMP。
- redaction は BlackBox のみ。
- F5 は MVP の specialKey 対象外（Shared Contract §11.2）。
- orphan になった edited PNG の自動 cleanup は行わない。

---

## 6. Known Non-blocking Technical Debt

解決済みではない。MVP integration gate を止めない既知事項として記録する。

- **xUnit1031 warning**: `tests/TrainingContent.EventCapture.Tests/MasterClockTests.cs` の既知 warning（B ownership）。
- **ProjectValidator**: shared validator には過去レビューで確認した defensive validation の残課題がある。
  少なくとも以下は未解決（persistence boundary では `ProjectStore` 等がより強い path / structure guard を持つ）:
  - `schemaVersion` の lower-bound の明示検査
  - `Step.Order` が必ず 1 から始まることの明示検査
  - shared path validator の leading slash / traversal 防御

  これらを「解決済み」として扱わない。

---

## 7. Evidence

repository に evidence file は追加しない。本 repository 外の local acceptance evidence:

| 内容 | 場所 / ID |
| --- | --- |
| H Full E2E project | `b7032400-1ed9-4a38-bdc4-ab5a12eb92ab` |
| B ordering recheck project | `5eec7a8e-2124-46b4-bad3-7b2ea069cff2` |
| B3 screenshot replacement project | `d04b0eab-0603-4253-a079-9cb5b976969a` |
| Video final acceptance | `%TEMP%\tcg-e2e\drive-video-final.ps1`, `%TEMP%\tcg-e2e\result-video-final.txt` |
| H E2E | `%TEMP%\tcg-e2e\drive27.ps1`, `drive28.ps1`, `result27.txt`, `result28.txt`, `result27-consistency.txt` |

※ `%TEMP%` 配下は repository artifact ではなく、Owner 実機での local acceptance evidence。

---

## 8. Final Integration SHA

closure doc commit 前の baseline:

- `feature/WPF-integration`: `8e315beb23f87d3f49045c3f124094b30ceb6b6e`
- `main`: `5a5026c1cf9069b66f4f26d5276caf51dbe90c49`

Phase 0 Shared Contract は本 closure によって変更されていない。
