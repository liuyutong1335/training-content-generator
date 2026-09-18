using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary>
/// Project のライフサイクル操作の単一窓口。Create / Open / Rename / Delete と
/// <see cref="CurrentProjectContext"/> の整合をここで一元的に保つ。
///
/// <para>
/// これにより Home と Contents が同じ操作を別々に実装して、Current Project の更新を
/// 片方だけ忘れる事故を防ぐ。一覧取得（読み取り専用で Current Project に関与しない）は
/// 各 View が <see cref="ProjectStore"/> を直接使う。
/// </para>
/// <para>
/// 意図的にやらないこと: Repository pattern / UnitOfWork / EventBus / DI Container /
/// generic service framework。ProjectStore を置き換えず、その上に最小限の調整だけを置く。
/// </para>
/// </summary>
public sealed class ProjectWorkspace
{
    private readonly ProjectStore _projectStore;
    private readonly CurrentProjectContext _currentProject;

    public ProjectWorkspace(ProjectStore projectStore, CurrentProjectContext currentProject)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(currentProject);

        _projectStore = projectStore;
        _currentProject = currentProject;
    }

    /// <summary>
    /// 新規 Project を作成し、成功したら Current Project に設定する。
    /// Guid 生成・directory 作成・project.json 書込みはすべて ProjectStore の責任。
    /// </summary>
    public async Task<TrainingProject> CreateProjectAsync(
        NewProjectInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);

        var project = await _projectStore
            .CreateProjectAsync(input.Title, input.Objective, input.TargetAudience, input.Prerequisites, cancellationToken);

        _currentProject.SetCurrent(project);
        return project;
    }

    /// <summary>
    /// 保存済み Project を開き、成功したら Current Project に設定する。
    /// </summary>
    /// <returns>見つからなければ null。破損などは <see cref="ProjectStoreException"/> を投げる。</returns>
    public async Task<TrainingProject?> OpenProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var project = await _projectStore.LoadProjectAsync(id, cancellationToken);
        if (project is null)
        {
            return null;
        }

        _currentProject.SetCurrent(project);
        return project;
    }

    /// <summary>
    /// 名前を変更する。Current Project を Rename した場合は Current Project も最新化する
    /// （Home に古い Title が残らないようにするため）。
    /// Revision の増加は Storage 側の責務で、ここでは操作しない。
    /// </summary>
    public async Task<TrainingProject> RenameProjectAsync(
        Guid id,
        string newTitle,
        CancellationToken cancellationToken = default)
    {
        var renamed = await _projectStore.RenameProjectAsync(id, newTitle, cancellationToken);

        if (_currentProject.IsCurrent(id))
        {
            _currentProject.SetCurrent(renamed);
        }

        return renamed;
    }

    /// <summary>
    /// Step の ScreenshotPath を差し替える（Redaction / Screenshot replacement）。
    /// Current Project を更新した場合は Current Project も最新 snapshot へ差し替える
    /// （Home に古い Screenshot / Revision が残らないようにするため）。
    ///
    /// <para>
    /// Current Project の差し替えは <b>Storage の保存が成功した後</b>だけ行う。
    /// Storage が投げた場合はここへ到達しないため、Current Project は旧状態のまま残る。
    /// 別 Project を更新しても Current Project は上書きしない（identity guard）。
    /// </para>
    /// <para>
    /// Revision の増加と no-op 判定は Storage 側の責務で、ここでは操作しない。
    /// </para>
    /// </summary>
    public async Task<TrainingProject> UpdateStepScreenshotAsync(
        Guid id,
        Guid stepId,
        string screenshotPath,
        CancellationToken cancellationToken = default)
    {
        var updated = await _projectStore
            .UpdateStepScreenshotAsync(id, stepId, screenshotPath, cancellationToken);

        if (_currentProject.IsCurrent(id))
        {
            _currentProject.SetCurrent(updated);
        }

        return updated;
    }

    /// <summary>
    /// 削除する。Current Project を削除した場合のみ Current Project を解除する
    /// （別 Project の削除では維持）。
    /// </summary>
    public async Task<bool> DeleteProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _projectStore.DeleteProjectAsync(id, cancellationToken);

        if (deleted)
        {
            _currentProject.ClearIfCurrent(id);
        }

        return deleted;
    }
}
