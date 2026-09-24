using TrainingContent.App.Services;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// B1-B: detached draft（<see cref="ReviewDraft"/> / <see cref="ReviewDraftStep"/>）の純ロジック。
///
/// <para>
/// WPF View / Dispatcher には触れない service-level の test のみ。
/// canonical <see cref="TrainingStep"/> が draft の編集で変化しないことを主眼に置く。
/// </para>
/// </summary>
public class ReviewDraftTests
{
    private static TrainingStep MakeStep(int order, string title, string? description = null, string? caution = null, string? expected = null)
    {
        return new TrainingStep
        {
            Id = Guid.NewGuid(),
            Order = order,
            StartMs = order * 1000,
            Action = "click",
            Target = $"対象 {order}",
            Title = title,
            Description = description,
            Caution = caution,
            ExpectedResult = expected,
            ScreenshotPath = $"screenshots/original/step-{order}.png",
        };
    }

    private static TrainingProject MakeProject(params TrainingStep[] steps)
    {
        return new TrainingProject
        {
            Id = Guid.NewGuid(),
            Title = "テスト教材",
            Revision = 1,
            Steps = [.. steps],
        };
    }

    // ---------------------------------------------------------------------
    // detached copy
    // ---------------------------------------------------------------------

    [Fact]
    public void Create_は_Order昇順で_displayOrderを1から振る()
    {
        // わざと Order の順序を崩して渡す。
        var project = MakeProject(
            MakeStep(2, "2 番目"),
            MakeStep(1, "1 番目"),
            MakeStep(3, "3 番目"));

        var draft = ReviewDraft.Create(project);

        Assert.Equal(3, draft.Steps.Count);
        Assert.Equal(new[] { "1 番目", "2 番目", "3 番目" }, draft.Steps.Select(s => s.Title));
        Assert.Equal(new[] { 1, 2, 3 }, draft.Steps.Select(s => s.DisplayOrder));

        // canonical 側は並びも Order 値も変更されない（OrderBy は新しい sequence を作るだけ）。
        Assert.Equal(new[] { "2 番目", "1 番目", "3 番目" }, project.Steps.Select(s => s.Title));
        Assert.Equal(new[] { 2, 1, 3 }, project.Steps.Select(s => s.Order));
    }

    [Fact]
    public void Create_は編集対象と表示専用項目をすべてcopyする()
    {
        var step = MakeStep(1, "タイトル", "説明", "注意", "結果");
        step.EndMs = 2500;

        var draft = ReviewDraft.Create(MakeProject(step));
        var d = draft.Steps[0];

        Assert.Equal(step.Id, d.StepId);
        Assert.Equal("タイトル", d.Title);
        Assert.Equal("説明", d.Description);
        Assert.Equal("注意", d.Caution);
        Assert.Equal("結果", d.ExpectedResult);

        // 表示専用（読み取りのみ）
        Assert.Equal("click", d.Action);
        Assert.Equal(1000, d.StartMs);
        Assert.Equal(2500, d.EndMs);
        Assert.Equal("screenshots/original/step-1.png", d.ScreenshotPath);
    }

    [Fact]
    public void canonical_Step_を編集しても_draft_は変化しない()
    {
        var step = MakeStep(1, "元のタイトル");
        var draft = ReviewDraft.Create(MakeProject(step));

        step.Title = "canonical 側で変更";
        step.Description = "canonical 側の説明";

        Assert.Equal("元のタイトル", draft.Steps[0].Title);
        Assert.Equal(string.Empty, draft.Steps[0].Description);
    }

    [Fact]
    public void draft_を編集しても_canonical_Step_は変化しない()
    {
        var step = MakeStep(1, "元のタイトル", "元の説明");
        var project = MakeProject(step);
        var draft = ReviewDraft.Create(project);

        draft.Steps[0].Title = "draft 側で変更";
        draft.Steps[0].Description = "draft 側の説明";
        draft.Steps[0].Caution = "draft 側の注意";
        draft.MoveUp(0);
        draft.RemoveAt(0);

        Assert.Equal("元のタイトル", step.Title);
        Assert.Equal("元の説明", step.Description);
        Assert.Null(step.Caution);
        Assert.Equal(1, step.Order);
        Assert.Single(project.Steps);
    }

    // ---------------------------------------------------------------------
    // reorder
    // ---------------------------------------------------------------------

