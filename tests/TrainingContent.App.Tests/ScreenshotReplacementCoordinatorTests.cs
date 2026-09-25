// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// B3: <see cref="ScreenshotReplacementCoordinator"/> の orchestration（R1〜R19）。
///
/// <para>
/// provenance（original へ書かない）・fresh naming・no overwrite・旧 file 保持・PNG 変換・
/// save failure semantics を file 単位で確認する。source 画像は test 側で WPF encoder を使って作る。
/// </para>
/// </summary>
public class ScreenshotReplacementCoordinatorTests
{
    private const string OriginalRelative = "screenshots/original/event-000001.png";
    private const int SourceWidth = 64;
    private const int SourceHeight = 40;

    // =====================================================================
    // 共通インフラ
    // =====================================================================

    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tc-app-tests", Guid.NewGuid().ToString("N"));

        public TempProjectsRoot() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record Context(
        TempProjectsRoot Temp,
        ProjectStore Store,
        ProjectWorkspace Workspace,
        CurrentProjectContext Current,
        ScreenshotReplacementCoordinator Coordinator,
        TrainingProject Project,
        Guid StepId,
        string ProjectDirectory);

    private static async Task<Context> SetupAsync(
        TempProjectsRoot temp,
        bool manualStep = false,
        bool withExistingScreenshot = true)
    {
        var store = new ProjectStore(temp.Root);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);
        var coordinator = new ScreenshotReplacementCoordinator(store, workspace);

        var project = await store.CreateProjectAsync("B3 テスト");
        var stepId = Guid.NewGuid();
        project.Steps.Add(new TrainingStep
        {
            Id = stepId,
            Order = 1,
            StartMs = 1000,
            Action = manualStep ? StepActions.Manual : StepActions.Click,
            Title = "手順 1",
            Target = manualStep ? null : "AlphaClick",
            Description = "desc",
            Caution = "caution",
            ExpectedResult = "expected",
            ScreenshotPath = manualStep || !withExistingScreenshot ? null : OriginalRelative,
            SourceEventIds = manualStep ? [] : [Guid.NewGuid()],
        });
        await store.SaveProjectAsync(project);

        var projectDirectory = store.GetProjectDirectory(project.Id);
        if (!manualStep && withExistingScreenshot)
        {
            WriteImage(
                Path.Combine(projectDirectory, "screenshots", "original", "event-000001.png"),
                ImageFormat.Png, 32, 24, 0x30, 0x60, 0x90);
        }

        var persisted = await store.LoadProjectAsync(project.Id)
            ?? throw new InvalidOperationException("test 前提が壊れています。");

