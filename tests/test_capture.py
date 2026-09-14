"""capture / core モデルの単体テスト（デバイス非依存の範囲）。"""

from __future__ import annotations

import json
from datetime import datetime, timezone

import pytest
from pydantic import ValidationError

from capture import CaptureError
from capture.screen import build_gdigrab_args, resolve_ffmpeg
from core.models import (
    RecordingConfig,
    RecordingFile,
    RecordingManifest,
    RecordingStatus,
    ScreenArea,
    SourceKind,
    StepMarker,
    Timeline,
)


class TestRecordingConfig:
    def test_defaults(self):
        config = RecordingConfig()
        assert config.fps == 30
        assert config.mic_enabled and config.system_audio_enabled
        assert config.screen_area is None

    def test_invalid_fps(self):
        with pytest.raises(ValidationError):
            RecordingConfig(fps=10)  # 下限 15 未満

    def test_invalid_sample_rate(self):
        with pytest.raises(ValidationError):
            RecordingConfig(sample_rate=44100)  # 8000 の倍数のみ許可


class TestScreenArea:
    def test_negative_offset_rejected(self):
        with pytest.raises(ValidationError):
            ScreenArea(offset_x=-1, offset_y=0, width=100, height=100)

    def test_zero_size_rejected(self):
        with pytest.raises(ValidationError):
            ScreenArea(width=0, height=0)


class TestGdigrabArgs:
    def test_full_desktop(self):
        args = build_gdigrab_args(RecordingConfig())
        assert args == ["-f", "gdigrab", "-framerate", "30", "-i", "desktop"]

    def test_specific_area(self):
        config = RecordingConfig(
            screen_area=ScreenArea(offset_x=1920, offset_y=0, width=1280, height=720)
        )
        args = build_gdigrab_args(config)
        assert "-offset_x" in args and "1920" in args
        assert "-video_size" in args and "1280x720" in args
        assert args[args.index("-i") + 1] == "desktop"


class TestResolveFFmpeg:
    def test_configured_path_wins(self):
        assert resolve_ffmpeg(r"C:\bin\ffmpeg.exe") == r"C:\bin\ffmpeg.exe"

    def test_raises_when_missing(self, monkeypatch):
        import sys

        monkeypatch.setattr("capture.screen.shutil.which", lambda _: None)
        monkeypatch.setitem(sys.modules, "imageio_ffmpeg", None)  # import を失敗させる
        with pytest.raises(CaptureError):
            resolve_ffmpeg(None)


class TestTimeline:
    def test_sorted_ok(self):
        timeline = Timeline(
            markers=[
                StepMarker(marker_id="m1", title="Step 1", timestamp_ms=0),
                StepMarker(marker_id="m2", title="Step 2", timestamp_ms=1500),
            ]
        )
        assert timeline.sorted_markers()[1].marker_id == "m2"

    def test_unsorted_rejected(self):
        with pytest.raises(ValidationError):
            Timeline(
                markers=[
                    StepMarker(marker_id="m1", title="Step 1", timestamp_ms=2000),
                    StepMarker(marker_id="m2", title="Step 2", timestamp_ms=1000),
                ]
            )

    def test_duplicate_id_rejected(self):
        with pytest.raises(ValidationError):
            Timeline(
                markers=[
                    StepMarker(marker_id="m1", title="Step 1", timestamp_ms=0),
                    StepMarker(marker_id="m1", title="Step 1 again", timestamp_ms=1000),
                ]
            )


class TestRecordingManifest:
    def _manifest(self, **overrides):
        base = dict(
            project_id="expense-registration",
            recording_id="r-0001",
            started_at=datetime(2026, 9, 14, 10, 0, 0, tzinfo=timezone.utc),
            duration_ms=60_000,
            files=[
                RecordingFile(kind=SourceKind.SCREEN, path="raw/screen.mp4",
                              offset_ms=120, duration_ms=59_880),
                RecordingFile(kind=SourceKind.MICROPHONE, path="raw/microphone.wav",
                              offset_ms=350, duration_ms=59_600),
            ],
        )
        base.update(overrides)
        return RecordingManifest(**base)

    def test_roundtrip_json(self):
        manifest = self._manifest()
        data = json.loads(manifest.model_dump_json())
        assert RecordingManifest(**data).duration_ms == 60_000

    def test_duplicate_source_rejected(self):
        with pytest.raises(ValidationError):
            self._manifest(
                files=[
                    RecordingFile(kind=SourceKind.MICROPHONE, path="a.wav"),
                    RecordingFile(kind=SourceKind.MICROPHONE, path="b.wav"),
                ]
            )

    def test_failed_status(self):
        manifest = self._manifest(status=RecordingStatus.FAILED)
        assert manifest.status == RecordingStatus.FAILED