    [Fact]
    public void MoveUp_MoveDown_は順序と表示順を更新する()
    {
        var draft = ReviewDraft.Create(MakeProject(
            MakeStep(1, "A"), MakeStep(2, "B"), MakeStep(3, "C")));

        Assert.True(draft.MoveDown(0));

        Assert.Equal(new[] { "B", "A", "C" }, draft.Steps.Select(s => s.Title));
        Assert.Equal(new[] { 1, 2, 3 }, draft.Steps.Select(s => s.DisplayOrder));

        Assert.True(draft.MoveUp(1));

        Assert.Equal(new[] { "A", "B", "C" }, draft.Steps.Select(s => s.Title));
        Assert.Equal(new[] { 1, 2, 3 }, draft.Steps.Select(s => s.DisplayOrder));
    }

    [Fact]
    public void 並べ替えと削除をしても_canonical_の並びと_Order_は不変()
    {
        var project = MakeProject(MakeStep(1, "A"), MakeStep(2, "B"), MakeStep(3, "C"));
        var draft = ReviewDraft.Create(project);

        draft.MoveDown(0);
        draft.MoveDown(1);
        draft.RemoveAt(0);

        Assert.Equal(new[] { "A", "B", "C" }, project.Steps.Select(s => s.Title));
        Assert.Equal(new[] { 1, 2, 3 }, project.Steps.Select(s => s.Order));
        Assert.Equal(new[] { 1000L, 2000L, 3000L }, project.Steps.Select(s => s.StartMs));
    }

    [Fact]
    public void 端での_Move_は_false_を返し順序を変えない()
    {
        var draft = ReviewDraft.Create(MakeProject(MakeStep(1, "A"), MakeStep(2, "B")));

        Assert.False(draft.CanMoveUp(0));
        Assert.False(draft.MoveUp(0));
        Assert.False(draft.CanMoveDown(1));
        Assert.False(draft.MoveDown(1));

        // 範囲外 index も false（例外にしない）
        Assert.False(draft.MoveUp(-1));
        Assert.False(draft.MoveDown(99));

        Assert.Equal(new[] { "A", "B" }, draft.Steps.Select(s => s.Title));
    }

    // ---------------------------------------------------------------------
    // delete
    // ---------------------------------------------------------------------

    [Fact]
    public void RemoveAt_は_draft_からのみ削除する()
    {
        var project = MakeProject(MakeStep(1, "A"), MakeStep(2, "B"), MakeStep(3, "C"));
        var draft = ReviewDraft.Create(project);

        Assert.True(draft.RemoveAt(1));

        Assert.Equal(new[] { "A", "C" }, draft.Steps.Select(s => s.Title));
        Assert.Equal(new[] { 1, 2 }, draft.Steps.Select(s => s.DisplayOrder));
        Assert.Equal(3, project.Steps.Count);
    }

    [Fact]
    public void 最後の1件も削除でき_空のdraftになる()
    {
        var draft = ReviewDraft.Create(MakeProject(MakeStep(1, "A")));

        Assert.True(draft.RemoveAt(0));

        Assert.Empty(draft.Steps);
        Assert.Empty(draft.BuildUpdates());
        Assert.True(draft.IsDirty);
    }

    [Fact]
    public void 範囲外の_RemoveAt_は_false_を返す()
    {
        var draft = ReviewDraft.Create(MakeProject(MakeStep(1, "A")));

        Assert.False(draft.RemoveAt(-1));
        Assert.False(draft.RemoveAt(1));
        Assert.Single(draft.Steps);
    }

    // ---------------------------------------------------------------------
    // request conversion
    // ---------------------------------------------------------------------

    [Fact]
    public void BuildUpdates_は_UI順のrequestを返す()
    {
        var draft = ReviewDraft.Create(MakeProject(
            MakeStep(1, "A"), MakeStep(2, "B"), MakeStep(3, "C")));

        draft.MoveDown(0);              // B, A, C
        draft.RemoveAt(2);              // B, A
        draft.Steps[1].Title = "A 編集後";

        var updates = draft.BuildUpdates();

        Assert.Equal(2, updates.Count);
        Assert.Equal(new[] { "B", "A 編集後" }, updates.Select(u => u.Title));
        Assert.Equal(draft.Steps.Select(s => s.StepId), updates.Select(u => u.StepId));
    }

    [Fact]
    public void BuildUpdates_は編集対象4項目だけを持ち空白をそのまま渡す()
    {
        var step = MakeStep(1, "タイトル", "説明", "注意", "結果");
        var draft = ReviewDraft.Create(MakeProject(step));

        draft.Steps[0].Title = "新タイトル";
        draft.Steps[0].Description = "   ";   // 正規化は Storage の責務（ここでは素通し）
        draft.Steps[0].Caution = "";
        draft.Steps[0].ExpectedResult = "新結果";

        var update = Assert.Single(draft.BuildUpdates());

        Assert.Equal(step.Id, update.StepId);
        Assert.Equal("新タイトル", update.Title);
        Assert.Equal("   ", update.Description);
        Assert.Equal("", update.Caution);
        Assert.Equal("新結果", update.ExpectedResult);
    }

