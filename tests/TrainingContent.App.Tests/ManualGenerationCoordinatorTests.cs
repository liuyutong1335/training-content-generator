// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Collections.Concurrent;
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Manual;
using TrainingContent.Storage;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// G: <see cref="ManualGenerationCoordinator"/> の orchestration（G1〜G14）。
///
/// <para>
/// Manual Core（<see cref="ManualGenerator"/>）は実物を delegate 経由で渡し、失敗経路（path 不一致 / Error）だけを
/// test が注入する。persistence は実物の <see cref="ManualArtifactTransaction"/> と <see cref="ProjectStore"/> を
/// temp root で通す（pair atomicity / rollback は Storage 側 test が authority）。
/// </para>
/// </summary>
public class ManualGenerationCoordinatorTests
{
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
        ProjectStore Store,
        CurrentProjectContext Current,
        ManualArtifactTransaction Transaction,
        TrainingProject Project);

    private static async Task<Context> SetupAsync(TempProjectsRoot temp, bool withSteps = true)
    {
        var store = new ProjectStore(temp.Root);
        var current = new CurrentProjectContext();
        var transaction = new ManualArtifactTransaction(store);

        var project = await store.CreateProjectAsync("Manual 生成テスト");
        if (withSteps)
        {
            project.Steps.Add(new TrainingStep
            {
                Id = Guid.NewGuid(),
                Order = 1,
                StartMs = 0,
                Action = StepActions.Click,
                Title = "手順 1",
                Target = "AlphaClick",
            });
        }

        await store.SaveProjectAsync(project);

        var persisted = await store.LoadProjectAsync(project.Id)
            ?? throw new InvalidOperationException("test 前提が壊れています。");

        return new Context(store, current, transaction, persisted);
    }

    private static ManualGenerationCoordinator CreateCoordinator(
        Context ctx,
        Func<TrainingProject, ManualGenerationResult>? generator = null) =>
        new(ctx.Store, ctx.Current, ctx.Transaction, generator ?? ManualGenerator.Generate);

    private static string CanonicalMarkdown(Context ctx) =>
        Path.Combine(ctx.Store.GetProjectDirectory(ctx.Project.Id), "manual", "manual.md");

    private static string CanonicalHtml(Context ctx) =>
        Path.Combine(ctx.Store.GetProjectDirectory(ctx.Project.Id), "manual", "manual.html");

    private static void AssertNoTransactionLeftovers(Context ctx)
    {
        var projectTransactions = Path.Combine(ctx.Transaction.TransactionsRoot, ctx.Project.Id.ToString("D"));
        if (!Directory.Exists(projectTransactions))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(projectTransactions));
    }

    /// <summary>単一 thread の UI context を模す（C の R18 と同型）。Post は owner thread が消化する。</summary>
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

    // =====================================================================
    // G1 / G2 — precondition
    // =====================================================================

    [Fact]
    public async Task G1_Project_が無ければ_ProjectNotFound()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var outcome = await CreateCoordinator(ctx).GenerateAsync(Guid.NewGuid());

        Assert.Equal(ManualGenerationStatus.ProjectNotFound, outcome.Status);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public async Task G2_Steps_が無ければ_StepsMissing_で_generator_を呼ばない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp, withSteps: false);
        var generatorCalls = 0;

        var outcome = await CreateCoordinator(ctx, project =>
        {
            generatorCalls++;
            return ManualGenerator.Generate(project);
        }).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.StepsMissing, outcome.Status);
        Assert.Equal(0, generatorCalls);

        // staging すら作らない（transaction workspace に何も残らない）。
        AssertNoTransactionLeftovers(ctx);
        Assert.False(File.Exists(CanonicalMarkdown(ctx)));
    }

    // =====================================================================
    // G3 / G4 — 正常系
    // =====================================================================

    [Fact]
    public async Task G3_生成された_Markdown_と_Html_がそのまま_canonical_へ書かれる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var expected = ManualGenerator.Generate(ctx.Project);

        var outcome = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Generated, outcome.Status);
        Assert.True(expected.Markdown is not null && expected.Html is not null);

        var markdown = await File.ReadAllTextAsync(CanonicalMarkdown(ctx));
        var html = await File.ReadAllTextAsync(CanonicalHtml(ctx));

        Assert.Equal(expected.Markdown!.Content, markdown);
        Assert.Equal(expected.Html!.Content, html);

        // UTF-8（BOM なし）で書く。
        var bytes = await File.ReadAllBytesAsync(CanonicalMarkdown(ctx));
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
    }

    [Fact]
    public async Task G4_generator_が返す_path_は固定_canonical_path_と一致する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var generated = ManualGenerator.Generate(ctx.Project);

        Assert.Equal(ManualArtifactTransaction.MarkdownRelativePath, generated.Markdown!.Path);
        Assert.Equal(ManualArtifactTransaction.HtmlRelativePath, generated.Html!.Path);
    }

    [Fact]
    public async Task G5_固定_path_以外を返したら_fail_closed_で_filesystem_へ書かない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var escapePath = Path.Combine(temp.Root, "evil.md");

        var outcome = await CreateCoordinator(ctx, project =>
        {
            var result = ManualGenerator.Generate(project);
            return new ManualGenerationResult
            {
                Markdown = new ManualGeneratedFile { Path = "../evil.md", Content = "evil" },
                Html = result.Html,
            };
        }).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Failed, outcome.Status);
        Assert.False(File.Exists(escapePath), "arbitrary path へ file が書かれた");
        Assert.False(File.Exists(CanonicalMarkdown(ctx)));
        Assert.False(File.Exists(CanonicalHtml(ctx)));
        AssertNoTransactionLeftovers(ctx);
    }

    // =====================================================================
    // G6 / G7 — metadata と Revision
    // =====================================================================

    [Fact]
    public async Task G6_成功時は_pair_の_Outputs_metadata_が書かれる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var outcome = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Generated, outcome.Status);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        var markdown = saved!.Outputs.ManualMarkdown;
        var html = saved.Outputs.ManualHtml;

        Assert.NotNull(markdown);
        Assert.NotNull(html);
        Assert.Equal(ManualArtifactTransaction.MarkdownRelativePath, markdown!.Path);
        Assert.Equal(ManualArtifactTransaction.HtmlRelativePath, html!.Path);
        Assert.Equal(markdown.GeneratedAtUtc, html.GeneratedAtUtc);
        Assert.Equal(markdown.SourceRevision, html.SourceRevision);
        Assert.Equal(ctx.Project.Revision, markdown.SourceRevision);
    }

    [Fact]
    public async Task G7_生成しても_Revision_は増えない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var revisionBefore = ctx.Project.Revision;

        await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

        var saved = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(revisionBefore, saved!.Revision);
    }

    // =====================================================================
    // G8 / G9 / G12 — CurrentProject identity
    // =====================================================================

    [Fact]
    public async Task G8_同一_Project_が_current_なら_CurrentProject_が更新される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(ctx.Project);
        var before = ctx.Current.CurrentProject;

        var outcome = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Generated, outcome.Status);
        Assert.NotSame(before, ctx.Current.CurrentProject);
        Assert.Equal(ManualArtifactTransaction.MarkdownRelativePath, ctx.Current.CurrentProject!.Outputs.ManualMarkdown!.Path);
    }

    [Fact]
    public async Task G9_別_Project_が_current_なら_上書きしない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var other = await ctx.Store.CreateProjectAsync("別プロジェクト");
        ctx.Current.SetCurrent(other);
        var before = ctx.Current.CurrentProject;

        var outcome = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Generated, outcome.Status);
        Assert.Same(before, ctx.Current.CurrentProject);
    }

    [Fact]
    public async Task G12_transaction_が失敗したら_Failed_で_CurrentProject_は変わらない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(ctx.Project);
        var before = ctx.Current.CurrentProject;

        // project.json を掴んで commit の Save を失敗させる。
        using var lockStream = new FileStream(
            Path.Combine(ctx.Store.GetProjectDirectory(ctx.Project.Id), ProjectStore.ProjectFileName),
            FileMode.Open, FileAccess.Read, FileShare.Read);

        var outcome = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Failed, outcome.Status);
        Assert.Same(before, ctx.Current.CurrentProject);
        Assert.False(File.Exists(CanonicalMarkdown(ctx)));
        AssertNoTransactionLeftovers(ctx);
    }

    // =====================================================================
    // G10 — Revision gate
    // =====================================================================

    [Fact]
    public async Task G10_生成中に_Revision_が変わったら_SourceChanged_で_artifact_を保持する()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        // 既存の manual pair を先に作る。
        var first = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);
        Assert.Equal(ManualGenerationStatus.Generated, first.Status);
        var markdownBefore = await File.ReadAllTextAsync(CanonicalMarkdown(ctx));
        var metadataBefore = (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Outputs.ManualMarkdown!.GeneratedAtUtc;

        // 生成中に teaching content が編集された状況（generator の中で Revision を進める）。
        var generator = new Func<TrainingProject, ManualGenerationResult>(project =>
        {
            var edited = ctx.Store.LoadProjectAsync(project.Id).GetAwaiter().GetResult()!;
            edited.Revision += 1;
            ctx.Store.SaveProjectAsync(edited).GetAwaiter().GetResult();
            return ManualGenerator.Generate(project);
        });

        var outcome = await CreateCoordinator(ctx, generator).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.SourceChanged, outcome.Status);
        Assert.Equal(markdownBefore, await File.ReadAllTextAsync(CanonicalMarkdown(ctx)));
        Assert.Equal(metadataBefore, (await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Outputs.ManualMarkdown!.GeneratedAtUtc);
        AssertNoTransactionLeftovers(ctx);
    }

    // =====================================================================
    // G11 / G13 — Manual Core の拒否と recovery
    // =====================================================================

    [Fact]
    public async Task G11_Manual_Core_が_Error_を返したら_GenerationRejected_で_canonical_を触らない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var outcome = await CreateCoordinator(ctx, _ => new ManualGenerationResult
        {
            Errors = ["手順 1 の Title が空です。"],
        }).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.GenerationRejected, outcome.Status);
        Assert.Equal(["手順 1 の Title が空です。"], outcome.Errors);
        Assert.False(File.Exists(CanonicalMarkdown(ctx)));
        Assert.False(File.Exists(CanonicalHtml(ctx)));
        Assert.Null((await ctx.Store.LoadProjectAsync(ctx.Project.Id))!.Outputs.ManualMarkdown);
        AssertNoTransactionLeftovers(ctx);
    }

    [Fact]
    public async Task G11b_Markdown_か_Html_が_null_なら_Failed()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var outcome = await CreateCoordinator(ctx, project => new ManualGenerationResult
        {
            Markdown = ManualGenerator.Generate(project).Markdown,
            Html = null,
        }).GenerateAsync(ctx.Project.Id);

        Assert.Equal(ManualGenerationStatus.Failed, outcome.Status);
        Assert.False(File.Exists(CanonicalMarkdown(ctx)));
        AssertNoTransactionLeftovers(ctx);
    }

    [Fact]
    public async Task G13_rollback_失敗時は_recovery_directory_が_outcome_に残る()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);

        var first = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);
        Assert.Equal(ManualGenerationStatus.Generated, first.Status);

        // canonical markdown を掴むと置換も restore も失敗し、recovery backup が残る。
        using (new FileStream(CanonicalMarkdown(ctx), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var outcome = await CreateCoordinator(ctx).GenerateAsync(ctx.Project.Id);

            Assert.Equal(ManualGenerationStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.RecoveryDirectory);
            Assert.True(Directory.Exists(outcome.RecoveryDirectory));
        }

        // recovery material が残っていることを確認（DiscardStaging が呼ばれても消えない）。
        var projectTransactions = Path.Combine(ctx.Transaction.TransactionsRoot, ctx.Project.Id.ToString("D"));
        Assert.True(Directory.Exists(projectTransactions));
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(projectTransactions));
    }

    // =====================================================================
    // G14 — thread affinity（C の R18 と同型）
    // =====================================================================

    [Fact]
    public async Task G14_CurrentProject_の_publish_は_caller_の_context_で行われる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        ctx.Current.SetCurrent(ctx.Project);

        var callerThread = Environment.CurrentManagedThreadId;
        var publishThread = -1;
        ctx.Current.CurrentProjectChanged += (_, _) => publishThread = Environment.CurrentManagedThreadId;

        var coordinator = CreateCoordinator(ctx);
        var context = new PumpingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(context);

        Task<ManualGenerationOutcome> task;
        try
        {
            task = coordinator.GenerateAsync(ctx.Project.Id);
            context.PumpUntil(task);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var outcome = await task;

        Assert.Equal(ManualGenerationStatus.Generated, outcome.Status);
        Assert.True(
            Volatile.Read(ref context.PostCount) > 0,
            "coordinator が caller の context へ戻っていません（UI thread から外れています）。");
        Assert.Equal(callerThread, publishThread);
    }
}
