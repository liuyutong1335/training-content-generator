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

            // events.jsonl は空の UTF-8 file のみを用意する。Event の append は EventCapture（担当B）の領域。
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

        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            // 置換が成功すれば temp は残らない。
            File.Move(tempPath, targetPath, overwrite: true);
        }
        catch
        {
            // 失敗時は temp を best-effort で片付ける。既存 project.json には触れない。
            // cleanup の失敗で元の例外を上書きしない（TryDeleteFile は例外を投げない）。
            TryDeleteFile(tempPath);
            throw;
        }
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
    /// Project directory の絶対パス（Contract §17 の directory 構造を持つもの）。
    ///
    /// <para>
    /// 担当B の OperationCaptureSession のように、runtime で Project 配下へ書き込む相手へ
    /// 渡すためだけに使う。project.json へ保存してはいけない（Contract §18）。
    /// ProjectsRoot 配下であることの保証は <see cref="ProjectDirectory"/> の検査をそのまま使う。
    /// </para>
    /// </summary>
    public string GetProjectDirectory(Guid id) => ProjectDirectory(id);

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

        // Persistence preflight: validator が安全に走れる最低限の object graph を保証する。
        // ProjectValidator は Steps / SourceEventIds を非 null 前提で走査するため、先に構造不正を
        // ProjectStoreException へ変換しておく（詳細は ValidatePersistedStructure の doc 参照）。
        ValidatePersistedStructure(project, path);

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

    /// <summary>
    /// Persisted structure の preflight。読み込んだ object graph が、以降の検証・表示処理を
    /// 安全に通せる最低限の形になっていることを確認する。
    ///
    /// <para>
    /// <b><see cref="ProjectValidator"/> の代替ではない。</b> Contract / business validation は
    /// validator の責任のままにし、ここでは「validator と <see cref="ProjectSummary"/> が
    /// 非 null 前提で触る property」だけを見る（validator が例外を投げずに走れるようにするのが目的）。
    /// </para>
    /// <para>
    /// Canonical な project.json は Contract 準拠であるべきなので、null は破損の証拠として扱い
    /// <b>reject する</b>（persisted null-array policy = REJECT）。<c>null → []</c> の自動正規化は
    /// しない——破損の証拠が消え、Load が暗黙にデータ意味を変えてしまうため。読み込みが file /
    /// directory を書き換えることもない（auto repair しない）。
    /// </para>
    /// </summary>
    /// <exception cref="ProjectStoreException">構造不正を検出した場合。</exception>
    private static void ValidatePersistedStructure(TrainingProject project, string path)
    {
        if (project.Outputs is null)
        {
            throw MalformedProject(path, "outputs が null です（Required field。Contract §6.1）");
        }

        if (project.Prerequisites is null)
        {
            throw MalformedProject(path, "prerequisites が null です");
        }

        if (project.Steps is null)
        {
            throw MalformedProject(path, "steps が null です");
        }

        for (var i = 0; i < project.Steps.Count; i++)
        {
            // JSON の [null] は List<TrainingStep> に null element として入る。
            TrainingStep? step = project.Steps[i];
            if (step is null)
            {
                throw MalformedProject(path, $"steps[{i}] が null です");
            }

            if (step.SourceEventIds is null)
            {
                throw MalformedProject(path, $"steps[{i}].sourceEventIds が null です");
            }
        }

        // Directory 名は filesystem identity であり Project.Id と一致しているべき。
        // 一致しない Project は「別 Project を上書きしている」等の破損なので reject する
        // （directory rename / Id 書換えによる auto repair はしない）。
        var directory = Path.GetDirectoryName(path);
        var directoryName = directory is null ? null : Path.GetFileName(directory);

        if (directoryName is null || !Guid.TryParse(directoryName, out var directoryId))
        {
            throw MalformedProject(path, $"Project directory 名が GUID ではありません: {directoryName}");
        }

        if (directoryId != project.Id)
        {
            throw MalformedProject(
                path,
                $"Project directory の GUID と project.Id が一致しません（directory={directoryId:D} / id={project.Id:D}）");
        }
    }

    private static ProjectStoreException MalformedProject(string path, string detail) =>
        new($"project.json の構造が不正です: {path}" + Environment.NewLine + detail);

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

    /// <summary>
    /// temp file などの後始末。失敗しても例外を投げない（呼出元の元の例外を上書きしないため）。
    /// 対象は常に Project directory 配下の固定名で、外部由来の path は受け取らない。
    /// </summary>
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("ProjectStore: temp file の削除に失敗しました — {0}: {1}", path, ex.Message);
        }
    }
}
