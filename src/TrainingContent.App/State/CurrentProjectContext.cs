using TrainingContent.Core.Models;

namespace TrainingContent.App.State;

/// <summary>
/// 現在 Application Session で開いている Project を保持する App 層の状態。
///
/// <para>
/// Phase 0 Shared Contract の型ではない。「ディスク上に保存された Project」(ProjectStore) と
/// 「今この Session で操作対象になっている Project」を区別するためのもの。
/// </para>
/// <para>
/// 意図的に持たないもの: TrainingProject の複製 / App 専用 DTO / filesystem path /
/// project.json への書込み / 別ファイルへの永続化。再起動後の自動復元も行わない。
/// </para>
/// </summary>
public sealed class CurrentProjectContext
{
    private TrainingProject? _currentProject;

    /// <summary>現在の Project。未 Open なら null。</summary>
    public TrainingProject? CurrentProject => _currentProject;

    /// <summary>現在の Project の Id。未 Open なら null。</summary>
    public Guid? CurrentProjectId => _currentProject?.Id;

    /// <summary>Current Project が設定・更新・解除されたときに発火する。</summary>
    public event EventHandler? CurrentProjectChanged;

    /// <summary>指定 Id が現在の Project かどうか。</summary>
    public bool IsCurrent(Guid id) => _currentProject?.Id == id;

    /// <summary>Current Project を設定する（同じ Id で更新する場合も含む）。</summary>
    public void SetCurrent(TrainingProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        _currentProject = project;
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Current Project を解除する。</summary>
    public void Clear()
    {
        if (_currentProject is null)
        {
            return;
        }

        _currentProject = null;
        CurrentProjectChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>指定 Id が現在の Project のときだけ解除する（別 Project の削除で解除しないため）。</summary>
    public void ClearIfCurrent(Guid id)
    {
        if (IsCurrent(id))
        {
            Clear();
        }
    }
}