    // ---------------------------------------------------------------------
    // dirty
    // ---------------------------------------------------------------------

    [Fact]
    public void 作成直後は_clean()
    {
        var draft = ReviewDraft.Create(MakeProject(
            MakeStep(1, "A", "説明"), MakeStep(2, "B")));

        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void テキスト変更で_dirty_になり元に戻すと_clean_に戻る()
    {
        var draft = ReviewDraft.Create(MakeProject(MakeStep(1, "A", "説明")));

        draft.Steps[0].Title = "A 編集後";
        Assert.True(draft.IsDirty);

        draft.Steps[0].Title = "A";
        Assert.False(draft.IsDirty);

        draft.Steps[0].Description = "説明 編集後";
        Assert.True(draft.IsDirty);

        draft.Steps[0].Description = "説明";
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void 空白のみの入力は_null_と同じ扱いで_dirty_にしない()
    {
        // canonical が null の optional field に空白だけを入れても、Storage は null へ正規化する。
        var draft = ReviewDraft.Create(MakeProject(MakeStep(1, "A")));

        draft.Steps[0].Description = "   ";
        draft.Steps[0].Caution = "";
        draft.Steps[0].ExpectedResult = "\t";

        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void 並べ替えで_dirty_になり戻すと_clean_に戻る()
    {
        var draft = ReviewDraft.Create(MakeProject(
            MakeStep(1, "A"), MakeStep(2, "B")));

        draft.MoveDown(0);
        Assert.True(draft.IsDirty);

        draft.MoveUp(1);
        Assert.False(draft.IsDirty);
    }

    [Fact]
    public void 削除で_dirty_になり_Title変更だけでも_dirty()
    {
        var draft = ReviewDraft.Create(MakeProject(
            MakeStep(1, "A"), MakeStep(2, "B")));

        draft.RemoveAt(0);
        Assert.True(draft.IsDirty);

        var other = ReviewDraft.Create(MakeProject(MakeStep(1, "A")));
        other.Steps[0].ExpectedResult = "結果";
        Assert.True(other.IsDirty);
    }

    [Fact]
    public void 末尾空白の追加は実質変更として_dirty_にする()
    {
        // Storage は Title を trim しないため、空白の追加も保存対象になる（保守的側に倒す）。
        var draft = ReviewDraft.Create(MakeProject(MakeStep(1, "A", "説明")));

        draft.Steps[0].Title = "A ";
        Assert.True(draft.IsDirty);

        draft.Steps[0].Description = "説明 ";
        Assert.True(draft.IsDirty);
    }

    // ---------------------------------------------------------------------
    // blank title（Save 前の UI 側 validation）
    // ---------------------------------------------------------------------

    [Fact]
    public void blank_title_を検出し最初の該当_StepId_を返す()
    {
        var project = MakeProject(MakeStep(1, "A"), MakeStep(2, "B"), MakeStep(3, "C"));
        var draft = ReviewDraft.Create(project);

        Assert.False(draft.HasBlankTitle);
        Assert.Null(draft.FirstBlankTitleStepId);

        draft.Steps[2].Title = "   ";
        Assert.True(draft.HasBlankTitle);
        Assert.Equal(project.Steps[2].Id, draft.FirstBlankTitleStepId);

        draft.Steps[1].Title = "";
        Assert.Equal(project.Steps[1].Id, draft.FirstBlankTitleStepId);
    }

    // ---------------------------------------------------------------------
    // Changed 通知（View の dirty 再評価）
    // ---------------------------------------------------------------------

    [Fact]
    public void 編集_並べ替え_削除で_Changed_が発火する()
    {
        var draft = ReviewDraft.Create(MakeProject(
            MakeStep(1, "A"), MakeStep(2, "B")));

        var count = 0;
        draft.Changed += (_, _) => count++;

        draft.Steps[0].Title = "変更";
        Assert.Equal(1, count);

        draft.Steps[0].Title = "変更";        // 同値なら通知しない
        Assert.Equal(1, count);

        draft.MoveDown(0);
        Assert.Equal(2, count);

        draft.RemoveAt(0);
        Assert.Equal(3, count);

        draft.Steps[0].Description = "説明";
        Assert.Equal(4, count);
    }

    [Fact]
    public void IndexOf_は現在の_UI_位置を返す()
    {
        var project = MakeProject(MakeStep(1, "A"), MakeStep(2, "B"));
        var draft = ReviewDraft.Create(project);

        Assert.Equal(1, draft.IndexOf(project.Steps[1].Id));
        Assert.Equal(-1, draft.IndexOf(Guid.NewGuid()));

        draft.MoveDown(0);
        Assert.Equal(0, draft.IndexOf(project.Steps[1].Id));
    }
}
