namespace TrainingContent.App.Services;

/// <summary>
/// 録画 session と video 生成という 2 つの activity から Shell navigation の enable 状態を決める純粋 helper。
///
/// <para>
/// <see cref="MainWindow"/> が ActivityChanged のたびに評価する。両 activity は通常 UI から同時には
/// 開始できないが、防御的に両方 true の場合は <b>navigation を全 disable</b> にして
/// 勝手な page 切り替えを行わない（安全側）。
/// </para>
/// </summary>
/// <param name="ForceRecordingPage">
/// 録画中に Recording 画面へ寄せる（既存挙動）。両 activity が同時のときは切り替えない。
/// </param>
public readonly record struct ShellNavigationState(
    bool IsHomeEnabled,
    bool IsRecordingEnabled,
    bool IsReviewEnabled,
    bool IsContentsEnabled,
    bool ForceRecordingPage);

/// <inheritdoc cref="ShellNavigationState"/>
public static class ShellNavigationPolicy
{
    public static ShellNavigationState Resolve(bool recordingActive, bool videoGenerating)
    {
        if (recordingActive && videoGenerating)
        {
            // 想定外の同時 activity。navigation を塞ぎ、page も動かさない。
            return new ShellNavigationState(
                IsHomeEnabled: false,
                IsRecordingEnabled: false,
                IsReviewEnabled: false,
                IsContentsEnabled: false,
                ForceRecordingPage: false);
        }

        if (recordingActive)
        {
            // 録画 session 中: Recording だけ。他 Project の Open/Delete をさせない。
            return new ShellNavigationState(
                IsHomeEnabled: false,
                IsRecordingEnabled: true,
                IsReviewEnabled: false,
                IsContentsEnabled: false,
                ForceRecordingPage: true);
        }

        if (videoGenerating)
        {
            // 生成中: Contents に留まらせる（cancel は Contents の Cancel button から行う）。
            return new ShellNavigationState(
                IsHomeEnabled: false,
                IsRecordingEnabled: false,
                IsReviewEnabled: false,
                IsContentsEnabled: true,
                ForceRecordingPage: false);
        }

        return new ShellNavigationState(
            IsHomeEnabled: true,
            IsRecordingEnabled: true,
            IsReviewEnabled: true,
            IsContentsEnabled: true,
            ForceRecordingPage: false);
    }
}
