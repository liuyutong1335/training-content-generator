# spike/python-recording — Python 録画 Spike（アーカイブ）

開発計画書 **v0.1**（Python + PySide6 + FFmpeg + PyAudioWPatch）時代に担当 A が実施した
Technical Spike の成果物。**v0.2（C#/.NET 8 + ScreenRecorderLib）で技術スタックが変更に
なったため、本配下はアーカイブ（参考資料）であり、今後開発しない。**

## 実績（v0.2 への設計入力として価値あり）

- 画面（FFmpeg gdigrab）＋システム音声（WASAPI loopback）＋マイクの **3 ソース同時録画**に実機で成功
- Master Session Clock 方式（`started_at` 基準 + トラックごとの `start_offset_ms` を manifest に記録）— v0.2 の `SessionManifest` 設計にそのまま踏襲される
- 実機検証で得た知見:
  - pyaudiowpatch の blocking `read()` は生 bytes を返す（`[0]` で取り出すと int になる）
  - DPI 拡張 125% 環境では `GetSystemMetrics` が仮想化値（1536x864）を返し、gdigrab 実像素（1920x1080）とずれる → DPI aware 化が必要（OpenSteps `DpiAwarenessService` の知見と一致）
  - マイクはデバイスの `max_input_channels` にチャンネル数を収めないと失敗する
  - ffmpeg 出力は `.tmp` 拡張子だとフォーマット推定に失敗する（`-f` 明示またはリネーム工夫）
- 単体テスト 19 本（モデル検証 / gdigrab 引数 / manifest ラウンドトリップ / fake sink による Recorder 試験）

## 構成

```
capture/    録画本体（screen.py / system_audio.py / microphone.py / recorder.py / devices.py）
core/       共有データモデル草案（Pydantic）→ v0.2 では C# の TrainingProject/TimelineEvent/TrainingStep へ
tests/      pytest
app.py      PySide6 最小ハーネス（録画開始/停止 + manifest 表示）
```

実行方法（再現する場合）: `pyproject.toml` をリポジトリルートに戻し `pip install -e .` → `python app.py`
