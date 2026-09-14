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
- Copyright: Copyright (c) 2025 ebanez8 (リポジトリ LICENSE より。正式表記は LICENSE ファイルを確認すること)
- 利用範囲: Global Mouse/Keyboard Hook、UI Automation、Screenshot の部分移植（fork はしない）
- コピー/参考ファイル: `spike/operation-capture/` に移植
  - `Hooks/NativeMethods.cs` — src/OpenSteps.Capture/NativeMethods.cs をコピー（スパイクで使用する P/Invoke のみ残す）
  - `Hooks/GlobalMouseHook.cs` — src/OpenSteps.Capture/GlobalMouseHook.cs + ClickCapturedEventArgs.cs をコピー（SpikeClickType に置換）
  - `Hooks/GlobalKeyboardHook.cs` — src/OpenSteps.Capture/GlobalKeyboardHook.cs + KeyboardInputEventArgs.cs をコピー（特殊キー名を契約 §11.2 の表記に合わせた）
  - `UiAutomation/UiAutomationService.cs` — src/OpenSteps.Capture/UiAutomationService.cs をコピー（namespace 変更のみ）
  - `UiAutomation/UiElementInfo.cs` — src/OpenSteps.Core/Models/UiElementInfo.cs をコピー
  - `Screenshot/ScreenshotCapture.cs` — src/OpenSteps.Capture/DpiAwarenessService.cs の Per-Monitor V2 スレッド切替方式を参考
  - `WindowInfoService.cs` — src/OpenSteps.Capture/ActiveWindowService.cs を参考に最小化
  - `ScreenshotCapture.cs` — src/OpenSteps.Capture/ScreenshotService.cs を参考に最小化
- 参照 Commit SHA: 8058980865ac07f261b97b7270776c486b942a16
- 改変: namespace 変更、OpenSteps.Core モデル依存の削除、キー名表記の契約合わせ、不要機能（Redaction/複数モード等）の削減

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
