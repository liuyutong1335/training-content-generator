using System.Diagnostics;
using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Core.Models;
using TrainingContent.Core.Validation;

namespace TrainingContent.Storage;

/// <summary>
/// ローカルファイルシステム上の TrainingProject 永続化（Contract §17 Directory / §18 Path Rule / §29 Validation）。
///
/// <para>
/// Projects Root は constructor で受け取る。Production は <see cref="DefaultProjectsRoot"/>、
/// テストは temp directory を渡す。
/// </para>
/// <para>
/// ProjectsRoot の絶対パスは project.json に保存しない（Contract §18）。
/// Project directory は常に Guid から構築し、外部から任意の相対パスを受け取らない。
/// </para>
/// </summary>
public sealed class ProjectStore
{
    public const string ProjectFileName = "project.json";

    /// <summary>保存途中の write 先。置換が完了すれば残らない。</summary>
    public const string ProjectTempFileName = "project.json.tmp";

    public const string EventsFileName = "events.jsonl";

    /// <summary>録画ファイルを置く Project directory 直下の folder 名（Contract §17）。</summary>
    private const string RecordingDirectoryName = "raw";

    /// <summary>録画ファイル名（Contract §17）。</summary>
    private const string RecordingFileName = "recording.mp4";

    /// <summary>
    /// Contract §17/§18 の canonical な録画 media path。
    /// project.json の <c>RecordingInfo.MediaPath</c> には必ずこの値を保存する
    /// （絶対パスは保存禁止・区切りは <c>/</c> に統一）。
    /// </summary>
    public const string RecordingMediaPath = RecordingDirectoryName + "/" + RecordingFileName;

    /// <summary>Contract §17 の固定 directory 構造（Project directory 直下に作成する分）。</summary>
    private static readonly string[] SubDirectories =
    [
        RecordingDirectoryName,
        Path.Combine("screenshots", "original"),
        Path.Combine("screenshots", "edited"),
        "manual",
        "output",
    ];

    private readonly string _projectsRoot;

