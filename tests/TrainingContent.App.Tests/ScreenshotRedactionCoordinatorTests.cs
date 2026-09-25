// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Screenshot.Redaction;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// C: <see cref="ScreenshotRedactionCoordinator"/> の orchestration（R9〜R18）。
///
/// <para>
/// 画像処理そのものは C（Screenshot Core）の責務なので、orchestration の検証には fake redactor を使う。
/// 併せて 1 件だけ <see cref="BlackBoxScreenshotRedactor"/> を実物で通し、生成された PNG の画素まで確認する
/// （original immutability と黒塗り結果の自動検証）。
/// </para>
/// </summary>
public class ScreenshotRedactionCoordinatorTests
{
    private const string OriginalRelative = "screenshots/original/event-000001.png";
    private const byte SourceR = 0xF0;
    private const byte SourceG = 0x40;
    private const byte SourceB = 0x20;

    // =====================================================================
    // 共通インフラ
    // =====================================================================

    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; }

        public TempProjectsRoot()
        {
            Root = Path.Combine(Path.GetTempPath(), "tc-app-tests", Guid.NewGuid().ToString("N"));
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
            }
        }
    }

    /// <summary>Commit / output 生成を test が制御できる最小の redactor。</summary>
    private sealed class FakeScreenshotRedactor : IScreenshotRedactor
    {
        public List<ScreenshotRedactionRequest> Requests { get; } = [];

        /// <summary>null なら「成功し output を書く」既定挙動。</summary>
        public Func<ScreenshotRedactionRequest, ScreenshotRedactionResult>? OnRedact { get; set; }

        public Task<ScreenshotRedactionResult> RedactAsync(
            ScreenshotRedactionRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);

            if (OnRedact is not null)
            {
                return Task.FromResult(OnRedact(request));
            }

            // 成功を模す: coordinator は output の存在を確認するので file も作る。
            File.WriteAllBytes(request.OutputPath, [0x01, 0x02, 0x03]);
            return Task.FromResult(new ScreenshotRedactionResult
            {
                Width = 64,
                Height = 64,
                AppliedRegionCount = request.Regions.Count,
            });
        }
    }

    private sealed record Context(
        TempProjectsRoot Temp,
        ProjectStore Store,
        ProjectWorkspace Workspace,
        CurrentProjectContext Current,
        TrainingProject Project,
        Guid StepId,
        string ProjectDirectory,
        string OriginalAbsolute);

    private static async Task<Context> SetupAsync(
        TempProjectsRoot temp,
        string sourceRelative = OriginalRelative,
        bool writeSourceFile = true)
    {
        var store = new ProjectStore(temp.Root);
        var current = new CurrentProjectContext();
        var workspace = new ProjectWorkspace(store, current);

        var project = await store.CreateProjectAsync("Redaction テスト");
        var stepId = Guid.NewGuid();
        project.Steps.Add(new TrainingStep
        {
            Id = stepId,
            Order = 1,
            StartMs = 0,
            Action = StepActions.Click,
            Title = "手順 1",
            Target = "AlphaClick",
            ScreenshotPath = sourceRelative,
        });
        await store.SaveProjectAsync(project);

        var projectDirectory = store.GetProjectDirectory(project.Id);
        var originalAbsolute = Path.Combine(projectDirectory, "screenshots", "original", "event-000001.png");
        if (writeSourceFile)
        {
            WritePng(originalAbsolute, 64, 64, SourceR, SourceG, SourceB);
        }

        var persisted = await store.LoadProjectAsync(project.Id)
            ?? throw new InvalidOperationException("test 前提が壊れています。");

        return new Context(temp, store, workspace, current, persisted, stepId, projectDirectory, originalAbsolute);
    }

    /// <summary>WPF の encoder で単色 PNG を作る（System.Drawing 依存を test へ持ち込まない）。</summary>
    private static void WritePng(string path, int width, int height, byte r, byte g, byte b)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var bitmap = new WriteableBitmap(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 0xFF;
        }

        bitmap.WritePixels(new System.Windows.Int32Rect(0, 0, width, height), pixels, width * 4, 0);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(stream);
    }

    private static (byte R, byte G, byte B) ReadPixel(string path, int x, int y)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var offset = (y * stride) + (x * 4);
        return (pixels[offset + 2], pixels[offset + 1], pixels[offset]);
    }

    private static FileStream LockProjectJson(ProjectStore store, Guid projectId) =>
        new(
            Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

    // =====================================================================
    // R9 / R10 / R11 — 成功経路と source の扱い
    // =====================================================================

    [Fact]
    public async Task R9_成功で_fresh_な_edited_png_が生成され_ScreenshotPath_と_Revision_が更新される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var redactor = new FakeScreenshotRedactor();
        var coordinator = new ScreenshotRedactionCoordinator(redactor, ctx.Store, ctx.Workspace);
        var region = new RedactionRectangle(10, 20, 30, 40);

        var outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, region);

        Assert.Equal(ScreenshotRedactionStatus.Redacted, outcome.Status);
        Assert.Single(redactor.Requests);

        var request = redactor.Requests[0];
        Assert.Equal(ctx.OriginalAbsolute, request.SourceImagePath);
        Assert.Equal(new[] { region }, request.Regions);

        var expectedRelative = $"{ScreenshotRedactionCoordinator.EditedDirectoryRelativePath}/step-{ctx.StepId:N}-";
        Assert.StartsWith(expectedRelative, outcome.EditedRelativePath!, StringComparison.Ordinal);
        Assert.EndsWith(".png", outcome.EditedRelativePath!, StringComparison.Ordinal);
        Assert.Equal(
            Path.Combine(ctx.ProjectDirectory, outcome.EditedRelativePath!.Replace('/', '\\')),
            request.OutputPath);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(2, saved!.Revision);
        Assert.Equal(outcome.EditedRelativePath, saved.Steps[0].ScreenshotPath);
        Assert.NotEqual(OriginalRelative, saved.Steps[0].ScreenshotPath);
    }

    [Fact]
    public async Task R10_original_は変更されず_選択範囲が実際に黒塗りされる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var sourceBytesBefore = File.ReadAllBytes(ctx.OriginalAbsolute);

        // C の実装（Screenshot Core）を実物で通す。
        var coordinator = new ScreenshotRedactionCoordinator(
            new BlackBoxScreenshotRedactor(), ctx.Store, ctx.Workspace);
        var region = new RedactionRectangle(16, 16, 32, 32);

        var outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, region);

        Assert.Equal(ScreenshotRedactionStatus.Redacted, outcome.Status);

        // original は byte 単位で不変。
        Assert.Equal(sourceBytesBefore, File.ReadAllBytes(ctx.OriginalAbsolute));

        var editedAbsolute = Path.Combine(ctx.ProjectDirectory, outcome.EditedRelativePath!.Replace('/', '\\'));
        Assert.True(File.Exists(editedAbsolute));

        // 選択範囲の中心は黒、範囲外は元の色のまま。
        var inside = ReadPixel(editedAbsolute, region.X + (region.Width / 2), region.Y + (region.Height / 2));
        Assert.True(inside.R < 16 && inside.G < 16 && inside.B < 16, $"黒塗りされていません: {inside}");

        var outside = ReadPixel(editedAbsolute, 2, 2);
        Assert.Equal(SourceR, outside.R);
        Assert.Equal(SourceG, outside.G);
        Assert.Equal(SourceB, outside.B);
    }

    [Fact]
    public async Task R11_source_が既に_edited_でも_新しい_edited_が作られ既存_edited_は不変()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        // 1 回目の redaction（これで current は edited A になる）。
        var firstCoordinator = new ScreenshotRedactionCoordinator(new FakeScreenshotRedactor(), ctx.Store, ctx.Workspace);
        var first = await firstCoordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(1, 1, 4, 4));
        Assert.Equal(ScreenshotRedactionStatus.Redacted, first.Status);

        var editedAbsoluteA = Path.Combine(ctx.ProjectDirectory, first.EditedRelativePath!.Replace('/', '\\'));
        var bytesA = File.ReadAllBytes(editedAbsoluteA);

        // 2 回目（source = edited A）。
        var redactor = new FakeScreenshotRedactor();
        var secondCoordinator = new ScreenshotRedactionCoordinator(redactor, ctx.Store, ctx.Workspace);
        var second = await secondCoordinator.RedactAsync(ctx.Project.Id, ctx.StepId, first.EditedRelativePath, new RedactionRectangle(2, 2, 8, 8));

        Assert.Equal(ScreenshotRedactionStatus.Redacted, second.Status);
        Assert.NotEqual(first.EditedRelativePath, second.EditedRelativePath);

        // 既存 edited A も original も変更されない。
        Assert.Equal(bytesA, File.ReadAllBytes(editedAbsoluteA));

        var originalPixel = ReadPixel(ctx.OriginalAbsolute, 1, 1);
        Assert.Equal(SourceR, originalPixel.R);
        Assert.Equal(SourceG, originalPixel.G);
        Assert.Equal(SourceB, originalPixel.B);

        // source は「現在参照している path」（edited A）が渡っている。
        Assert.Equal(editedAbsoluteA, redactor.Requests[0].SourceImagePath);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(3, saved!.Revision);
    }

    // =====================================================================
    // R12 / R13 — 失敗経路
    // =====================================================================

    [Fact]
    public async Task R12_redactor_失敗では_project_を変更しない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var jsonBefore = File.ReadAllText(Path.Combine(ctx.ProjectDirectory, ProjectStore.ProjectFileName));

        var redactor = new FakeScreenshotRedactor
        {
            OnRedact = _ => new ScreenshotRedactionResult { Errors = ["処理に失敗しました。"] },
        };
        var coordinator = new ScreenshotRedactionCoordinator(redactor, ctx.Store, ctx.Workspace);

        var outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(1, 1, 4, 4));

        Assert.Equal(ScreenshotRedactionStatus.RedactionFailed, outcome.Status);
        Assert.Null(outcome.EditedRelativePath);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(1, saved!.Revision);
        Assert.Equal(OriginalRelative, saved.Steps[0].ScreenshotPath);
        Assert.Equal(jsonBefore, File.ReadAllText(Path.Combine(ctx.ProjectDirectory, ProjectStore.ProjectFileName)));

        // 失敗 message に raw な path を含めない。
        Assert.DoesNotContain(ctx.ProjectDirectory, outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R13_保存失敗では_canonical_と_CurrentProject_が旧状態のまま_orphan_png_は許容する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var persistedForCurrent = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        ctx.Current.SetCurrent(persistedForCurrent!);
        var currentBefore = ctx.Current.CurrentProject;
        var jsonBefore = File.ReadAllText(Path.Combine(ctx.ProjectDirectory, ProjectStore.ProjectFileName));

        var redactor = new FakeScreenshotRedactor();
        var coordinator = new ScreenshotRedactionCoordinator(redactor, ctx.Store, ctx.Workspace);

        ScreenshotRedactionOutcome outcome;
        using (LockProjectJson(ctx.Store, ctx.Project.Id))
        {
            outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(1, 1, 4, 4));
        }

        Assert.Equal(ScreenshotRedactionStatus.SaveFailed, outcome.Status);
        Assert.Same(currentBefore, ctx.Current.CurrentProject);
        Assert.Equal(jsonBefore, File.ReadAllText(Path.Combine(ctx.ProjectDirectory, ProjectStore.ProjectFileName)));

        // 生成済み PNG は orphan として残ってよい（rollback delete の failure surface を増やさない）。
        Assert.True(File.Exists(redactor.Requests[0].OutputPath));
    }

    // =====================================================================
    // R14 / R15 — redaction 不可の条件
    // =====================================================================

    [Fact]
    public async Task R14_ScreenshotPath_が無ければ_redaction_不可()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var redactor = new FakeScreenshotRedactor();
        var coordinator = new ScreenshotRedactionCoordinator(redactor, ctx.Store, ctx.Workspace);

        var outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, currentScreenshotPath: null, new RedactionRectangle(1, 1, 4, 4));

        Assert.Equal(ScreenshotRedactionStatus.NoScreenshot, outcome.Status);
        Assert.Empty(redactor.Requests);
    }

    [Fact]
    public async Task R15_解決できない_source_では_redactor_を呼ばない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var redactor = new FakeScreenshotRedactor();
        var coordinator = new ScreenshotRedactionCoordinator(redactor, ctx.Store, ctx.Workspace);
        var region = new RedactionRectangle(1, 1, 4, 4);

        // (a) 存在しない file
        var missing = await coordinator.RedactAsync(
            ctx.Project.Id, ctx.StepId, "screenshots/original/does-not-exist.png", region);
        Assert.Equal(ScreenshotRedactionStatus.InvalidSource, missing.Status);

        // (b) 脱出 path
        var escapes = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, "../evil.png", region);
        Assert.Equal(ScreenshotRedactionStatus.InvalidSource, escapes.Status);

        // (c) 絶対パス
        var absolute = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, ctx.OriginalAbsolute, region);
        Assert.Equal(ScreenshotRedactionStatus.InvalidSource, absolute.Status);

        // (d) 選択矩形が不正
        var badRegion = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(0, 0, 0, 0));
        Assert.Equal(ScreenshotRedactionStatus.InvalidSource, badRegion.Status);

        Assert.Empty(redactor.Requests);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(1, saved!.Revision);
        Assert.Equal(OriginalRelative, saved.Steps[0].ScreenshotPath);
    }

    // =====================================================================
    // R16 / R17 — CurrentProject の identity
    // =====================================================================

    [Fact]
    public async Task R16_同一_Project_なら_CurrentProject_が更新される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(await ctx.Store.LoadProjectAsync(ctx.Project.Id) ?? ctx.Project);
        var currentBefore = ctx.Current.CurrentProject;

        var coordinator = new ScreenshotRedactionCoordinator(new FakeScreenshotRedactor(), ctx.Store, ctx.Workspace);
        var outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(1, 1, 4, 4));

        Assert.Equal(ScreenshotRedactionStatus.Redacted, outcome.Status);
        Assert.NotSame(currentBefore, ctx.Current.CurrentProject);
        Assert.Equal(2, ctx.Current.CurrentProject!.Revision);
        Assert.Equal(outcome.EditedRelativePath, ctx.Current.CurrentProject.Steps[0].ScreenshotPath);
    }

    [Fact]
    public async Task R17_別_Project_が_current_なら_巻き戻さない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var other = await ctx.Store.CreateProjectAsync("別プロジェクト");
        ctx.Current.SetCurrent(other);
        var currentBefore = ctx.Current.CurrentProject;

        var coordinator = new ScreenshotRedactionCoordinator(new FakeScreenshotRedactor(), ctx.Store, ctx.Workspace);
        var outcome = await coordinator.RedactAsync(ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(1, 1, 4, 4));

        Assert.Equal(ScreenshotRedactionStatus.Redacted, outcome.Status);
        Assert.Same(currentBefore, ctx.Current.CurrentProject);

        // 対象 Project 自体の更新は成功している。
        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(2, saved!.Revision);
        Assert.Equal(outcome.EditedRelativePath, saved.Steps[0].ScreenshotPath);
    }

    // =====================================================================
    // R18 — caller の context（runtime smoke で見つかった thread affinity の regression）
    // =====================================================================

    /// <summary>単一 thread の UI context を模す（WPF Dispatcher の代わり）。Post は owner thread が消化する。</summary>
    private sealed class PumpingSynchronizationContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _queue = new();

        public int PostCount;

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref PostCount);
            _queue.Enqueue((d, state));
        }

        /// <summary>owner thread で queue を消化しながら task の完了を待つ（message loop 相当）。</summary>
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

    /// <summary>redactor の完了が別 thread になる fake（<c>Task.Run</c> 越しに実行する）。</summary>
    private sealed class OffloadingScreenshotRedactor : IScreenshotRedactor
    {
        private readonly FakeScreenshotRedactor _inner = new();

        public Task<ScreenshotRedactionResult> RedactAsync(
            ScreenshotRedactionRequest request,
            CancellationToken cancellationToken = default) =>
            Task.Run(() => _inner.RedactAsync(request, cancellationToken).GetAwaiter().GetResult());
    }

    /// <summary>
    /// Workspace 経由の CurrentProject 差し替えは View が観測するため UI thread で行う必要がある。
    /// coordinator が caller の context を離れずに戻ることを固定する
    /// （runtime smoke でこの乖離が「保存できませんでした」として現れた）。
    /// </summary>
    [Fact]
    public async Task R18_redactor_を_await_した後も_caller_の_context_で_Workspace_を更新する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(ctx.Project);

        var callerThread = Environment.CurrentManagedThreadId;
        var publishThread = -1;
        var publishCount = 0;
        ctx.Current.CurrentProjectChanged += (_, _) =>
        {
            publishThread = Environment.CurrentManagedThreadId;
            publishCount++;
        };

        var coordinator = new ScreenshotRedactionCoordinator(
            new OffloadingScreenshotRedactor(), ctx.Store, ctx.Workspace);

        var context = new PumpingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        Task<ScreenshotRedactionOutcome> task;
        try
        {
            task = coordinator.RedactAsync(
                ctx.Project.Id, ctx.StepId, OriginalRelative, new RedactionRectangle(1, 1, 4, 4));

            context.PumpUntil(task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var outcome = await task;

        Assert.Equal(ScreenshotRedactionStatus.Redacted, outcome.Status);
        Assert.True(
            Volatile.Read(ref context.PostCount) > 0,
            "coordinator が caller の context へ戻っていません（ConfigureAwait(false) の再導入で UI thread から外れます）。");
        Assert.Equal(1, publishCount);
        Assert.Equal(callerThread, publishThread);
    }
}