        return new Context(temp, store, workspace, current, coordinator, persisted, stepId, projectDirectory);
    }

    private enum ImageFormat
    {
        Png,
        Jpeg,
        Bmp,
    }

    /// <summary>WPF の encoder で単色画像を作る（第三者 library は使わない）。</summary>
    private static void WriteImage(
        string path, ImageFormat format, int width, int height, byte r, byte g, byte b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);
        var frame = BitmapFrame.Create(bitmap);

        BitmapEncoder encoder = format switch
        {
            ImageFormat.Jpeg => new JpegBitmapEncoder(),
            ImageFormat.Bmp => new BmpBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        encoder.Frames.Add(frame);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static string EditedDirectory(Context ctx) => Path.Combine(ctx.ProjectDirectory, "screenshots", "edited");

    private static string[] EditedFiles(Context ctx) =>
        Directory.Exists(EditedDirectory(ctx)) ? Directory.GetFiles(EditedDirectory(ctx), "*.png") : [];

    private static string SourcePath(Context ctx, string name) => Path.Combine(ctx.Temp.Root, "sources", name);

    // =====================================================================
    // R1 / R2 / R3 / R4 — 形式ごとの変換と出力 path
    // =====================================================================

    [Fact]
    public async Task R1_PNG_source_から_fresh_PNG_が作られる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 0x10, 0x20, 0x30);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.Equal("スクリーンショットを差し替えました。", outcome.Message);
        Assert.Single(EditedFiles(ctx));
    }

    [Fact]
    public async Task R2_JPEG_source_から_canonical_PNG_が作られる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.jpg");
        WriteImage(source, ImageFormat.Jpeg, SourceWidth, SourceHeight, 0xAA, 0xBB, 0xCC);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        var produced = Assert.Single(EditedFiles(ctx));
        Assert.EndsWith(".png", produced, StringComparison.Ordinal);
        Assert.True(File.Exists(produced));
    }

    [Fact]
    public async Task R3_BMP_source_から_canonical_PNG_が作られる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.bmp");
        WriteImage(source, ImageFormat.Bmp, SourceWidth, SourceHeight, 0x11, 0x22, 0x33);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.Single(EditedFiles(ctx));
    }

    [Fact]
    public async Task R4_出力は_screenshots_edited_step_StepId_Guid_png_になる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 1, 2, 3);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.NotNull(outcome.EditedRelativePath);
        Assert.StartsWith("screenshots/edited/step-" + ctx.StepId.ToString("N") + "-", outcome.EditedRelativePath!, StringComparison.Ordinal);
        Assert.EndsWith(".png", outcome.EditedRelativePath!, StringComparison.Ordinal);

        // 拡張子だけ .png へ rename した copy ではない（decode 済み PNG として読める）。
        var saved = Path.Combine(ctx.ProjectDirectory, "screenshots", "edited", Path.GetFileName(outcome.EditedRelativePath!));
        var size = ReadPngSize(saved);
        Assert.True(size.Width > 0 && size.Height > 0);
    }

    // =====================================================================
    // R5 / R6 / R7 / R8 / R9 — decode 品質と immutability
    // =====================================================================

    [Fact]
    public async Task R5_生成された_PNG_は_decode_できる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 9, 8, 7);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        var produced = Path.Combine(ctx.ProjectDirectory, "screenshots", "edited", Path.GetFileName(outcome.EditedRelativePath!));
        using var stream = new FileStream(produced, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        Assert.NotEmpty(decoder.Frames);
    }

    [Fact]
    public async Task R6_pixel_dimensions_は_source_と同じ()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.jpg");
        WriteImage(source, ImageFormat.Jpeg, 37, 21, 5, 6, 7);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        var produced = Path.Combine(ctx.ProjectDirectory, "screenshots", "edited", Path.GetFileName(outcome.EditedRelativePath!));
        Assert.Equal((37, 21), ReadPngSize(produced));
    }

    [Fact]
    public async Task R7_source_file_は変更されない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.bmp");
        WriteImage(source, ImageFormat.Bmp, SourceWidth, SourceHeight, 3, 3, 3);
        var bytesBefore = await File.ReadAllBytesAsync(source);
        var mtimeBefore = File.GetLastWriteTimeUtc(source);

        await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(source));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public async Task R8_old_original_は_bytes_mtime_とも_変わらない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 4, 4, 4);

        var original = Path.Combine(ctx.ProjectDirectory, "screenshots", "original", "event-000001.png");
        var bytesBefore = await File.ReadAllBytesAsync(original);
        var mtimeBefore = File.GetLastWriteTimeUtc(original);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.True(File.Exists(original));
        Assert.Equal(bytesBefore, await File.ReadAllBytesAsync(original));
        Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(original));
    }

    [Fact]
    public async Task R9_old_edited_は削除も上書きもされない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp, withExistingScreenshot: false);

        // 1 回目（import）→ 2 回目（replacement from edited）。
        var first = SourcePath(ctx, "first.png");
        WriteImage(first, ImageFormat.Png, SourceWidth, SourceHeight, 1, 1, 1);
        var firstOutcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, null, first);
        var firstPath = Path.Combine(ctx.ProjectDirectory, "screenshots", "edited", Path.GetFileName(firstOutcome.EditedRelativePath!));
        var firstBytes = await File.ReadAllBytesAsync(firstPath);

        var second = SourcePath(ctx, "second.png");
        WriteImage(second, ImageFormat.Png, SourceWidth, SourceHeight, 2, 2, 2);
        var secondOutcome = await ctx.Coordinator.ReplaceAsync(
            ctx.Project.Id, ctx.StepId, firstOutcome.EditedRelativePath, second);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, secondOutcome.Status);
        Assert.NotEqual(firstOutcome.EditedRelativePath, secondOutcome.EditedRelativePath);

        // 旧 edited は残り、bytes も変わらない。
        Assert.True(File.Exists(firstPath));
        Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath));
        Assert.Equal(2, EditedFiles(ctx).Length);
    }

    // =====================================================================
    // R10 / R11 / R12 — 成功時の canonical 更新
    // =====================================================================

    [Fact]
    public async Task R10_成功で_ScreenshotPath_が_fresh_path_へ更新される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 8, 8, 8);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(outcome.EditedRelativePath, saved!.Steps[0].ScreenshotPath);
        Assert.StartsWith("screenshots/edited/", saved.Steps[0].ScreenshotPath!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R11_成功で_Revision_が_1_進む()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 1, 2, 3);
        var revisionBefore = ctx.Project.Revision;

        await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(revisionBefore + 1, (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Revision);
    }

    [Fact]
    public async Task R12_他の_Step_field_は_変わらない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 1, 2, 3);

        var before = ctx.Project.Steps[0];
        var beforeFields = (
            before.Id, before.Order, before.StartMs, before.EndMs, before.Action, before.Target,
            before.Title, before.Description, before.Caution, before.ExpectedResult);

        await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        var after = (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Steps[0];
        var afterFields = (
            after.Id, after.Order, after.StartMs, after.EndMs, after.Action, after.Target,
            after.Title, after.Description, after.Caution, after.ExpectedResult);

        Assert.Equal(beforeFields, afterFields);
        Assert.Equal(before.SourceEventIds, after.SourceEventIds);
    }

    // =====================================================================
    // R13 — manual Step への import
    // =====================================================================

    [Fact]
    public async Task R13_manual_Step_の_null_screenshot_へ_import_できる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp, manualStep: true);
        var source = SourcePath(ctx, "manual.png");
        WriteImage(source, ImageFormat.Jpeg, SourceWidth, SourceHeight, 7, 7, 7);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, null, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.Equal("スクリーンショットを追加しました。", outcome.Message);

        var step = (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Steps[0];
        Assert.Equal(outcome.EditedRelativePath, step.ScreenshotPath);
        Assert.Equal(StepActions.Manual, step.Action);
        Assert.Empty(step.SourceEventIds);
        Assert.Equal(1000, step.StartMs);
    }

    // =====================================================================
    // R14 / R15 / R16 — failure では Project を変えない
    // =====================================================================

    [Fact]
    public async Task R14_source_が存在しなければ_SourceMissing_で_Project_は不変()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var revisionBefore = ctx.Project.Revision;

        var outcome = await ctx.Coordinator.ReplaceAsync(
            ctx.Project.Id, ctx.StepId, OriginalRelative, SourcePath(ctx, "missing.png"));

        Assert.Equal(ScreenshotReplacementStatus.SourceMissing, outcome.Status);
        Assert.Equal("画像を読み込めませんでした。", outcome.Message);
        Assert.Equal(revisionBefore, (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Revision);
        Assert.Empty(EditedFiles(ctx));
    }

    [Fact]
    public async Task R15_未対応拡張子は_UnsupportedFormat_で_Project_は不変()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "anim.gif");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllBytesAsync(source, [0x47, 0x49, 0x46]);
        var revisionBefore = ctx.Project.Revision;

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.UnsupportedFormat, outcome.Status);
        Assert.Equal("選択した画像を使用できません。", outcome.Message);
        Assert.Equal(revisionBefore, (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Revision);
        Assert.Empty(EditedFiles(ctx));
    }

    [Fact]
    public async Task R16_壊れた画像は_DecodeFailed_で_中間_file_も残さない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "broken.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        await File.WriteAllTextAsync(source, "this is not a png");
        var revisionBefore = ctx.Project.Revision;

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.DecodeFailed, outcome.Status);
        Assert.Equal("選択した画像を使用できません。", outcome.Message);
        Assert.Equal(revisionBefore, (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Revision);

        // 途中まで作られた fresh PNG は best-effort で片付ける。
        Assert.Empty(EditedFiles(ctx));
    }

    // =====================================================================
    // R17 — save failure
    // =====================================================================

    [Fact]
    public async Task R17_保存失敗では_old_ScreenshotPath_が残り_orphan_PNG_は許容する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 5, 5, 5);

        // project.json を掴んで Storage save を失敗させる。
        using var lockStream = new FileStream(
            Path.Combine(ctx.ProjectDirectory, ProjectStore.ProjectFileName),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.SaveFailed, outcome.Status);
        Assert.Equal("スクリーンショットを保存できませんでした。", outcome.Message);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(OriginalRelative, saved!.Steps[0].ScreenshotPath);
        Assert.Equal(ctx.Project.Revision, saved.Revision);

        // 生成済み PNG は orphan として許容（rollback delete はしない）。
        Assert.Single(EditedFiles(ctx));
    }

    // =====================================================================
    // R18 / R19 — CurrentProject と thread affinity
    // =====================================================================

    [Fact]
    public async Task R18_同一_Project_が_current_なら_CurrentProject_が更新される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(ctx.Project);
        var before = ctx.Current.CurrentProject;
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 6, 6, 6);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.NotSame(before, ctx.Current.CurrentProject);
        Assert.Equal(outcome.EditedRelativePath, ctx.Current.CurrentProject!.Steps[0].ScreenshotPath);
    }

    [Fact]
    public async Task R18b_別_Project_が_current_なら_上書きしない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var other = await ctx.Store.CreateProjectAsync("別プロジェクト");
        ctx.Current.SetCurrent(other);
        var before = ctx.Current.CurrentProject;
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 6, 6, 6);

        var outcome = await ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.Same(before, ctx.Current.CurrentProject);
    }

    /// <summary>単一 thread の UI context を模す（C の R18 / G14 / W4 と同型）。</summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PostCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            _queue.Enqueue((d, state));
        }

        public void PumpUntil(Task task)
        {
            while (!task.IsCompleted)
            {
                if (_queue.TryDequeue(out var work))
                {
                    work.Callback(work.State);
                }
                else
                {
                    Thread.Sleep(5);
                }
            }

            while (_queue.TryDequeue(out var work))
            {
                work.Callback(work.State);
            }
        }
    }

    [Fact]
    public async Task R19_Workspace_publish_は_caller_の_context_で行われる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(ctx.Project);
        var source = SourcePath(ctx, "import.png");
        WriteImage(source, ImageFormat.Png, SourceWidth, SourceHeight, 1, 1, 1);

        var callerThread = Environment.CurrentManagedThreadId;
        var publishThread = -1;
        ctx.Current.CurrentProjectChanged += (_, _) => publishThread = Environment.CurrentManagedThreadId;

        var context = new PumpingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        Task<ScreenshotReplacementOutcome> task;
        try
        {
            task = ctx.Coordinator.ReplaceAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, source);
            context.PumpUntil(task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var outcome = await task;

        Assert.Equal(ScreenshotReplacementStatus.Replaced, outcome.Status);
        Assert.True(
            Volatile.Read(ref context.PostCount) > 0,
            "coordinator が caller の context へ戻っていません（UI thread から外れています）。");
        Assert.Equal(callerThread, publishThread);
    }
}
