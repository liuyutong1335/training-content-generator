using TrainingContent.App.Services;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// B3: <see cref="ScreenshotReplacementPolicy"/> の可否 / label 判定（U1〜U6）。
/// WPF automation は行わず、dirty / busy / selection と label の出し分けを pure helper で固定する。
/// </summary>
public class ScreenshotReplacementPolicyTests
{
    private const string EditedRelative = "screenshots/edited/step-0123456789abcdef0123456789abcdef-aabbccddeeff00112233445566778899.png";

    [Fact]
    public void U1_dirty_なら_dialog_を開かず_保存か破棄を案内する()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: true,
            isMutatingCanonical: false,
            currentScreenshotPath: EditedRelative);

        Assert.False(decision.CanReplace);
        Assert.Equal("先に手順の変更を保存または破棄してください。", decision.Guidance);
    }

    [Fact]
    public void U2_busy_なら_disabled_で_理由は出さない()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: false,
            isMutatingCanonical: true,
            currentScreenshotPath: null);

        Assert.False(decision.CanReplace);
        Assert.Null(decision.Guidance);
    }

    [Fact]
    public void U2b_busy_は_dirty_より優先される()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: true,
            isMutatingCanonical: true,
            currentScreenshotPath: null);

        Assert.False(decision.CanReplace);
        Assert.Null(decision.Guidance);
    }

    [Fact]
    public void U3_clean_で選択中なら実行できる()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: false,
            isMutatingCanonical: false,
            currentScreenshotPath: EditedRelative);

        Assert.True(decision.CanReplace);
        Assert.Null(decision.Guidance);
    }

    [Fact]
    public void U4_selection_が無ければ_disabled()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: false,
            isDirty: false,
            isMutatingCanonical: false,
            currentScreenshotPath: null);

        Assert.False(decision.CanReplace);
    }

    [Fact]
    public void U5_ScreenshotPath_が_null_なら_label_は_画像を追加()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: false,
            isMutatingCanonical: false,
            currentScreenshotPath: null);

        Assert.Equal("画像を追加", decision.ButtonLabel);
    }

    [Fact]
    public void U6_ScreenshotPath_があれば_label_は_画像を差し替え()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: false,
            isMutatingCanonical: false,
            currentScreenshotPath: EditedRelative);

        Assert.Equal("画像を差し替え", decision.ButtonLabel);
    }

    [Fact]
    public void U6b_blank_な_ScreenshotPath_も_未設定として扱う()
    {
        var decision = ScreenshotReplacementPolicy.Resolve(
            hasSelectedStep: true,
            isDirty: false,
            isMutatingCanonical: false,
            currentScreenshotPath: "   ");

        Assert.Equal("画像を追加", decision.ButtonLabel);
    }
}
