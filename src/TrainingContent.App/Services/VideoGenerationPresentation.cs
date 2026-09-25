namespace TrainingContent.App.Services;

/// <summary>video 生成結果に対して UI が出す dialog の種類。</summary>
public enum VideoResultDialog
{
    /// <summary>dialog を出さない（status bar のみ）。</summary>
    None,

    /// <summary>error dialog（detail に <see cref="VideoGenerationResult.ErrorMessage"/>）。</summary>
    Error,

    /// <summary>手動 recovery が必要な失敗の warning（detail に backup directory）。</summary>
    RecoveryWarning,
}

/// <summary>
/// <see cref="VideoGenerationResult"/> を user-facing な status message と dialog 種別へ写す。
/// 実際の MessageBox 表示は View が行う（この helper は文字列と分岐だけを決める）。
/// </summary>
/// <param name="DialogDetail">
/// <see cref="VideoResultDialog.Error"/> なら error message、<see cref="VideoResultDialog.RecoveryWarning"/> なら
/// recovery directory。
/// </param>
public sealed record VideoResultPresentation(
    string StatusText,
    VideoResultDialog Dialog = VideoResultDialog.None,
    string? DialogDetail = null);

/// <inheritdoc cref="VideoResultPresentation"/>
public static class VideoGenerationPresentation
{
    public static VideoResultPresentation Describe(VideoGenerationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Status switch
        {
            VideoGenerationStatus.Generated =>
                new VideoResultPresentation("動画を生成しました。"),

            // cancel は failure ではない。error dialog も raw exception も出さない。
            VideoGenerationStatus.Cancelled =>
                new VideoResultPresentation("動画の生成をキャンセルしました。"),

            VideoGenerationStatus.ProjectNotFound =>
                new VideoResultPresentation("プロジェクトが見つかりません。"),

            VideoGenerationStatus.RecordingMissing =>
                new VideoResultPresentation("録画がありません。先に録画を作成してください。"),

            VideoGenerationStatus.StepsMissing =>
                new VideoResultPresentation("手順がありません。動画を生成するには手順の確定が必要です。"),

            VideoGenerationStatus.SourceChanged =>
                new VideoResultPresentation("生成中に内容が変更されました。最新の内容で再度生成してください。"),

            VideoGenerationStatus.ComposerUnavailable =>
                new VideoResultPresentation(
                    "動画生成に必要な FFmpeg が見つかりません。",
                    VideoResultDialog.Error,
                    result.ErrorMessage),

            // 手動 recovery が必要な失敗は status も warning も専用の文言にする（backup を隠さない）。
            _ when result.RecoveryDirectory is { } recoveryDirectory =>
                new VideoResultPresentation(
                    "動画の保存に失敗しました。復旧用バックアップを保持しています。",
                    VideoResultDialog.RecoveryWarning,
                    recoveryDirectory),

            _ => new VideoResultPresentation(
                "動画の生成に失敗しました。",
                VideoResultDialog.Error,
                result.ErrorMessage),
        };
    }
}
