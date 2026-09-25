using System.Collections.ObjectModel;
using System.ComponentModel;
using TrainingContent.Core.Models;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>
/// Review UI の編集単位。canonical <see cref="TrainingProject"/> から作った
/// detached draft の集合と、その編集操作（並べ替え / 削除）を保持する。
///
/// <para>
/// <see cref="Steps"/> の順序が UI の表示順であり、保存時は request list の順序として
/// canonical な <see cref="TrainingStep.Order"/>（1..N）になる。canonical へは書き戻さない。
/// </para>
/// <para>
/// dirty 判定は「作成直後の内容 / 並び / 集合」との比較で行う。optional field の比較には
/// Storage と同じ正規化（whitespace-only → null）を使うため、この判定が clean なら
/// Storage 側も no-op と判定する関係になる（<c>IsSameAsRequested</c> と同じ比較軸）。
/// </para>
/// <para>
/// 意図的に持たないもの: WPF / Dispatcher 依存 / ProjectStore への参照 / 保存処理 /
/// 独自の diff アルゴリズム。保存は View が
/// <see cref="ProjectWorkspace.UpdateReviewedStepsAsync"/> を呼ぶ。
/// </para>
/// </summary>
public sealed class ReviewDraft
{
    private readonly List<Baseline> _baseline;

    /// <summary>並べ替え時の内部的な再採番で <see cref="Changed"/> が複数回出るのを抑える。</summary>
    private bool _suppressStepNotifications;

    private ReviewDraft(List<Baseline> baseline, List<ReviewDraftStep> steps)
    {
        _baseline = baseline;
        Steps = new ObservableCollection<ReviewDraftStep>(steps);

        foreach (var step in Steps)
        {
            step.PropertyChanged += OnStepPropertyChanged;
        }
    }

    /// <summary>UI 表示順の draft collection。並べ替え / 削除はこの class の操作を通す。</summary>
    public ObservableCollection<ReviewDraftStep> Steps { get; }

    /// <summary>
    /// draft の内容・並び・集合が変化したときに発火する（dirty の再評価用）。
    /// 1 つの論理操作につき 1 回だけ発火する（並べ替えに伴う表示順の再採番では重複して出さない）。
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// canonical Project から detached draft を作る。UI 順は <see cref="TrainingStep.Order"/> の
    /// 昇順で、表示順は 1..N を振り直す（canonical の Order 値は変更しない）。
    /// </summary>
    public static ReviewDraft Create(TrainingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        var ordered = project.Steps.OrderBy(step => step.Order).ToList();

        var baseline = new List<Baseline>(ordered.Count);
        var steps = new List<ReviewDraftStep>(ordered.Count);

        for (var i = 0; i < ordered.Count; i++)
        {
            var step = ordered[i];

            baseline.Add(new Baseline(
                step.Id,
                step.Title,
                Normalize(step.Description),
                Normalize(step.Caution),
                Normalize(step.ExpectedResult)));

            steps.Add(new ReviewDraftStep(step, i + 1));
        }

        return new ReviewDraft(baseline, steps);
    }

    /// <summary>作成直後と比べて実質的な変更（内容 / 並び / 集合）があるか。</summary>
    public bool IsDirty
    {
        get
        {
            if (Steps.Count != _baseline.Count)
            {
                return true;
            }

            for (var i = 0; i < Steps.Count; i++)
            {
                var step = Steps[i];
                var baseline = _baseline[i];

                if (step.StepId != baseline.StepId
                    || !string.Equals(step.Title, baseline.Title, StringComparison.Ordinal)
                    || !string.Equals(Normalize(step.Description), baseline.Description, StringComparison.Ordinal)
                    || !string.Equals(Normalize(step.Caution), baseline.Caution, StringComparison.Ordinal)
                    || !string.Equals(Normalize(step.ExpectedResult), baseline.ExpectedResult, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>Title が blank の draft があるか（Save 前の UI 側 validation 用）。</summary>
    public bool HasBlankTitle => Steps.Any(step => string.IsNullOrWhiteSpace(step.Title));

    /// <summary>最初の blank Title の StepId（該当なしは null）。Save 時に該当行へ選択を移すために使う。</summary>
    public Guid? FirstBlankTitleStepId =>
        Steps.FirstOrDefault(step => string.IsNullOrWhiteSpace(step.Title))?.StepId;

    /// <summary>
    /// Save へ渡す request。並びは現在の UI 順で、これが canonical な Order（1..N）になる。
    /// </summary>
    public IReadOnlyList<StepReviewUpdate> BuildUpdates() =>
        Steps.Select(step => step.ToUpdate()).ToList();

    /// <summary>StepId から現在の UI 位置を返す（見つからなければ -1）。</summary>
    public int IndexOf(Guid stepId)
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].StepId == stepId)
            {
                return i;
            }
        }

        return -1;
    }

    public bool CanMoveUp(int index) => index > 0 && index < Steps.Count;

    public bool CanMoveDown(int index) => index >= 0 && index < Steps.Count - 1;

    /// <summary>1 つ上へ移動する。draft collection の順序だけを変える。</summary>
    public bool MoveUp(int index)
    {
        if (!CanMoveUp(index))
        {
            return false;
        }

        Steps.Move(index, index - 1);
        RenumberAndNotify();
        return true;
    }

    /// <summary>1 つ下へ移動する。draft collection の順序だけを変える。</summary>
    public bool MoveDown(int index)
    {
        if (!CanMoveDown(index))
        {
            return false;
        }

        Steps.Move(index, index + 1);
        RenumberAndNotify();
        return true;
    }

    /// <summary>
    /// draft collection からのみ削除する（canonical は Save まで変化しない）。
    /// 最後の 1 件も削除できる（保存結果が Steps=[] になることは B1-A で valid）。
    /// </summary>
    public bool RemoveAt(int index)
    {
        if (index < 0 || index >= Steps.Count)
        {
            return false;
        }

        Steps[index].PropertyChanged -= OnStepPropertyChanged;
        Steps.RemoveAt(index);
        RenumberAndNotify();
        return true;
    }

    /// <summary>
    /// 表示順を collection の position に合わせて 1..N に振り直し、その結果を <see cref="Changed"/> で
    /// 1 回だけ通知する。<see cref="ReviewDraftStep.DisplayOrder"/> の PropertyChanged 自体は
    /// 抑止しない（UI の表示更新は通常どおり行われる）。
    /// </summary>
    private void RenumberAndNotify()
    {
        _suppressStepNotifications = true;
        try
        {
            Renumber();
        }
        finally
        {
            _suppressStepNotifications = false;
        }

        RaiseChanged();
    }

    /// <summary>表示順を collection の position に合わせて 1..N に振り直す（変化した行だけ設定する）。</summary>
    private void Renumber()
    {
        for (var i = 0; i < Steps.Count; i++)
        {
            if (Steps[i].DisplayOrder != i + 1)
            {
                Steps[i].DisplayOrder = i + 1;
            }
        }
    }

    private void OnStepPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressStepNotifications)
        {
            return;
        }

        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>null / empty / whitespace-only を canonical 側（Storage）と同じ規則で null に寄せる。</summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>作成直後の 1 行分の値（dirty 比較の基準）。</summary>
    private readonly record struct Baseline(
        Guid StepId,
        string Title,
        string? Description,
        string? Caution,
        string? ExpectedResult);
}
