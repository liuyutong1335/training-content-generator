using TrainingContent.App.Services;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// D: Shell navigation lock の決定表（録画 session × video 生成）。
///
/// <para>
/// MainWindow の実 navigation は WPF 依存のため、enable 判定だけを pure helper で固定する
/// （close guard の実際の挙動は runtime smoke で確認する）。
/// </para>
/// </summary>
public class ShellNavigationPolicyTests
{
    [Fact]
    public void 両方_idle_なら_全_navigation_が有効()
    {
        var state = ShellNavigationPolicy.Resolve(recordingActive: false, videoGenerating: false);

        Assert.True(state.IsHomeEnabled);
        Assert.True(state.IsRecordingEnabled);
        Assert.True(state.IsReviewEnabled);
        Assert.True(state.IsContentsEnabled);
        Assert.False(state.ForceRecordingPage);
    }

    [Fact]
    public void 録画中は_Recording_だけ有効で_Recording_へ寄せる()
    {
        var state = ShellNavigationPolicy.Resolve(recordingActive: true, videoGenerating: false);

        Assert.False(state.IsHomeEnabled);
        Assert.True(state.IsRecordingEnabled);
        Assert.False(state.IsReviewEnabled);
        Assert.False(state.IsContentsEnabled);
        Assert.True(state.ForceRecordingPage);
    }

    [Fact]
    public void 生成中は_Contents_だけ有効で_page_は動かさない()
    {
        var state = ShellNavigationPolicy.Resolve(recordingActive: false, videoGenerating: true);

        Assert.False(state.IsHomeEnabled);
        Assert.False(state.IsRecordingEnabled);
        Assert.False(state.IsReviewEnabled);
        Assert.True(state.IsContentsEnabled);
        Assert.False(state.ForceRecordingPage);
    }

    [Fact]
    public void 両方が同時なら_安全側で全_navigation_を塞ぎ_page_も動かさない()
    {
        var state = ShellNavigationPolicy.Resolve(recordingActive: true, videoGenerating: true);

        Assert.False(state.IsHomeEnabled);
        Assert.False(state.IsRecordingEnabled);
        Assert.False(state.IsReviewEnabled);
        Assert.False(state.IsContentsEnabled);
        Assert.False(state.ForceRecordingPage);
    }
}
