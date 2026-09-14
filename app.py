"""起動入口（現時点では担当 A の録画 Spike 検証ハーネス）。

※ 正式な PySide6 アプリ（Project/Record/Review/Generate/Contents 画面）は
   開発計画書 F-01〜F-08 どおり D 担当が実装する。このファイルは
   capture モジュールの実機検証用の最小デモであり、製品 UI ではない。

使い方:
    .venv\\Scripts\\python.exe app.py
"""

from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path

from PySide6.QtCore import Qt
from PySide6.QtWidgets import (
    QApplication,
    QLabel,
    QMainWindow,
    QPushButton,
    QTextEdit,
    QVBoxLayout,
    QWidget,
)

from core.models import RecordingConfig
from capture.recorder import Recorder

PROJECTS_DIR = Path(__file__).parent / "projects"


class SpikeWindow(QMainWindow):
    """録画 Spike 検証ウィンドウ（開始 → 停止 → manifest 表示）。"""

    def __init__(self) -> None:
        super().__init__()
        self.setWindowTitle("Training Content Generator — 録画 Spike (担当A)")
        self.resize(720, 480)
        self._recorder: Recorder | None = None

        self.status = QLabel("録画デバイス: 未確認")
        self.status.setStyleSheet("color: #666; font-size: 12px;")
        self.start_button = QPushButton("● 録画開始（画面＋システム音声＋マイク）")
        self.stop_button = QPushButton("■ 録画停止")
        self.stop_button.setEnabled(False)
        self.log = QTextEdit()
        self.log.setReadOnly(True)

        self.start_button.clicked.connect(self._start)
        self.stop_button.clicked.connect(self._stop)

        layout = QVBoxLayout()
        layout.addWidget(self.status)
        layout.addWidget(self.start_button)
        layout.addWidget(self.stop_button)
        layout.addWidget(self.log)
        container = QWidget()
        container.setLayout(layout)
        self.setCentralWidget(container)

    def _log_line(self, text: str) -> None:
        self.log.append(text)

    def _start(self) -> None:
        stamp = datetime.now(timezone.utc).strftime("%Y%m%d-%H%M%S")
        project_dir = PROJECTS_DIR / f"spike-{stamp}"
        config = RecordingConfig()
        self._recorder = Recorder(config, project_dir)
        try:
            self._recorder.start()
        except Exception as exc:
            self._log_line(f"[エラー] 録画を開始できません: {exc}")
            self._recorder = None
            return
        self.start_button.setEnabled(False)
        self.stop_button.setEnabled(True)
        self.status.setText(f"録画中… → {project_dir}")
        self._log_line(f"録画を開始しました: {project_dir}")

    def _stop(self) -> None:
        if self._recorder is None:
            return
        try:
            manifest = self._recorder.stop(project_id="spike")
        except Exception as exc:
            self._log_line(f"[エラー] 録画停止に失敗: {exc}")
            self._recorder = None
            self.start_button.setEnabled(True)
            self.stop_button.setEnabled(False)
            return
        self._log_line("")
        self._log_line(f"録画完了: {manifest.recording_id} / 状態: {manifest.status.value}")
        self._log_line(f"合計 {manifest.duration_ms} ms / fps={manifest.fps} / 解像度={manifest.screen_resolution}")
        for f in manifest.files:
            line = f"  {f.kind.value}: {f.path} (offset={f.start_offset_ms}ms, dur={f.duration_ms}ms"
            if f.sample_rate:
                line += f", {f.sample_rate}Hz/{f.channels}ch"
            line += ")"
            self._log_line(line)
        self._log_line("")
        self._log_line("manifest.json と録画ファイルは projects/ 配下に保存されました。")
        self._recorder = None
        self.start_button.setEnabled(True)
        self.stop_button.setEnabled(False)
        self.status.setText("待機中")


def main() -> None:
    app = QApplication([])
    window = SpikeWindow()
    window.show()
    app.exec()


if __name__ == "__main__":
    main()
