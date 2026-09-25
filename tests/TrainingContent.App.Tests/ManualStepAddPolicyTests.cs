using TrainingContent.App.Services;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// B2: <see cref="ManualStepAddPolicy"/> の可否判定（§30 の UI-level 最小確認）。
///
/// <para>
/// WPF automation は行わず、「dirty → 追加不可 / busy → 追加不可 / clean + selection → その Step の直後 /
/// clean + selection 無し → null = last の後ろ」「Recording 無し → 追加不可」を pure helper で固定する。
/// </para>
/// </summary>
public class ManualStepAddPolicyTests
{
    private static readonly Guid SelectedStepId = Guid.NewGuid();

    [Fact]
    public void clean_で選択中_Step_があれば_その_Step_を_anchor_にする()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: true,
            hasRecording: true,
            isDirty: false,
            isMutatingCanonical: false,
            selectedStepId: SelectedStepId);

        Assert.True(decision.CanAdd);
        Assert.Null(decision.Guidance);
        Assert.Equal(SelectedStepId, decision.AnchorStepId);
    }

    [Fact]
    public void clean_で_selection_が無ければ_anchor_は_null_last_の後ろ()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: true,
            hasRecording: true,
            isDirty: false,
            isMutatingCanonical: false,
            selectedStepId: null);

        Assert.True(decision.CanAdd);
        Assert.Null(decision.AnchorStepId);
    }

    [Fact]
    public void dirty_なら_追加不可で_保存か破棄を案内する()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: true,
            hasRecording: true,
            isDirty: true,
            isMutatingCanonical: false,
            selectedStepId: SelectedStepId);

        Assert.False(decision.CanAdd);
        Assert.Equal("先に手順の変更を保存または破棄してください。", decision.Guidance);
    }

    [Fact]
    public void busy_なら_追加不可で_理由は出さない()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: true,
            hasRecording: true,
            isDirty: false,
            isMutatingCanonical: true,
            selectedStepId: SelectedStepId);

        Assert.False(decision.CanAdd);
        Assert.Null(decision.Guidance);
    }

    [Fact]
    public void busy_は_dirty_より優先される()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: true,
            hasRecording: true,
            isDirty: true,
            isMutatingCanonical: true,
            selectedStepId: null);

        Assert.False(decision.CanAdd);
        Assert.Null(decision.Guidance);
    }

    [Fact]
    public void Recording_が無ければ_追加不可で_その旨を案内する()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: true,
            hasRecording: false,
            isDirty: false,
            isMutatingCanonical: false,
            selectedStepId: null);

        Assert.False(decision.CanAdd);
        Assert.Equal("録画がないため手順を追加できません。", decision.Guidance);
    }

    [Fact]
    public void Project_が無ければ_追加不可()
    {
        var decision = ManualStepAddPolicy.Resolve(
            hasProject: false,
            hasRecording: false,
            isDirty: false,
            isMutatingCanonical: false,
            selectedStepId: null);

        Assert.False(decision.CanAdd);
        Assert.NotNull(decision.Guidance);
    }
}
