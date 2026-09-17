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
- Copyright: Copyright (c) 2026 OpenSteps contributors（リポジトリ LICENSE の実文どおり。2026-09-17 時点の clone で確認）
- 利用範囲: Global Mouse/Keyboard Hook、UI Automation、Screenshot の部分移植（fork はしない）
- スパイク（`spike/operation-capture/`）と出荷コード（`src/TrainingContent.EventCapture/`）は同源。
  出荷コード基準での帰属対象は次の 7 ファイル:
  - 明示 Ported（upstream からのコピー・改変）: 5 件
    - `Hooks/NativeMethods.cs` — src/OpenSteps.Capture/NativeMethods.cs をコピー（使用する P/Invoke のみ残す）
    - `Hooks/GlobalMouseHook.cs` — src/OpenSteps.Capture/GlobalMouseHook.cs + ClickCapturedEventArgs.cs をコピー
    - `Hooks/GlobalKeyboardHook.cs` — src/OpenSteps.Capture/GlobalKeyboardHook.cs + KeyboardInputEventArgs.cs をコピー
    - `UiAutomation/UiAutomationService.cs` — src/OpenSteps.Capture/UiAutomationService.cs をコピー
    - `UiAutomation/UiElementInfo.cs` — src/OpenSteps.Core/Models/UiElementInfo.cs をコピー
  - 翻案（upstream コードを改変・転用した derivative。attribution 必要）: 2 件
    - `WindowInfo/WindowInfoService.cs` — src/OpenSteps.Capture/ActiveWindowService.cs の翻案
      （GetTitle / プロセス解決の実装を引き継ぎ、FromPoint・シェルウィンドウ フォールバック等を追加）
    - `Screenshot/ScreenshotCapture.cs` — src/OpenSteps.Capture/ScreenshotService.cs および
      DpiAwarenessService.cs の翻案（CopyFromScreen + クリック ハイライトの実装を引き継ぎ、
      仮想デスクトップ 1 モードに最小化。DPI コンテキスト切替は同リポジトリの NativeMethods パターンによる）
  - 上記のほか、設計のみを参考にした独自実装（attribution 不要扱い）: EventTimelineWriter / MasterClock /
    TextEntryAggregator / OperationCaptureSession は OpenSteps に存在しない本プロジェクト独自の実装
- 参照 Commit SHA: 8058980865ac07f261b97b7270776c486b942a16
- 改変: namespace 変更、OpenSteps.Core モデル依存の削除、キー名表記の契約合わせ、不要機能（Redaction/複数モード等）の削減

## NessStudio

- Repository: https://github.com/micilini/NessStudio
- License: MIT License
- Copyright: Copyright (c) 2026 Micilini Roll（リポジトリ LICENSE の実文どおり。2026-09-17 に raw LICENSE で確認）
- 利用範囲: アーキテクチャ参考のみ（コードは利用しない）。redistributed third-party code ではないため
  参考資料扱い（本セクションは謝辞としての記録）
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