    public ProjectStore(string projectsRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectsRoot);
        _projectsRoot = Path.GetFullPath(projectsRoot);
    }

    /// <summary>Projects Root の絶対パス（読み取り用）。</summary>
    public string ProjectsRoot => _projectsRoot;

    /// <summary>
    /// Production の既定 Projects Root。Application install directory や repository 内は使わない。
    /// テスト容易性のため constructor から任意 root を指定できること。
    /// </summary>
    public static string DefaultProjectsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TrainingContentGenerator",
        "projects");

    // ---------------------------------------------------------------------
    // Create
    // ---------------------------------------------------------------------

    /// <summary>
    /// 新しい TrainingProject を作成し、Contract §17 の directory 構造と project.json を用意する。
    /// 途中で失敗した場合は作成途中の directory を rollback する（既存 Project には触れない）。
    /// </summary>
    public async Task<TrainingProject> CreateProjectAsync(
        string title,
        string? objective = null,
        string? targetAudience = null,
        IReadOnlyList<string>? prerequisites = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var now = DateTimeOffset.UtcNow;
        var project = new TrainingProject
        {
            SchemaVersion = 1,
            Id = Guid.NewGuid(),
            Title = title,
            Objective = objective,
            TargetAudience = targetAudience,
            Prerequisites = prerequisites is null ? [] : [.. prerequisites],
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Revision = 1,
            Recording = null,
            Steps = [],
            Outputs = new ProjectOutputs(),
        };

        var directory = ProjectDirectory(project.Id);

        // Guid 衝突は実質起きないが、rollback が既存 Project を消してしまう事故を構造的に防ぐ。
        if (Directory.Exists(directory))
        {
            throw new ProjectStoreException($"Project directory が既に存在します: {directory}");
        }

        try
        {
            Directory.CreateDirectory(directory);
            foreach (var sub in SubDirectories)
            {
                Directory.CreateDirectory(Path.Combine(directory, sub));
            }

            // events.jsonl は空の UTF-8 file のみ。Event append は B 担当領域（D2 では writer を作らない）。
            await File.WriteAllBytesAsync(Path.Combine(directory, EventsFileName), [], cancellationToken)
                .ConfigureAwait(false);

            await SaveProjectAsync(project, cancellationToken).ConfigureAwait(false);
            return project;
        }
        catch
        {
            TryDeleteDirectory(directory);
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // Save
    // ---------------------------------------------------------------------

    /// <summary>
    /// TrainingProject を project.json へ保存する。
    /// Validation 違反時は project.json に一切触れない。
    /// </summary>
    public async Task SaveProjectAsync(TrainingProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);

        var directory = ProjectDirectory(project.Id);
        if (!Directory.Exists(directory))
        {
            throw new ProjectStoreException($"Project directory が存在しません: {directory}");
        }

        // 1. Validate — ここで落ちれば target には手を付けていない。
        var errors = ProjectValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new ProjectStoreException(
                "Project が Contract validation に違反しています:" + Environment.NewLine +
                string.Join(Environment.NewLine, errors));
        }

        // 2. serialize — Contract §21 の直列化規則を必ず使う。独自 JsonSerializerOptions は作らない。
        var json = JsonSerializer.SerializeToUtf8Bytes(project, TrainingJson.Indented);

        // 3-5. temp へ完全に書き → flush → 置換。書込み途中で停止しても既存 project.json は無傷。
        var targetPath = Path.Combine(directory, ProjectFileName);
        var tempPath = Path.Combine(directory, ProjectTempFileName);

        var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
        try
        {
            await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }

        File.Move(tempPath, targetPath, overwrite: true);
    }

    // ---------------------------------------------------------------------
    // Load
    // ---------------------------------------------------------------------

    /// <summary>
    /// Guid から Project を読み込む。存在しなければ null。
    /// 存在するが破損・未対応 schema・validation 違反の場合は <see cref="ProjectStoreException"/>。
    /// </summary>
    public async Task<TrainingProject?> LoadProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var path = ProjectFilePath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        return await ReadProjectAsync(path, cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------------
    // List
    // ---------------------------------------------------------------------

    /// <summary>
    /// Projects Root 直下の有効 Project を <see cref="ProjectSummary"/> として列挙する。
    /// 壊れた Project は skip し、他の正常 Project の取得を妨げない。
    /// </summary>
    public async Task<IReadOnlyList<ProjectSummary>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = new List<ProjectSummary>();

        if (!Directory.Exists(_projectsRoot))
        {
            return summaries;
        }

        foreach (var directory in Directory.EnumerateDirectories(_projectsRoot))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // GUID でない directory は無視
            if (!Guid.TryParse(Path.GetFileName(directory), out _))
            {
                continue;
            }

            var path = Path.Combine(directory, ProjectFileName);

            // project.json を持たない directory は無視
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var project = await ReadProjectAsync(path, cancellationToken).ConfigureAwait(false);
                summaries.Add(ProjectSummary.From(project, relative => ArtifactExists(directory, relative)));
            }
            catch (ProjectStoreException ex)
            {
                // 壊れた 1 件で一覧全体を落とさない。ただし握り潰さず Trace で追跡可能にする。
                Trace.TraceWarning(
                    "ProjectStore.ListProjectsAsync: 破損 Project を skip しました — {0}: {1}",
                    path,
                    ex.Message);
            }
        }

        return summaries.OrderByDescending(s => s.UpdatedAtUtc).ToList();
    }

    // ---------------------------------------------------------------------
    // Rename
    // ---------------------------------------------------------------------

    /// <summary>
    /// Title を変更する。Title は教材内容の一部なので Revision を進める（Contract §16）。
    /// Directory 名（= filesystem identity）は変更しない。
    /// </summary>
    public async Task<TrainingProject> RenameProjectAsync(
        Guid id,
        string newTitle,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newTitle);

        var project = await LoadProjectAsync(id, cancellationToken).ConfigureAwait(false)
            ?? throw new ProjectStoreException($"Project が見つかりません: {id:D}");

        project.Title = newTitle;
        project.Revision++;
        project.UpdatedAtUtc = DateTimeOffset.UtcNow;

        await SaveProjectAsync(project, cancellationToken).ConfigureAwait(false);
        return project;
    }

    // ---------------------------------------------------------------------
    // Delete
    // ---------------------------------------------------------------------

    /// <summary>
    /// 指定 Project の directory のみを再帰削除する。
    /// 削除対象は必ず Guid から構築し、ProjectsRoot の外へは決して及ばない。
    /// </summary>
    /// <returns>削除したら true、存在しなければ false。</returns>
    public Task<bool> DeleteProjectAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var directory = ProjectDirectory(id);
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(false);
        }

        Directory.Delete(directory, recursive: true);
        return Task.FromResult(true);
    }

    // ---------------------------------------------------------------------
    // Path safety
    // ---------------------------------------------------------------------

    /// <summary>
    /// Project directory を Guid から構築する。外部由来の path 文字列は一切受け取らない。
    /// </summary>
    private string ProjectDirectory(Guid id)
    {
        var full = Path.GetFullPath(Path.Combine(_projectsRoot, id.ToString("D")));

        // 防御的確認: 構築結果が必ず ProjectsRoot 配下であること。
        var rootPrefix = _projectsRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _projectsRoot
            : _projectsRoot + Path.DirectorySeparatorChar;

        if (!full.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectStoreException($"Project path が ProjectsRoot の外を指しています: {full}");
        }

        return full;
    }

    private string ProjectFilePath(Guid id) => Path.Combine(ProjectDirectory(id), ProjectFileName);

    /// <summary>
    /// 録画 Engine へ渡す出力先の絶対パス（Contract §17: <c>raw/recording.mp4</c>）。
    ///
    /// <para>
    /// 録画 Engine（担当A）は実ファイルを書くため Windows の絶対パスを要求するが、
    /// project.json へ保存するのは <see cref="RecordingMediaPath"/> の方である。
    /// ここで独自に path を組み立てず、ProjectsRoot 配下の保証は
    /// <see cref="ProjectDirectory"/> に委譲する。
    /// </para>
    /// </summary>
    public string GetRecordingOutputPath(Guid id) =>
        Path.Combine(ProjectDirectory(id), RecordingDirectoryName, RecordingFileName);

    /// <summary>
    /// Project 相対パス（Contract §18）を Project directory 配下に解決し、実ファイルの有無を返す。
    /// 脱出を試みる path は false。
    /// </summary>
    private static bool ArtifactExists(string projectDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (Path.IsPathRooted(relativePath) ||
            relativePath.Split('/', '\\').Any(segment => segment == ".."))
        {
            return false;
        }

        var full = Path.GetFullPath(Path.Combine(projectDirectory, relativePath));
        var prefix = projectDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? projectDirectory
            : projectDirectory + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
    }

    // ---------------------------------------------------------------------
    // Shared read path（Load と List で同一の検証を通す）
    // ---------------------------------------------------------------------

    private static async Task<TrainingProject> ReadProjectAsync(string path, CancellationToken cancellationToken)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ProjectStoreException($"project.json を読み込めません: {path}", ex);
        }

        TrainingProject? project;
        try
        {
            project = JsonSerializer.Deserialize<TrainingProject>(bytes, TrainingJson.Indented);
        }
        catch (JsonException ex)
        {
            throw new ProjectStoreException(
                $"project.json が破損しています（JSON として解釈できません）: {path}", ex);
        }

        if (project is null)
        {
            throw new ProjectStoreException($"project.json が空です: {path}");
        }

        // unsupported schemaVersion もここで検出される（Contract §23）。
        var errors = ProjectValidator.Validate(project);
        if (errors.Count > 0)
        {
            throw new ProjectStoreException(
                $"project.json が Contract validation に違反しています: {path}" + Environment.NewLine +
                string.Join(Environment.NewLine, errors));
        }

        return project;
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("ProjectStore: rollback に失敗しました — {0}: {1}", directory, ex.Message);
        }
    }
}
