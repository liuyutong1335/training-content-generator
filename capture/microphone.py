"""マイク録音（R-02 後半）。

sounddevice (PortAudio) の入力ストリームで説明ナレーションを記録する。
callback を使わず blocking read を専用スレッドで回す（system_audio.py と
同じ最構成で安定させる）。WAV (PCM16) で書き出し、正常停止時に
.tmp → os.replace リネームする。
"""

from __future__ import annotations

import os
import threading
import wave
from pathlib import Path

import sounddevice as sd

from core.models import RecordingConfig

from . import CaptureError

_CHUNK_FRAMES = 1024


class MicRecorder:
    """マイク入力の録音制御。"""

    def __init__(self, config: RecordingConfig, out_dir: Path) -> None:
        self._config = config
        self._out_dir = Path(out_dir)
        self._stream: sd.InputStream | None = None
        self._wave: wave.Wave_write | None = None
        self._final_path: Path | None = None
        self._thread: threading.Thread | None = None
        self._stop_event = threading.Event()
        self._frames_written = 0
        self._channels = self._config.channels  # start() でデバイス能力に合わせて確定する
        self.error: Exception | None = None

    @property
    def output_path(self) -> Path:
        if self._final_path is None:
            raise CaptureError("録画を開始する前に output_path は取得できません")
        return self._final_path

    @property
    def sample_rate(self) -> int:
        return self._config.sample_rate

    @property
    def channels(self) -> int:
        """実録音チャンネル数（デバイス能力に合わせて収めた値）。"""
        return self._channels

    def start(self) -> None:
        if self._wave is not None:
            raise CaptureError("マイク録音は既に開始されています")
        self._out_dir.mkdir(parents=True, exist_ok=True)
        self._final_path = self._out_dir / "microphone.wav"

        try:
            # デバイスが対応しないチャンネル数（例: モノラルマイクに 2ch を要求）は失敗するため収める
            if self._config.mic_device_index is None:
                device_info = sd.query_devices(kind="input")  # 既定の入力デバイス
            else:
                device_info = sd.query_devices(device=self._config.mic_device_index)
            max_in = int(device_info["max_input_channels"])
            self._channels = max(1, min(self._config.channels, max_in))
        except Exception as exc:
            raise CaptureError(f"マイクデバイスを解決できません: {exc}") from exc

        try:
            self._wave = wave.open(str(self._tmp_path), "wb")
            self._wave.setnchannels(self.channels)
            self._wave.setsampwidth(2)  # PCM16
            self._wave.setframerate(self.sample_rate)
            self._frames_written = 0
            self._stop_event.clear()

            self._stream = sd.InputStream(
                samplerate=self._config.sample_rate,
                channels=self.channels,
                dtype="int16",
                device=self._config.mic_device_index,  # None は既定デバイス
            )
            self._stream.start()
            self._thread = threading.Thread(target=self._record_loop, daemon=True)
            self._thread.start()
        except Exception as exc:
            self._release()
            raise CaptureError(f"マイク録音を開始できません: {exc}") from exc

    def _record_loop(self) -> None:
        """blocking read で WAV に書き続ける専用スレッド。"""
        try:
            while not self._stop_event.is_set():
                data, overflow = self._stream.read(_CHUNK_FRAMES)  # numpy (frames, channels) int16
                if overflow:
                    self.error = CaptureError("マイク入力でバッファ溢れ（音声欠落の可能性）")
                self._frames_written += len(data)
                self._wave.writeframes(bytes(data))
        except Exception as exc:  # read 失敗は stop() で検知させる
            self.error = exc

    def stop(self) -> int:
        """録音を停止し、記録ミリ秒を返す。"""
        if self._wave is None or self._stream is None:
            raise CaptureError("マイク録音は開始されていません")
        self._stop_event.set()
        if self._thread is not None:
            self._thread.join(timeout=5)
            self._thread = None
        if isinstance(self.error, CaptureError):
            raise self.error
        if self.error is not None:
            raise CaptureError(f"マイク録音中にエラー: {self.error}")
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
