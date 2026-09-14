"""共有データモデル（Phase 1 契約ドラフト）。

開発計画書 §5.1 Training Project に対応する Pydantic モデル。
§8.1 の共同管理対象のため、**変更は必ず 4 名の合議を経ること**。
独自フィールドの追加は禁止。timestamp は P-03 に従い
「録画開始からの経過ミリ秒（int, offset_ms / timestamp_ms）」で統一する。
"""

from __future__ import annotations

from datetime import datetime
from enum import Enum
from typing import Optional

from pydantic import BaseModel, Field, NonNegativeInt, field_validator, model_validator

# 契約ドラフト版。4 名合議後に確定版を採番する。
SCHEMA_VERSION = "0.1.0-draft"


class SourceKind(str, Enum):
    """録画ソースの種別（Raw Media の識別に使用）。"""

    SCREEN = "screen"
    SYSTEM_AUDIO = "system_audio"
    MICROPHONE = "microphone"


class RecordingStatus(str, Enum):
    """録画セッションの終了状態。"""

    COMPLETED = "completed"
    FAILED = "failed"


class ScreenArea(BaseModel):
    """録画領域（仮想スクリーン座標・ピクセル）。

    None で指定した場合はデスクトップ全体（仮想スクリーン全体）を意味する。
    """

    offset_x: NonNegativeInt = 0
    offset_y: NonNegativeInt = 0
    width: int = Field(gt=0)
    height: int = Field(gt=0)


class RecordingConfig(BaseModel):
    """録画設定（F-02 録画デバイス設定の成果物）。"""

    fps: int = Field(default=30, ge=15, le=60)
    screen_area: Optional[ScreenArea] = None
    sample_rate: int = Field(default=48000, multiple_of=8000)
    channels: int = Field(default=2, ge=1, le=2)
    mic_enabled: bool = True
    mic_device_index: Optional[int] = None  # None は既定のマイク
    system_audio_enabled: bool = True


class StepMarker(BaseModel):
    """1 操作ステップのマーカー（F-04 の成果物）。"""

    marker_id: str
    title: str = Field(min_length=1)
    description: str = ""
    caution: str = ""
    expected_result: str = ""
    timestamp_ms: NonNegativeInt  # 録画開始からの経過ミリ秒（P-03）


class Timeline(BaseModel):
    """Step Marker の順序付きリスト（F-04 / F-05 の成果物）。"""

    markers: list[StepMarker] = Field(default_factory=list)

    @field_validator("markers")
    @classmethod
    def _sorted_by_timestamp(cls, v: list[StepMarker]) -> list[StepMarker]:
        ids = [m.marker_id for m in v]
        if len(ids) != len(set(ids)):
            raise ValueError("marker_id が重複しています")
        stamps = [m.timestamp_ms for m in v]
        if stamps != sorted(stamps):
            raise ValueError("markers は timestamp_ms 昇順で保持すること")
        return v

    def sorted_markers(self) -> list[StepMarker]:
        return list(self.markers)


class RecordingFile(BaseModel):
    """Raw Media 1 トラック分の参照情報（A→B の受け渡し契約）。

    path は完成 MP4 ではなくトラック単位の原素材を指す（NessStudio 流の
    independent tracks）。B は start_offset_ms を使って master clock に揃えて mix する。
    """

    kind: SourceKind
    path: str  # プロジェクトルート相対パス
    start_offset_ms: NonNegativeInt = 0  # 録画開始（Recorder.start）からの開始ずれ
    duration_ms: NonNegativeInt = 0  # ソース個別の実記録時間
    sample_rate: Optional[int] = None  # 音声トラックのみ
    channels: Optional[int] = None  # 音声トラックのみ
    note: str = ""


class RecordingManifest(BaseModel):
    """1 回の録画セッションの成果物一覧（F-03 の成果物）。

    B（動画生成）・C（マニュアル生成）はこの manifest を介して Raw Media を参照する。
    """

    project_id: str = Field(min_length=1)
    recording_id: str = Field(min_length=1)
    started_at: datetime  # Master Session Clock の起点（実時間・tz 付き）
    duration_ms: NonNegativeInt
    fps: Optional[int] = None
    screen_resolution: Optional[str] = None  # 例: "1920x1080"（全デスクトップ時は仮想スクリーン全体）
    status: RecordingStatus = RecordingStatus.COMPLETED
    files: list[RecordingFile] = Field(default_factory=list)
    schema_version: str = SCHEMA_VERSION

    @model_validator(mode="after")
    def _check_files(self) -> "RecordingManifest":
        kinds = [f.kind for f in self.files]
        if len(kinds) != len(set(kinds)):
            raise ValueError("同一 kind の録画ファイルが複数あります")
        return self
