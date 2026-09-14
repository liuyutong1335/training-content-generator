"""capture: 録画モジュール（担当 A）。

画面・システム音声・マイクを同時録画し、同期用オフセット付きの
Raw Media と RecordingManifest を出力する（開発計画書 R-01 / R-02 / P-03）。
"""

from __future__ import annotations


class CaptureError(Exception):
    """録画系の失敗を表す基底例外（P0: 録画不能は UI まで伝播させる）。"""


__all__ = ["CaptureError"]
