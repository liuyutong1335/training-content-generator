"""録画セッション制御（F-03）。

画面・システム音声・マイクの 3 ソースを同時に開始/停止し、
P-03（timestamp 統一）に従い「Recorder.start() を基準 0 とする経過ミリ秒」
で各ソースの開始ずれを RecordingManifest に記録する。
"""

from __future__ import annotations

import time
import uuid
from datetime import datetime, timezone
from pathlib import Path

from core.models import (
    RecordingConfig,
    RecordingFile,
    RecordingManifest,
    RecordingStatus,
    SourceKind,
)

from . import CaptureError
from .microphone import MicRecorder
from .screen import ScreenRecorder
from .system_audio import SystemAudioRecorder


class Recorder:
    """1 回の録画セッションを統括する。使い方は README / docstring 参照。"""

    def __init__(self, config: RecordingConfig, project_dir: Path, ffmpeg_path: str | None = None) -> None:
        self._config = config
        self._project_dir = Path(project_dir)
        self._raw_dir = self._project_dir / "raw"
        self._ffmpeg_path = ffmpeg_path
        self._sinks: dict[SourceKind, object] = {}
        self._offsets: dict[SourceKind, int] = {}
        self._start_monotonic: float | None = None
        self._started_at: datetime | None = None

    @property
    def is_recording(self) -> bool:
        return self._start_monotonic is not None

    def start(self) -> None:
        """3 ソースの録画を開始する。開始済みなら CaptureError。"""
        if self.is_recording:
            raise CaptureError("録画セッションは既に開始されています")
        self._start_monotonic = time.monotonic()
        self._started_at = datetime.now(timezone.utc)

        def _elapsed_ms() -> int:
            return int((time.monotonic() - self._start_monotonic) * 1000)

        try:
            # 画面（起動が最も遅い）を最初に開始し、ずれを offset として記録する
            sink = ScreenRecorder(self._config, self._raw_dir, self._ffmpeg_path)
            sink.start()
            self._sinks[SourceKind.SCREEN] = sink
            self._offsets[SourceKind.SCREEN] = _elapsed_ms()
            if self._config.system_audio_enabled:
                sink = SystemAudioRecorder(self._config, self._raw_dir)
                sink.start()
                self._sinks[SourceKind.SYSTEM_AUDIO] = sink
                self._offsets[SourceKind.SYSTEM_AUDIO] = _elapsed_ms()
            if self._config.mic_enabled:
                sink = MicRecorder(self._config, self._raw_dir)
                sink.start()
                self._sinks[SourceKind.MICROPHONE] = sink
                self._offsets[SourceKind.MICROPHONE] = _elapsed_ms()
        except Exception:
            self._abort()  # 一部だけ開始した状態で残留させない
            raise

    def stop(self, recording_id: str | None = None, project_id: str = "default") -> RecordingManifest:
        """録画を停止し manifest.json を書き出して返す。"""
        if not self.is_recording:
            raise CaptureError("開始されていない録画セッションを停止できません")
        status = RecordingStatus.COMPLETED
        files: list[RecordingFile] = []
        for kind in (SourceKind.SCREEN, SourceKind.SYSTEM_AUDIO, SourceKind.MICROPHONE):
            sink = self._sinks.get(kind)
            if sink is None:
                continue
            try:
                duration_ms = sink.stop()
                files.append(
                    RecordingFile(
                        kind=kind,
                        path=sink.output_path.relative_to(self._project_dir).as_posix(),
                        offset_ms=self._offsets[kind],
                        duration_ms=duration_ms,
                    )
                )
            except Exception as exc:
                status = RecordingStatus.FAILED
                files.append(
                    RecordingFile(kind=kind, path="", note=f"stop 失敗: {exc}")
                )
        duration_ms = int((time.monotonic() - self._start_monotonic) * 1000)
        started_at = self._started_at
        self._reset()
        manifest = RecordingManifest(
            project_id=project_id,
            recording_id=recording_id or uuid.uuid4().hex[:12],
            started_at=started_at,
            duration_ms=duration_ms,
            status=status,
            files=files,
        )
        self._project_dir.mkdir(parents=True, exist_ok=True)
        (self._project_dir / "manifest.json").write_text(
            manifest.model_dump_json(indent=2), encoding="utf-8"
        )
        return manifest

    def pause(self) -> None:
        """一時停止（F-03）。MVP では未対応 — v0.2 でセグメント方式として実装予定。"""
        raise NotImplementedError("一時停止は v0.2 (Recording Reliability) で対応予定です")

    def _abort(self) -> None:
        """開始に失敗したソースをすべて破棄する。"""
        for sink in self._sinks.values():
            try:
                sink.stop()
            except Exception:
                pass
        self._reset()

    def _reset(self) -> None:
        self._sinks = {}
        self._offsets = {}
        self._start_monotonic = None
        self._started_at = None
