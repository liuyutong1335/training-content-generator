# THIRD_PARTY_NOTICES

開発計画書 §28 に基づき、利用・参考にした OSS とライセンスを記録する。
MIT のコードをコピー・改変した場合も、著作権表示およびライセンス表示を保持する。

## ScreenRecorderLib

- Repository: https://github.com/sskodje/ScreenRecorderLib
- License: MIT License
- Copyright: Copyright (c) 2017 Ramin Kaviani
- 利用範囲: NuGet パッケージ依存（画面録画・システム音声・マイク録音）
- コピー/参考ファイル: なし（NuGet 依存のみ）
- 参照 Commit SHA: （Spike A 実施時に v6.6.0 / v7.0.1 のタグを記録）
- 改変: なし

## OpenSteps

- Repository: https://github.com/ebanez8/openstep
- License: MIT License
- Copyright: （移植時に記録）
- 利用範囲: Global Mouse/Keyboard Hook、UI Automation、Screenshot、Redaction、Markdown/HTML Export の部分移植（fork はしない）
- コピー/参考ファイル: 移植時に記録する
- 参照 Commit SHA: （移植時に記録）
- 改変: 移植時に記録する

## NessStudio

- Repository: https://github.com/micilini/NessStudio
- License: MIT License
- Copyright: （記録）
- 利用範囲: アーキテクチャ参考のみ（コードは利用しない）
- 改変: なし

## Microsoft Skill Recorder

- Repository: https://github.com/microsoft/skill-recorder
- License: MIT License
- Copyright: Copyright (c) Microsoft Corporation
- 利用範囲: Post-MVP（v0.3 以降）の参考実装。MVP では依存しない
- 改変: なし

## 参考にしたが配布に含めないもの

- OBS Studio (GPL-2.0) / ShareX (GPL-3.0): コードをコピーしない。設計研究のみ
- 本プロジェクトの Python Spike（`spike/python-recording/`）: 自社成果物（FFmpeg は実行時に別途取得）
