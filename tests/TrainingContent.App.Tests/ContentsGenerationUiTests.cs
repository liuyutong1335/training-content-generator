using TrainingContent.App.Services;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// D: ContentsView の video 生成中 state（U1〜U5）。
///
/// <para>
/// WPF の automation は行わない。button / progress / cancel の可否は
/// <see cref="ContentsGenerationUiStateResolver"/>、user-facing message は
/// <see cref="VideoGenerationPresentation"/> という pure helper で固定する。
/// </para>
/// </summary>
public class ContentsGenerationUiTests
{
    // =====================================================================
    // U1〜U4 — 生成 session 中の lock / progress / cancel
    // =====================================================================

    [Fact]
    public void U1_idle_では_内容操作が可能で_progress_と_cancel_は出ない()
    {
        var state = ContentsGenerationUiStateResolver.Resolve(
            isLoading: false,
            isGenerating: false,
            isCancelRequested: false);

        Assert.True(state.IsContentMutationEnabled);
        Assert.True(state.IsGridEnabled);
        Assert.False(state.IsProgressVisible);
        Assert.False(state.IsCancelVisible);
        Assert.False(state.IsCancelEnabled);

        // Generate は precondition（選択行の Step / Duration）次第で selectable。
        Assert.True(state.IsGenerateEnabled(preconditionMet: true));
        Assert.False(state.IsGenerateEnabled(preconditionMet: false));
    }

    [Fact]
    public void U1b_読み込み中は_内容操作を止めるが_grid_は止めない()
    {
        var state = ContentsGenerationUiStateResolver.Resolve(
            isLoading: true,
            isGenerating: false,
            isCancelRequested: false);

        Assert.False(state.IsContentMutationEnabled);
        Assert.False(state.IsGenerateEnabled(preconditionMet: true));
        Assert.True(state.IsGridEnabled);
        Assert.False(state.IsProgressVisible);
    }

    [Fact]
    public void U2_生成中は_内容操作と_grid_を止め_progress_と_cancel_を出す()
    {
        var state = ContentsGenerationUiStateResolver.Resolve(
            isLoading: false,
            isGenerating: true,
            isCancelRequested: false);

        Assert.False(state.IsContentMutationEnabled);
        Assert.False(state.IsGenerateEnabled(preconditionMet: true));
        Assert.False(state.IsGridEnabled);
        Assert.True(state.IsProgressVisible);
        Assert.True(state.IsCancelVisible);
        Assert.True(state.IsCancelEnabled);
    }

    [Fact]
    public void U3_cancel_要求後は_cancel_を止めるが_lock_は維持する()
    {
        var state = ContentsGenerationUiStateResolver.Resolve(
            isLoading: false,
            isGenerating: true,
            isCancelRequested: true);

        Assert.False(state.IsCancelEnabled);
        Assert.False(state.IsContentMutationEnabled);
        Assert.False(state.IsGridEnabled);
        Assert.True(state.IsProgressVisible);
    }

    [Fact]
    public void U4_生成完了で_lock_が解除される()
    {
        var completed = ContentsGenerationUiStateResolver.Resolve(
            isLoading: false,
            isGenerating: false,
            isCancelRequested: false);

        Assert.True(completed.IsContentMutationEnabled);
        Assert.True(completed.IsGridEnabled);
        Assert.False(completed.IsCancelVisible);
    }

    // =====================================================================
    // U5 — result → user-facing message
    // =====================================================================

    [Fact]
    public void U5_cancel_は_error_ではなく_専用_message_になる()
    {
        var presentation = VideoGenerationPresentation.Describe(new VideoGenerationResult(VideoGenerationStatus.Cancelled));

        Assert.Equal("動画の生成をキャンセルしました。", presentation.StatusText);
        Assert.Equal(VideoResultDialog.None, presentation.Dialog);
        Assert.Null(presentation.DialogDetail);
    }

    [Fact]
    public void U5b_成功と既存の非_dialog_status_は_dialog_を出さない()
    {
        Assert.Equal(
            new VideoResultPresentation("動画を生成しました。"),
            VideoGenerationPresentation.Describe(new VideoGenerationResult(VideoGenerationStatus.Generated)));

        Assert.Equal(
            VideoResultDialog.None,
            VideoGenerationPresentation.Describe(new VideoGenerationResult(VideoGenerationStatus.ProjectNotFound)).Dialog);

        Assert.Equal(
            "録画がありません。先に録画を作成してください。",
            VideoGenerationPresentation.Describe(new VideoGenerationResult(VideoGenerationStatus.RecordingMissing)).StatusText);

        Assert.Equal(
            VideoResultDialog.None,
            VideoGenerationPresentation.Describe(new VideoGenerationResult(VideoGenerationStatus.StepsMissing)).Dialog);

        Assert.Equal(
            VideoResultDialog.None,
            VideoGenerationPresentation.Describe(new VideoGenerationResult(VideoGenerationStatus.SourceChanged)).Dialog);
    }

    [Fact]
    public void U5c_失敗は_error_dialog_で_detail_を渡す()
    {
        var failed = VideoGenerationPresentation.Describe(
            new VideoGenerationResult(VideoGenerationStatus.Failed, ErrorMessage: "詳細"));

        Assert.Equal("動画の生成に失敗しました。", failed.StatusText);
        Assert.Equal(VideoResultDialog.Error, failed.Dialog);
        Assert.Equal("詳細", failed.DialogDetail);

        var ffmpeg = VideoGenerationPresentation.Describe(
            new VideoGenerationResult(VideoGenerationStatus.ComposerUnavailable, ErrorMessage: "ffmpeg がありません"));

        Assert.Equal("動画生成に必要な FFmpeg が見つかりません。", ffmpeg.StatusText);
        Assert.Equal(VideoResultDialog.Error, ffmpeg.Dialog);
    }

    [Fact]
    public void U5d_recovery_が必要な失敗は_warning_dialog_で_backup_directory_を出す()
    {
        var recovery = VideoGenerationPresentation.Describe(new VideoGenerationResult(
            VideoGenerationStatus.Failed,
            ErrorMessage: "rollback に失敗",
            RecoveryDirectory: @"C:\transactions\recovery"));

        Assert.Equal("動画の保存に失敗しました。復旧用バックアップを保持しています。", recovery.StatusText);
        Assert.Equal(VideoResultDialog.RecoveryWarning, recovery.Dialog);
        Assert.Equal(@"C:\transactions\recovery", recovery.DialogDetail);
    }
}
