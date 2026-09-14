"""録画デバイスの列挙（F-02）。

- 画面: Windows のモニター情報（仮想スクリーン座標）
- マイク: PortAudio (sounddevice) の入力デバイス
- システム音声: PyAudioWPatch の WASAPI loopback デバイス
"""

from __future__ import annotations

import ctypes
import ctypes.wintypes as wintypes
import sys
from dataclasses import dataclass

from core.models import ScreenArea


@dataclass(frozen=True)
class ScreenDevice:
    """モニター 1 台分の情報（仮想スクリーン座標）。"""

    index: int
    name: str
    area: ScreenArea
    primary: bool = False


@dataclass(frozen=True)
class AudioDeviceInfo:
    """音声デバイス 1 台分の情報。"""

    index: int
    name: str
    channels: int
    sample_rate: int
    is_default: bool = False


class _MONITORINFO(ctypes.Structure):
    _fields_ = [
        ("cbSize", wintypes.DWORD),
        ("rcMonitor", wintypes.RECT),
        ("rcWork", wintypes.RECT),
        ("dwFlags", wintypes.DWORD),
        ("szDevice", wintypes.WCHAR * 32),
    ]


def enumerate_screens() -> list[ScreenDevice]:
    """接続中のモニターを列挙する（Windows のみ）。非 Windows では空リスト。"""
    if sys.platform != "win32":
        return []
    user32 = ctypes.windll.user32
    monitors: list[ScreenDevice] = []

    def _on_monitor(hmon, _hdc, _rect, _lparam):  # noqa: ANN001 - ctypes callback
        info = _MONITORINFO()
        info.cbSize = ctypes.sizeof(_MONITORINFO)
        if user32.GetMonitorInfoW(hmon, ctypes.byref(info)):
            rc = info.rcMonitor
            monitors.append(
                ScreenDevice(
                    index=len(monitors),
                    name=info.szDevice,
                    area=ScreenArea(
                        offset_x=max(rc.left, 0),
                        offset_y=max(rc.top, 0),
                        width=rc.right - rc.left,
                        height=rc.bottom - rc.top,
                    ),
                    primary=bool(info.dwFlags & 1),  # MONITORINFOF_PRIMARY
                )
            )
        return True

    callback = ctypes.WINFUNCTYPE(
        wintypes.BOOL, wintypes.HMONITOR, wintypes.HDC,
        ctypes.POINTER(wintypes.RECT), wintypes.LPARAM,
    )
    user32.EnumDisplayMonitors(None, None, callback(_on_monitor), 0)
    return monitors


def enumerate_audio_inputs() -> list[AudioDeviceInfo]:
    """マイク等の入力デバイスを列挙する（sounddevice が使えない環境では空リスト）。"""
    try:
        import sounddevice as sd
    except Exception:  # OSError: PortAudio が無い等
        return []
    devices = [
        AudioDeviceInfo(
            index=i,
            name=d["name"],
            channels=d["max_input_channels"],
            sample_rate=int(d["default_samplerate"]),
            is_default=i == sd.default.device[0],
        )
        for i, d in enumerate(sd.query_devices())
        if d["max_input_channels"] > 0
    ]
    return devices


def enumerate_loopback_outputs() -> list[AudioDeviceInfo]:
    """システム音声録音用の WASAPI loopback デバイスを列挙する。"""
    try:
        import pyaudiowpatch as pyaudio
    except Exception:
        return []
    devices: list[AudioDeviceInfo] = []
    with pyaudio.PyAudio() as p:
        for i, info in enumerate(p.get_loopback_device_info_generator()):
            devices.append(
                AudioDeviceInfo(
                    index=int(info["index"]),
                    name=info["name"],
                    channels=int(info["maxInputChannels"]),
                    sample_rate=int(info["defaultSampleRate"]),
                )
            )
    return devices
