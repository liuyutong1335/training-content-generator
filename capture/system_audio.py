"""システム音声録音（R-02 前半）。

PyAudioWPatch の WASAPI loopback で PC 再生音をそのまま記録する。
WAV (PCM16) で書き出し、正常停止時に .tmp → リネームする。
"""

from __future__ import annotations

import os
import wave
from pathlib import Path

from core.models import RecordingConfig

from . import CaptureError


class SystemAudioRecorder:
    """WASAPI loopback によるシステム音声録音の制御。"""

    def __init__(self, config: RecordingConfig, out_dir: Path) -> None:
        self._config = config
        self._out_dir = Path(out_dir)
        self._wave: wave.Wave_write | None = None
        self._stream = None
        self._pyaudio = None
        self._final_path: Path | None = None
        self._frames_written = 0
        self._rate = 0

    @property
    def output_path(self) -> Path:
        if self._final_path is None:
            raise CaptureError("録画を開始する前に output_path は取得できません")
        return self._final_path

    def start(self) -> None:
        if self._wave is not None:
            raise CaptureError("システム音声録音は既に開始されています")
        self._out_dir.mkdir(parents=True, exist_ok=True)
        self._final_path = self._out_dir / "system_audio.wav"

        try:
            import pyaudiowpatch as pyaudio
        except Exception as exc:
            raise CaptureError("pyaudiowpatch を初期化できません") from exc

        self._pyaudio = pyaudio.PyAudio()
        try:
            device = self._find_default_loopback()
            self._rate = int(device["defaultSampleRate"])
            channels = min(int(device["maxInputChannels"]), self._config.channels)
            self._wave = wave.open(str(self._tmp_path(self._final_path)), "wb")
            self._wave.setnchannels(channels)
            self._wave.setsampwidth(2)  # PCM16
            self._wave.setframerate(self._rate)
            self._frames_written = 0

            def _callback(in_data, _frame_count, _time_info, status):  # noqa: ANN001
                self._frames_written += _frame_count
                self._wave.writeframes(in_data)
                return (None, pyaudio.paContinue)

            self._stream = self._pyaudio.open(
                format=pyaudio.paInt16,
                channels=channels,
                rate=self._rate,
                input=True,
                input_device_index=int(device["index"]),
                stream_callback=_callback,
            )
        except Exception as exc:
            self._release()
            raise CaptureError(f"システム音声の録音を開始できません: {exc}") from exc

    def stop(self) -> int:
        """録音を停止し、記録ミリ秒を返す。"""
        if self._wave is None or self._stream is None:
            raise CaptureError("システム音声録音は開始されていません")
        try:
            self._stream.stop_stream()
            self._stream.close()
            self._stream = None
            self._wave.close()
            duration_ms = int(self._frames_written / self._rate * 1000)
            os.replace(self._tmp_path(self._final_path), self._final_path)
            return duration_ms
        except Exception as exc:
            raise CaptureError(f"システム音声録音の停止に失敗しました: {exc}") from exc
        finally:
            self._release()

    @staticmethod
    def _tmp_path(final: Path) -> Path:
        return final.with_name(final.name + ".tmp")

    def _find_default_loopback(self) -> dict:
        """既定出力デバイスに対応する loopback デバイスを探す。

        paWASAPI などの定数はモジュール、デバイス照会メソッドは PyAudio
        インスタンスに属するため両方を使う。
        """
        import pyaudiowpatch as pa

        wasapi = self._pyaudio.get_host_api_info_by_type(pa.paWASAPI)
        default_out = self._pyaudio.get_device_info_by_index(wasapi["defaultOutputDevice"])
        if default_out.get("isLoopbackDevice"):
            return default_out
        for loopback in self._pyaudio.get_loopback_device_info_generator():
            if default_out["name"] in loopback["name"]:
                return loopback
        raise CaptureError("システム音声用の loopback デバイスが見つかりません")

    def _release(self) -> None:
        if self._wave is not None:
            try:
                self._wave.close()  # 二重 close は無視される
            except Exception:
                pass
        self._wave = None
        if self._pyaudio is not None:
            self._pyaudio.terminate()
            self._pyaudio = None
