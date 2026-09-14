"""画面録画（R-01）。

FFmpeg の gdigrab を使い、デスクトップ全体または指定モニター領域を
MP4 (H.264) で記録する。FFmpeg バイナリは環境の PATH を優先し、
無ければ imageio-ffmpeg 同梱バイナリを使う（Phase 0 Technical Spike の
「システム音声キャプチャが環境依存」リスクへの対策）。
"""

from __future__ import annotations

import os
import shutil
import subprocess
import time
from pathlib import Path

from core.models import RecordingConfig

from . import CaptureError

_STOP_WAIT_SEC = 15  # 正常終了 ('q') の待ち秒数。超過時は kill（ファイル破損対策・計画書 §11）


def resolve_ffmpeg(configured_path: str | None = None) -> str:
    """FFmpeg バイナリのパスを解決する。設定値 > PATH > imageio-ffmpeg 同梱。"""
    if configured_path:
        return configured_path
    found = shutil.which("ffmpeg")
    if found:
        return found
    try:
        import imageio_ffmpeg

        return imageio_ffmpeg.get_ffmpeg_exe()
    except Exception as exc:  # imageio_ffmpeg 非インストール等
        raise CaptureError(
            "FFmpeg が見つかりません。PATH に追加するか `pip install imageio-ffmpeg` を実行してください。"
        ) from exc


def build_gdigrab_args(config: RecordingConfig) -> list[str]:
    """gdigrab 入力の引数を組み立てる（テスト容易性のため分離）。"""
    args = ["-f", "gdigrab", "-framerate", str(config.fps)]
    if config.screen_area is None:
        args += ["-i", "desktop"]  # 仮想スクリーン全体
    else:
        area = config.screen_area
        args += [
            "-offset_x", str(area.offset_x),
            "-offset_y", str(area.offset_y),
            "-video_size", f"{area.width}x{area.height}",
            "-i", "desktop",
        ]
    return args


class ScreenRecorder:
    """FFmpeg gdigrab による画面録画の制御。"""

    def __init__(self, config: RecordingConfig, out_dir: Path, ffmpeg_path: str | None = None) -> None:
        self._config = config
        self._out_dir = Path(out_dir)
        self._ffmpeg = resolve_ffmpeg(ffmpeg_path)
        self._proc: subprocess.Popen | None = None
        self._tmp_path: Path | None = None
        self._final_path: Path | None = None

    @property
    def output_path(self) -> Path:
        if self._final_path is None:
            raise CaptureError("録画を開始する前に output_path は取得できません")
        return self._final_path

    def start(self) -> None:
        """録画を開始する。既に開始済みなら CaptureError。"""
        if self._proc is not None:
            raise CaptureError("画面録画は既に開始されています")
        self._out_dir.mkdir(parents=True, exist_ok=True)
        self._final_path = self._out_dir / "screen.mp4"
        # 先に .tmp に書き、正常停止時にリネームする（異常終了時の破損ファイル混入防止）
        self._tmp_path = self._out_dir / "screen.mp4.tmp"
        log_path = self._out_dir / "screen.log"
        cmd = [
            self._ffmpeg, "-hide_banner", "-y",
            *build_gdigrab_args(self._config),
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "23",
            "-pix_fmt", "yuv420p",
            # .tmp は拡張子からフォーマット推定できないため明示指定する
            "-f", "mp4",
            str(self._tmp_path),
        ]
        try:
            self._log_file = log_path.open("wb")
            self._started_monotonic = time.monotonic()
            self._proc = subprocess.Popen(
                cmd, stdin=subprocess.PIPE, stdout=self._log_file, stderr=self._log_file
            )
        except OSError as exc:
            self._log_file.close()
            raise CaptureError(f"画面録画プロセスを起動できません: {exc}") from exc

    def stop(self) -> int:
        """録画を停止し、記録ミリ秒を返す。ffmpeg 異常時は CaptureError。"""
        if self._proc is None:
            raise CaptureError("画面録画は開始されていません")
        duration_ms = int((time.monotonic() - self._started_monotonic) * 1000)
        proc, self._proc = self._proc, None
        try:
            if proc.stdin:
                try:
                    proc.stdin.write(b"q")
                    proc.stdin.flush()
                except OSError:
                    pass  # ffmpeg が既に終了している場合。直後の wait/returncode で判定する
            proc.wait(timeout=_STOP_WAIT_SEC)
        except subprocess.TimeoutExpired:
            proc.kill()  # 異常終了。tmp はリネームせず破損扱いにする
            proc.wait()
            raise CaptureError("画面録画が正常終了しませんでした（強制終了）") from None
        finally:
            self._log_file.close()
        if proc.returncode != 0 or not self._tmp_path or not self._tmp_path.exists():
            raise CaptureError(f"画面録画に失敗しました (exit={proc.returncode}) 詳細は screen.log を確認してください")
        os.replace(self._tmp_path, self._final_path)  # Windows は rename の上書き不可のため replace を使う
        return duration_ms
