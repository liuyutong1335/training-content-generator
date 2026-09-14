"""マイク録音（R-02 後半）。

sounddevice (PortAudio) の入力ストリームで説明ナレーションを記録する。
WAV (PCM16) で書き出し、正常停止時に .tmp → リネームする。
"""

from __future__ import annotations

import os
import wave
from pathlib import Path

import sounddevice as sd

from core.models import RecordingConfig

from . import CaptureError


class MicRecorder:
    """マイク入力の録音制御。"""

    def __init__(self, config: RecordingConfig, out_dir: Path) -> None:
        self._config = config
        self._out_dir = Path(out_dir)
        self._stream: sd.InputStream | None = None
        self._wave: wave.Wave_write | None = None
        self._final_path: Path | None = None
        self._frames_written = 0

    @property
    def output_path(self) -> Path:
        if self._final_path is None:
            raise CaptureError("録画を開始する前に output_path は取得できません")
        return self._final_path

    def start(self) -> None:
        if self._wave is not None:
            raise CaptureError("マイク録音は既に開始されています")
        self._out_dir.mkdir(parents=True, exist_ok=True)
        self._final_path = self._out_dir / "microphone.wav"

        try:
            self._wave = wave.open(str(self._tmp_path), "wb")
            self._wave.setnchannels(self._config.channels)
            self._wave.setsampwidth(2)  # PCM16
            self._wave.setframerate(self._config.sample_rate)
            self._frames_written = 0

            def _callback(indata, frames, _time_info, status):  # noqa: ANN001
                self._frames_written += frames
                self._wave.writeframes(bytes(indata))

            self._stream = sd.InputStream(
                samplerate=self._config.sample_rate,
                channels=self._config.channels,
                dtype="int16",
                device=self._config.mic_device_index,  # None は既定デバイス
                callback=_callback,
            )
            self._stream.start()
        except Exception as exc:
            self._release()
            raise CaptureError(f"マイク録音を開始できません: {exc}") from exc

    def stop(self) -> int:
        """録音を停止し、記録ミリ秒を返す。"""
        if self._wave is None or self._stream is None:
            raise CaptureError("マイク録音は開始されていません")
        try:
            self._stream.stop()
            self._stream.close()
            self._stream = None
            self._wave.close()
            duration_ms = int(self._frames_written / self._config.sample_rate * 1000)
            os.replace(self._tmp_path, self._final_path)
            return duration_ms
        except Exception as exc:
            raise CaptureError(f"マイク録音の停止に失敗しました: {exc}") from exc
        finally:
            self._release()

    @property
    def _tmp_path(self) -> Path:
        return self._final_path.with_name(self._final_path.name + ".tmp")

    def _release(self) -> None:
        if self._stream is not None:
            try:
                self._stream.close()
            except Exception:
                pass
            self._stream = None
        if self._wave is not None:
            try:
                self._wave.close()  # 二重 close は無視される
            except Exception:
                pass
        self._wave = None
