using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// D2 ProjectStore のテスト。実ユーザーの Projects directory や repository の projects/ は使わず、
/// 必ず temp directory を使う。
/// </summary>
public class ProjectStoreTests
{
    /// <summary>テストごとに独立した Projects Root を temp 配下に作り、終了時に削除する。</summary>
    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; }

        public TempProjectsRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "tc-storage-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // temp の後始末失敗でテストを落とさない
            }
        }
    }

    private static string ProjectDir(TempProjectsRoot temp, Guid id) => Path.Combine(temp.Root, id.ToString("D"));

    private static string ProjectJsonPath(TempProjectsRoot temp, Guid id) =>
        Path.Combine(ProjectDir(temp, id), ProjectStore.ProjectFileName);

    // =====================================================================
    // T-D2-01 Create — Contract §17 の directory 構造
    // =====================================================================
    [Fact]
    public async Task T_D2_01_Create_InitializesDirectoryContract()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("経費申請の登録");

        var dir = ProjectDir(temp, project.Id);
        Assert.True(Directory.Exists(dir), "project directory");
        Assert.True(File.Exists(Path.Combine(dir, "project.json")), "project.json");
        Assert.True(File.Exists(Path.Combine(dir, "events.jsonl")), "events.jsonl");
        Assert.True(Directory.Exists(Path.Combine(dir, "raw")), "raw/");
        Assert.True(Directory.Exists(Path.Combine(dir, "screenshots")), "screenshots/");
        Assert.True(Directory.Exists(Path.Combine(dir, "screenshots", "original")), "screenshots/original/");
        Assert.True(Directory.Exists(Path.Combine(dir, "screenshots", "edited")), "screenshots/edited/");
        Assert.True(Directory.Exists(Path.Combine(dir, "manual")), "manual/");
        Assert.True(Directory.Exists(Path.Combine(dir, "output")), "output/");
    }

    // =====================================================================
    // T-D2-02 Create — 初期値
    // =====================================================================
    [Fact]
    public async Task T_D2_02_Create_ProducesContractInitialValues()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var before = DateTimeOffset.UtcNow;
        var project = await store.CreateProjectAsync("初期値テスト");
        var after = DateTimeOffset.UtcNow;

        Assert.Equal(1, project.SchemaVersion);
        Assert.Equal(1, project.Revision);
        Assert.Equal("初期値テスト", project.Title);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.InRange(project.CreatedAtUtc, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(project.CreatedAtUtc, project.UpdatedAtUtc);
        Assert.Empty(project.Steps);
        Assert.Null(project.Recording);
        Assert.NotNull(project.Outputs);
        Assert.Null(project.Outputs.ManualMarkdown);
        Assert.Null(project.Outputs.TrainingVideo);
    }

    // =====================================================================
    // T-D2-03 Save / Load roundtrip
    // =====================================================================
    [Fact]
    public async Task T_D2_03_SaveLoad_RoundTrips()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("往復テスト");
        project.Objective = "経費を申請できるようにする";
        project.TargetAudience = "新入社員";
        project.Prerequisites = ["経理の基礎"];
        project.Recording = new RecordingInfo
        {
            MediaPath = "raw/recording.mp4",
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 84_210,
            HasSystemAudio = true,
            HasMicrophone = false,
            DisplayId = "display-1",
        };
        project.Steps =
        [
            new TrainingStep
            {
                Id = Guid.NewGuid(),
                Order = 1,
                StartMs = 5_210,
                Action = StepActions.Click,
                Title = "「新規申請」をクリックします",
                ScreenshotPath = "screenshots/edited/step-001.png",
                SourceEventIds = [Guid.NewGuid()],
            },
        ];

        await store.SaveProjectAsync(project);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.NotNull(loaded);
        Assert.Equal(project.Id, loaded.Id);
        Assert.Equal("往復テスト", loaded.Title);
        Assert.Equal("経費を申請できるようにする", loaded.Objective);
        Assert.Equal("新入社員", loaded.TargetAudience);
        Assert.Equal(["経理の基礎"], loaded.Prerequisites);
        Assert.Equal(84_210, loaded.Recording!.DurationMs);
        Assert.True(loaded.Recording.HasSystemAudio);
        Assert.False(loaded.Recording.HasMicrophone);
        Assert.Equal("display-1", loaded.Recording.DisplayId);
        Assert.Single(loaded.Steps);
        Assert.Equal("「新規申請」をクリックします", loaded.Steps[0].Title);
        Assert.Equal("screenshots/edited/step-001.png", loaded.Steps[0].ScreenshotPath);
        Assert.Equal(project.Steps[0].SourceEventIds, loaded.Steps[0].SourceEventIds);
    }

    // =====================================================================
    // T-D2-04 camelCase / Contract §21 の直列化規則
    // =====================================================================
    [Fact]
    public async Task T_D2_04_ProjectJson_FollowsTrainingJsonRule()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("直列化テスト");
        var json = await File.ReadAllTextAsync(ProjectJsonPath(temp, project.Id));

        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"title\":", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
        Assert.DoesNotContain("\"Title\"", json);

        // TrainingJson.Indented（整形あり）
        Assert.Contains(Environment.NewLine, json);
    }

    // =====================================================================
    // T-D2-05 Invalid Project Reject — 既存 project.json を壊さない
    // =====================================================================
    [Fact]
    public async Task T_D2_05_InvalidProject_IsRejected_AndExistingFileSurvives()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("正常な教材");
        var path = ProjectJsonPath(temp, project.Id);
        var before = await File.ReadAllTextAsync(path);

        project.Title = "   "; // Contract §22: Required Text は null / blank 禁止
        await Assert.ThrowsAsync<ProjectStoreException>(() => store.SaveProjectAsync(project));

        Assert.Equal(before, await File.ReadAllTextAsync(path));
    }

    // =====================================================================
    // T-D2-06 Missing Project
    // =====================================================================
    [Fact]
    public async Task T_D2_06_Load_MissingProject_ReturnsNull()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        Assert.Null(await store.LoadProjectAsync(Guid.NewGuid()));
    }

    // =====================================================================
    // T-D2-07 Corrupt JSON — silently null にしない
    // =====================================================================
    [Fact]
    public async Task T_D2_07_Load_CorruptJson_ThrowsInsteadOfReturningNull()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("壊れる教材");
        await File.WriteAllTextAsync(ProjectJsonPath(temp, project.Id), "{{{ this is not json");

        var ex = await Assert.ThrowsAsync<ProjectStoreException>(() => store.LoadProjectAsync(project.Id));
        Assert.Contains("破損", ex.Message);
    }

    // =====================================================================
    // T-D2-08 Unsupported Schema — Load reject
    // =====================================================================
    [Fact]
    public async Task T_D2_08_Load_UnsupportedSchemaVersion_IsRejected()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("未来の教材");

        // 保存は validator が拒否するので、将来 schema の project.json を直接置く
        project.SchemaVersion = 2;
        var json = JsonSerializer.Serialize(project, TrainingJson.Indented);
        await File.WriteAllTextAsync(ProjectJsonPath(temp, project.Id), json);

        var ex = await Assert.ThrowsAsync<ProjectStoreException>(() => store.LoadProjectAsync(project.Id));
        Assert.Contains("schemaVersion", ex.Message);
    }

    // =====================================================================
    // T-D2-09 List — UpdatedAtUtc desc / StepCount
    // =====================================================================
    [Fact]
    public async Task T_D2_09_List_ReturnsSummaries_OrderedByUpdatedAtDescending()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var a = await store.CreateProjectAsync("A");
        var b = await store.CreateProjectAsync("B");
        var c = await store.CreateProjectAsync("C");

        b.Steps =
        [
            new TrainingStep { Id = Guid.NewGuid(), Order = 1, StartMs = 0, Action = StepActions.Manual, Title = "手順1" },
            new TrainingStep { Id = Guid.NewGuid(), Order = 2, StartMs = 10, Action = StepActions.Manual, Title = "手順2" },
        ];

        // 作成直後は UpdatedAtUtc が同値になり得るため、明示的にずらして順序を確定させる
        a.UpdatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        b.UpdatedAtUtc = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        c.UpdatedAtUtc = new DateTimeOffset(2026, 1, 3, 0, 0, 0, TimeSpan.Zero);
        await store.SaveProjectAsync(a);
        await store.SaveProjectAsync(b);
        await store.SaveProjectAsync(c);

        var list = await store.ListProjectsAsync();

        Assert.Equal(3, list.Count);
        Assert.Equal(new[] { "C", "B", "A" }, list.Select(s => s.Title));
        Assert.Equal(2, list.Single(s => s.Title == "B").StepCount);
        Assert.Equal(0, list.Single(s => s.Title == "A").StepCount);
        Assert.Equal(b.Id, list.Single(s => s.Title == "B").Id);
    }

    // =====================================================================
    // T-D2-10 List Ignores Noise
    // =====================================================================
    [Fact]
    public async Task T_D2_10_List_IgnoresNonGuidAndMissingJson()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var valid = await store.CreateProjectAsync("有効な教材");

        Directory.CreateDirectory(Path.Combine(temp.Root, "not-a-guid"));
        Directory.CreateDirectory(Path.Combine(temp.Root, Guid.NewGuid().ToString("D"))); // project.json なし
        await File.WriteAllTextAsync(Path.Combine(temp.Root, "loose-file.txt"), "noise");

        var list = await store.ListProjectsAsync();

        Assert.Single(list);
        Assert.Equal(valid.Id, list[0].Id);
    }

    // =====================================================================
    // T-D2-11 Corrupt Project Does Not Break List
    // =====================================================================
    [Fact]
    public async Task T_D2_11_List_SkipsCorruptProject_AndStillReturnsHealthyOnes()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var healthy = await store.CreateProjectAsync("正常な教材");
        var broken = await store.CreateProjectAsync("壊れた教材");
        await File.WriteAllTextAsync(ProjectJsonPath(temp, broken.Id), "{ not json at all");

        var list = await store.ListProjectsAsync();

        Assert.Single(list);
        Assert.Equal(healthy.Id, list[0].Id);
    }

    // =====================================================================
    // T-D2-12 Rename — directory 名は不変 / Revision +1
    // =====================================================================
    [Fact]
    public async Task T_D2_12_Rename_UpdatesTitleAndRevision_WithoutRenamingDirectory()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("旧タイトル");
        var dir = ProjectDir(temp, project.Id);
        var revisionBefore = project.Revision;

        var renamed = await store.RenameProjectAsync(project.Id, "新タイトル");

        Assert.Equal("新タイトル", renamed.Title);
        Assert.Equal(revisionBefore + 1, renamed.Revision);
        Assert.True(renamed.UpdatedAtUtc >= project.UpdatedAtUtc);

        // filesystem identity は不変
        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "project.json")));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal("新タイトル", loaded!.Title);
        Assert.Equal(revisionBefore + 1, loaded.Revision);
    }

    // =====================================================================
    // T-D2-13 Rename Blank
    // =====================================================================
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task T_D2_13_Rename_BlankTitle_IsRejected(string blank)
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("元のタイトル");

        await Assert.ThrowsAsync<ArgumentException>(() => store.RenameProjectAsync(project.Id, blank));

        // 失敗しても元のタイトルのまま
        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal("元のタイトル", loaded!.Title);
    }

    // =====================================================================
    // T-D2-14 Delete
    // =====================================================================
    [Fact]
    public async Task T_D2_14_Delete_RemovesOnlyThatProjectDirectory()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("削除対象");
        var dir = ProjectDir(temp, project.Id);
        Assert.True(Directory.Exists(dir));

        Assert.True(await store.DeleteProjectAsync(project.Id));

        Assert.False(Directory.Exists(dir));
        Assert.Null(await store.LoadProjectAsync(project.Id));
    }

    // =====================================================================
    // T-D2-15 Delete Isolation
    // =====================================================================
    [Fact]
    public async Task T_D2_15_Delete_DoesNotAffectOtherProjects()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var a = await store.CreateProjectAsync("教材A");
        var b = await store.CreateProjectAsync("教材B");

        await store.DeleteProjectAsync(a.Id);

        Assert.False(Directory.Exists(ProjectDir(temp, a.Id)));
        Assert.True(Directory.Exists(ProjectDir(temp, b.Id)));

        var loadedB = await store.LoadProjectAsync(b.Id);
        Assert.NotNull(loadedB);
        Assert.Equal("教材B", loadedB.Title);

        var list = await store.ListProjectsAsync();
        Assert.Single(list);
        Assert.Equal(b.Id, list[0].Id);
    }

    // =====================================================================
    // T-D2-16 Absolute Path Validation（Contract §18）
    // =====================================================================
    [Theory]
    [InlineData("C:\\Users\\user\\raw\\recording.mp4")]
    [InlineData("\\\\server\\share\\recording.mp4")]
    public async Task T_D2_16_Save_RejectsContractViolatingAbsolutePath(string absolutePath)
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("パス違反");
        project.Recording = new RecordingInfo
        {
            MediaPath = absolutePath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 1_000,
        };

        await Assert.ThrowsAsync<ProjectStoreException>(() => store.SaveProjectAsync(project));
    }

    // =====================================================================
    // AC-D2-08 ProjectsRoot の絶対パスを JSON に保存しない
    // =====================================================================
    [Fact]
    public async Task Ac_D2_08_ProjectJson_DoesNotContainProjectsRootAbsolutePath()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("絶対パス混入チェック");
        project.Recording = new RecordingInfo
        {
            MediaPath = "raw/recording.mp4",
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 1_000,
        };
        await store.SaveProjectAsync(project);

        var json = await File.ReadAllTextAsync(ProjectJsonPath(temp, project.Id));

        Assert.DoesNotContain(temp.Root, json);
        Assert.Contains("raw/recording.mp4", json);
    }

    // =====================================================================
    // T-D2-17 Atomic Save — 失敗時に既存 project.json を維持する
    // =====================================================================
    [Fact]
    public async Task T_D2_17_SaveFailure_KeepsExistingProjectJsonIntact()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("原子性テスト");
        project.Objective = "v1";
        await store.SaveProjectAsync(project);

        var path = ProjectJsonPath(temp, project.Id);
        var before = await File.ReadAllTextAsync(path);

        // validation failure で保存が止まるケース
        project.Objective = "v2";
        project.Title = ""; // 違反
        await Assert.ThrowsAsync<ProjectStoreException>(() => store.SaveProjectAsync(project));

        Assert.Equal(before, await File.ReadAllTextAsync(path));

        // temp file が残って次回操作を妨げないこと
        Assert.False(File.Exists(Path.Combine(ProjectDir(temp, project.Id), ProjectStore.ProjectTempFileName)));

        // 再度正しく保存すれば通る
        project.Title = "原子性テスト";
        await store.SaveProjectAsync(project);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal("v2", loaded!.Objective);
    }

    // =====================================================================
    // ProjectSummary — metadata と実ファイルの両方を確認する（§7）
    // =====================================================================
    [Fact]
    public async Task Summary_ArtifactFlags_RequireBothMetadataAndFile()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("成果物フラグ");
        project.Outputs.ManualMarkdown = new GeneratedArtifact
        {
            Path = "manual/manual.md",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = project.Revision,
        };
        project.Outputs.TrainingVideo = new GeneratedArtifact
        {
            Path = "output/training_video.mp4",
            GeneratedAtUtc = DateTimeOffset.UtcNow,
            SourceRevision = project.Revision,
        };
        await store.SaveProjectAsync(project);

        // metadata はあるが実ファイルが無い → false
        var withoutFiles = await store.ListProjectsAsync();
        var s1 = Assert.Single(withoutFiles);
        Assert.False(s1.HasManual);
        Assert.False(s1.HasVideo);

        // 実ファイルを置く → true
        var dir = ProjectDir(temp, project.Id);
        await File.WriteAllTextAsync(Path.Combine(dir, "manual", "manual.md"), "# manual");
        await File.WriteAllBytesAsync(Path.Combine(dir, "output", "training_video.mp4"), [1, 2, 3]);

        var withFiles = await store.ListProjectsAsync();
        var s2 = Assert.Single(withFiles);
        Assert.True(s2.HasManual);
        Assert.True(s2.HasVideo);
    }

    // =====================================================================
    // D5-A Recording path — Contract §17 raw/recording.mp4（§18 で絶対パス保存禁止）
    // =====================================================================

    [Fact]
    public void T_D5A_S01_RecordingMediaPath_IsCanonicalRelativePath()
    {
        Assert.Equal("raw/recording.mp4", ProjectStore.RecordingMediaPath);
        Assert.DoesNotContain('\\', ProjectStore.RecordingMediaPath);
        Assert.False(Path.IsPathRooted(ProjectStore.RecordingMediaPath));
    }

    [Fact]
    public async Task T_D5A_S02_GetRecordingOutputPath_IsUnderProjectRawDirectory()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("録画パス");

        var path = store.GetRecordingOutputPath(project.Id);

        Assert.True(Path.IsPathRooted(path), "engine へ渡す path は絶対 path");
        Assert.Equal(Path.Combine(ProjectDir(temp, project.Id), "raw", "recording.mp4"), path);
        Assert.Equal("recording.mp4", Path.GetFileName(path));
        Assert.Equal("raw", Path.GetFileName(Path.GetDirectoryName(path)));
    }

    [Fact]
    public void T_D5A_S03_GetRecordingOutputPath_NeverEscapesProjectsRoot()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        // Guid 由来なので任意の id でも ProjectsRoot 配下に閉じる（Create 前でも同じ）
        foreach (var id in new[] { Guid.NewGuid(), Guid.Empty, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") })
        {
            var path = store.GetRecordingOutputPath(id);

            var rootPrefix = temp.Root.EndsWith(Path.DirectorySeparatorChar)
                ? temp.Root
                : temp.Root + Path.DirectorySeparatorChar;

            Assert.StartsWith(rootPrefix, path, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(id.ToString("D"), path, StringComparison.OrdinalIgnoreCase);
        }
    }

    // =====================================================================
    // D5-B Project directory — OperationCaptureSession へ渡す runtime path
    // =====================================================================

    [Fact]
    public async Task T_D5B_S01_GetProjectDirectory_IsAbsoluteGuidDirectory()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("プロジェクトディレクトリ");

        var dir = store.GetProjectDirectory(project.Id);

        Assert.True(Path.IsPathRooted(dir), "runtime path は絶対 path");
        Assert.Equal(ProjectDir(temp, project.Id), dir);
        Assert.Equal(project.Id.ToString("D"), Path.GetFileName(dir));
        Assert.True(Directory.Exists(dir));
    }

    [Fact]
    public async Task T_D5B_S02_GetProjectDirectory_HasContractSubDirectories()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("サブディレクトリ");

        var dir = store.GetProjectDirectory(project.Id);

        // OperationCaptureSession は events.jsonl と screenshots/original/ をここへ書く。
        Assert.True(File.Exists(Path.Combine(dir, ProjectStore.EventsFileName)));
        Assert.True(Directory.Exists(Path.Combine(dir, "screenshots", "original")));
        Assert.Equal(
            store.GetRecordingOutputPath(project.Id),
            Path.Combine(dir, "raw", "recording.mp4"));
    }

    [Fact]
    public void T_D5B_S03_GetProjectDirectory_NeverEscapesProjectsRoot()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var rootPrefix = temp.Root.EndsWith(Path.DirectorySeparatorChar)
            ? temp.Root
            : temp.Root + Path.DirectorySeparatorChar;

        foreach (var id in new[] { Guid.NewGuid(), Guid.Empty, Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff") })
        {
            var dir = store.GetProjectDirectory(id);

            Assert.StartsWith(rootPrefix, dir, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(id.ToString("D"), dir, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("..", dir);
        }
    }

    // =====================================================================
    // Save の失敗経路 — temp file を残さない（既存 project.json は無傷のまま）
    // =====================================================================

    [Fact]
    public async Task T_D5_H01_SaveFailure_LeavesNoTempFileAndKeepsExistingJson()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("temp cleanup");
        var jsonPath = ProjectJsonPath(temp, project.Id);
        var tempPath = Path.Combine(ProjectDir(temp, project.Id), ProjectStore.ProjectTempFileName);
        var before = await File.ReadAllTextAsync(jsonPath);

        // 置換（File.Move）だけを失敗させる: 既存 project.json を他ハンドルが握っている状態にする。
        using (var hold = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            project.Objective = "v2";

            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveProjectAsync(project));
        }

        // temp file が残らない
        Assert.False(File.Exists(tempPath), "project.json.tmp が残っている");

        // 既存 project.json は削除も上書きもされていない
        Assert.True(File.Exists(jsonPath));
        Assert.Equal(before, await File.ReadAllTextAsync(jsonPath));
    }

    [Fact]
    public async Task T_D5_H02_SaveFailure_RecoversOnNextSave()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        var project = await store.CreateProjectAsync("temp cleanup recovery");
        var jsonPath = ProjectJsonPath(temp, project.Id);
        var tempPath = Path.Combine(ProjectDir(temp, project.Id), ProjectStore.ProjectTempFileName);

        using (var hold = new FileStream(jsonPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            project.Objective = "v2";
            await Assert.ThrowsAnyAsync<Exception>(() => store.SaveProjectAsync(project));
        }

        // 障害要因が消えれば、通常どおり保存できる（temp も残らない）
        await store.SaveProjectAsync(project);

        Assert.False(File.Exists(tempPath), "project.json.tmp が残っている");
        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Equal("v2", loaded!.Objective);
    }

    // =====================================================================
    // Default Projects Root — install directory / repository を使わない（§5）
    // =====================================================================
    [Fact]
    public void DefaultProjectsRoot_IsUnderLocalApplicationData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        Assert.False(string.IsNullOrWhiteSpace(localAppData));
        Assert.StartsWith(localAppData, ProjectStore.DefaultProjectsRoot, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TrainingContentGenerator", ProjectStore.DefaultProjectsRoot);
        Assert.EndsWith("projects", ProjectStore.DefaultProjectsRoot);
    }
}
