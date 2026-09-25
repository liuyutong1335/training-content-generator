// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using TrainingContent.App.State;
using TrainingContent.Core.Models;
using TrainingContent.Storage;
using TrainingContent.Video;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// D: <see cref="VideoGenerationCoordinator"/> の progress forwarding と cancellation 分類（V1〜V13）。
///
/// <para>
/// composer は fake（Video Core の renderer logic は A の責務）。staging / canonical / metadata は
/// 実物の <see cref="VideoArtifactTransaction"/> と <see cref="ProjectStore"/> を temp root で通す
/// （cancel 時の rollback / staging cleanup は Storage の既存 semantics が authority であり、
/// coordinator がそれを壊していないことを file 単位で確認する）。
/// </para>
/// <para>
/// commit 内部での OCE（rollback 済み Failed）は実 filesystem では任意の時点に注入できないため、
/// V12 / V13 は <see cref="VideoGenerationCommitClassifier"/> を直接検証する。
/// </para>
/// </summary>
public class VideoGenerationCoordinatorTests
{
    private static readonly byte[] StagingBytes = [0x11, 0x22, 0x33, 0x44];
    private static readonly byte[] OldCanonicalBytes = [0xDE, 0xAD, 0xBE, 0xEF];
    private static readonly DateTimeOffset OldGeneratedAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

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

    /// <summary>compose の呼ばれ方と完了のさせ方を test が制御できる最小の composer。</summary>
    private sealed class FakeVideoComposer : IVideoComposer
    {
        public List<VideoCompositionOptions?> Options { get; } = [];

        /// <summary>null なら「成功し staging を書く」既定挙動。</summary>
        public Func<VideoCompositionRequest, VideoCompositionOptions?, CancellationToken, Task>? OnCompose { get; set; }

        public async Task<VideoCompositionResult> ComposeAsync(
            VideoCompositionRequest request,
            VideoCompositionOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Options.Add(options);

            if (OnCompose is not null)
            {
                await OnCompose(request, options, cancellationToken);
            }
            else
            {
                await File.WriteAllBytesAsync(request.OutputPath, StagingBytes, cancellationToken);
            }

            return new VideoCompositionResult(request.OutputPath, 1.0);
        }
    }

    private sealed class RecordedProgress : IProgress<VideoCompositionProgress>
    {
        public List<VideoCompositionProgress> Values { get; } = [];

        public void Report(VideoCompositionProgress value) => Values.Add(value);
    }

    private sealed record Context(
        TempProjectsRoot Temp,
        ProjectStore Store,
        CurrentProjectContext Current,
        VideoArtifactTransaction Transaction,
        TrainingProject Project);

    private static async Task<Context> SetupAsync(TempProjectsRoot temp, bool withExistingVideo = false)
    {
        var store = new ProjectStore(temp.Root);
        var current = new CurrentProjectContext();
        var transaction = new VideoArtifactTransaction(store);

        var project = await store.CreateProjectAsync("Video テスト");
        project.Recording = new RecordingInfo
        {
            MediaPath = ProjectStore.RecordingMediaPath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 5000,
            HasSystemAudio = true,
            HasMicrophone = false,
        };
        project.Steps.Add(new TrainingStep
        {
            Id = Guid.NewGuid(),
            Order = 1,
            StartMs = 0,
            EndMs = 1000,
            Action = StepActions.Click,
            Title = "手順 1",
            Target = "AlphaClick",
        });
        await store.SaveProjectAsync(project);

        // precondition は実 file の存在も見る。
        await File.WriteAllBytesAsync(store.GetRecordingOutputPath(project.Id), [0xAB, 0xCD]);

        if (withExistingVideo)
        {
            var canonical = transaction.CanonicalPath(project.Id);
            await File.WriteAllBytesAsync(canonical, OldCanonicalBytes);

            project.Outputs.TrainingVideo = new GeneratedArtifact
            {
                Path = VideoArtifactTransaction.CanonicalRelativePath,
                GeneratedAtUtc = OldGeneratedAt,
                SourceRevision = project.Revision,
            };
            await store.SaveProjectAsync(project);
        }

        var persisted = await store.LoadProjectAsync(project.Id)
            ?? throw new InvalidOperationException("test 前提が壊れています。");

        return new Context(temp, store, current, transaction, persisted);
    }

    private static VideoGenerationCoordinator CreateCoordinator(Context ctx, FakeVideoComposer composer) =>
        new(ctx.Store, ctx.Current, ctx.Transaction, () => composer);

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("test の待機条件が成立しませんでした。");
            }

            await Task.Delay(10);
        }
    }

    /// <summary>transaction workspace に child（staging / backup / 別 transaction）が残っていないこと。</summary>
    private static void AssertNoTransactionLeftovers(VideoArtifactTransaction transaction, Guid projectId)
    {
        var projectTransactions = Path.Combine(transaction.TransactionsRoot, projectId.ToString("D"));
        if (!Directory.Exists(projectTransactions))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(projectTransactions));
    }

    /// <summary>compose 中に cancel される生成を開始し、composer が入ったことを確認してから cancel する。</summary>
    private static async Task<(Task<VideoGenerationResult> Task, CancellationTokenSource Cts)> StartAndCancelDuringComposeAsync(
        VideoGenerationCoordinator coordinator,
        FakeVideoComposer composer,
        Guid projectId)
    {
        var cts = new CancellationTokenSource();

        composer.OnCompose = async (_, _, ct) => await Task.Delay(Timeout.Infinite, ct);

        var task = coordinator.GenerateAsync(projectId, progress: null, cts.Token);
        await WaitUntilAsync(() => composer.Options.Count == 1);
        cts.Cancel();

        return (task, cts);
    }

    // =====================================================================
    // V1 / V2 — progress の受け渡しと forward
    // =====================================================================

    [Fact]
    public async Task V1_GenerateAsync_は_progress_を付けた_VideoCompositionOptions_を_composer_へ渡す()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();
        var progress = new RecordedProgress();

        var result = await CreateCoordinator(ctx, composer).GenerateAsync(ctx.Project.Id, progress);

        Assert.Equal(VideoGenerationStatus.Generated, result.Status);
        var options = Assert.Single(composer.Options);
        Assert.NotNull(options);
        Assert.Same(progress, options!.Progress);

        // MVP のその他 options は既定値のまま（UI から expose しない）。
        Assert.Null(options.TitleText);
        Assert.Equal(3.0, options.TitleSeconds);
        Assert.Equal(2.0, options.EndingSeconds);
        Assert.Equal(4.0, options.FallbackStepSeconds);
        Assert.Equal("libopenh264", options.Encoder);
    }

    [Fact]
    public async Task V2_composer_の_progress_は_加工されずに_caller_へ_forward_される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();

        var reported = new[]
        {
            new VideoCompositionProgress(VideoCompositionStage.AnalyzingInput, "録画を解析しています…", 0.0),
            new VideoCompositionProgress(VideoCompositionStage.BurningSubtitles, "Step 字幕を焼き込んでいます…", 0.42),
            new VideoCompositionProgress(VideoCompositionStage.Finalizing, "完了", 1.0),
        };

        composer.OnCompose = async (request, options, _) =>
        {
            foreach (var value in reported)
            {
                options!.Progress!.Report(value);
            }

            await File.WriteAllBytesAsync(request.OutputPath, StagingBytes);
        };

        var progress = new RecordedProgress();
        var result = await CreateCoordinator(ctx, composer).GenerateAsync(ctx.Project.Id, progress);

        Assert.Equal(VideoGenerationStatus.Generated, result.Status);
        Assert.Equal(reported, progress.Values);
    }

    // =====================================================================
    // V3 / V4 / V5 — compose 中の cancel
    // =====================================================================

    [Fact]
    public async Task V3_compose_中の_cancel_は_Cancelled_になる()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();

        var (task, cts) = await StartAndCancelDuringComposeAsync(CreateCoordinator(ctx, composer), composer, ctx.Project.Id);
        var result = await task;
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task V4_compose_の_cancel_で_staging_が残らない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();

        var (task, cts) = await StartAndCancelDuringComposeAsync(CreateCoordinator(ctx, composer), composer, ctx.Project.Id);
        var result = await task;
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);
        AssertNoTransactionLeftovers(ctx.Transaction, ctx.Project.Id);
    }

    [Fact]
    public async Task V5_新規生成の_cancel_では_canonical_が作られず_metadata_も変わらない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();

        var (task, cts) = await StartAndCancelDuringComposeAsync(CreateCoordinator(ctx, composer), composer, ctx.Project.Id);
        var result = await task;
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);
        Assert.False(File.Exists(ctx.Transaction.CanonicalPath(ctx.Project.Id)));

        var reloaded = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Null(reloaded!.Outputs.TrainingVideo);
    }

    // =====================================================================
    // V6〜V8 — cancel が既存 state を壊さないこと
    // =====================================================================

    [Fact]
    public async Task V6_再生成の_cancel_では_旧_canonical_と_旧_metadata_が保持される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp, withExistingVideo: true);
        var composer = new FakeVideoComposer();

        var (task, cts) = await StartAndCancelDuringComposeAsync(CreateCoordinator(ctx, composer), composer, ctx.Project.Id);
        var result = await task;
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);
        Assert.Equal(OldCanonicalBytes, await File.ReadAllBytesAsync(ctx.Transaction.CanonicalPath(ctx.Project.Id)));

        var reloaded = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        var artifact = reloaded!.Outputs.TrainingVideo;
        Assert.NotNull(artifact);
        Assert.Equal(VideoArtifactTransaction.CanonicalRelativePath, artifact!.Path);
        Assert.Equal(OldGeneratedAt, artifact.GeneratedAtUtc);
        Assert.Equal(ctx.Project.Revision, artifact.SourceRevision);
    }

    [Fact]
    public async Task V7_cancel_では_Revision_と_UpdatedAtUtc_が変わらない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();
        var revisionBefore = ctx.Project.Revision;
        var updatedBefore = ctx.Project.UpdatedAtUtc;

        var (task, cts) = await StartAndCancelDuringComposeAsync(CreateCoordinator(ctx, composer), composer, ctx.Project.Id);
        var result = await task;
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);

        var reloaded = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(revisionBefore, reloaded!.Revision);
        Assert.Equal(updatedBefore, reloaded.UpdatedAtUtc);
    }

    [Fact]
    public async Task V8_cancel_では_CurrentProject_が差し替わらない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();
        ctx.Current.SetCurrent(ctx.Project);
        var currentBefore = ctx.Current.CurrentProject;

        var (task, cts) = await StartAndCancelDuringComposeAsync(CreateCoordinator(ctx, composer), composer, ctx.Project.Id);
        var result = await task;
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);
        Assert.Same(currentBefore, ctx.Current.CurrentProject);
    }

    // =====================================================================
    // V9 / V10 — 既存 behavior の維持
    // =====================================================================

    [Fact]
    public async Task V9_成功時は_Generated_で_CurrentProject_が最新_snapshot_へ更新される()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();
        ctx.Current.SetCurrent(ctx.Project);
        var currentBefore = ctx.Current.CurrentProject;

        var result = await CreateCoordinator(ctx, composer).GenerateAsync(ctx.Project.Id);

        Assert.Equal(VideoGenerationStatus.Generated, result.Status);
        Assert.Equal(ctx.Transaction.CanonicalPath(ctx.Project.Id), result.CanonicalPath);
        Assert.True(File.Exists(result.CanonicalPath));
        AssertNoTransactionLeftovers(ctx.Transaction, ctx.Project.Id);

        // CurrentProject は同じ Project の新しい snapshot へ差し替わる（Revision は進めない）。
        Assert.NotSame(currentBefore, ctx.Current.CurrentProject);
        Assert.Equal(ctx.Project.Revision, ctx.Current.CurrentProject!.Revision);
        Assert.Equal(
            ctx.Project.Revision,
            ctx.Current.CurrentProject.Outputs.TrainingVideo!.SourceRevision);
    }

    [Fact]
    public async Task V10_生成中に_Revision_が変わったら_SourceChanged_で_commit_しない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp, withExistingVideo: true);
        var composer = new FakeVideoComposer();

        composer.OnCompose = async (request, _, _) =>
        {
            await File.WriteAllBytesAsync(request.OutputPath, StagingBytes);

            // 生成中に teaching content が編集された状況を作る。
            var edited = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
            edited!.Revision += 1;
            await ctx.Store.SaveProjectAsync(edited);
        };

        var result = await CreateCoordinator(ctx, composer).GenerateAsync(ctx.Project.Id);

        Assert.Equal(VideoGenerationStatus.SourceChanged, result.Status);

        // canonical も metadata も旧状態のまま（staging は破棄）。
        Assert.Equal(OldCanonicalBytes, await File.ReadAllBytesAsync(ctx.Transaction.CanonicalPath(ctx.Project.Id)));
        var reloaded = await ctx.Store.LoadProjectAsync(ctx.Project.Id);
        Assert.Equal(OldGeneratedAt, reloaded!.Outputs.TrainingVideo!.GeneratedAtUtc);
        AssertNoTransactionLeftovers(ctx.Transaction, ctx.Project.Id);
    }

    // =====================================================================
    // V11 — commit が OCE を throw する cancel（canonical mutation 前）
    // =====================================================================

    [Fact]
    public async Task V11_commit_中の_cancel_は_Cancelled_で_staging_が残らない()
    {
        using var temp = new TempProjectsRoot();
        var ctx = await SetupAsync(temp);
        var composer = new FakeVideoComposer();

        var cts = new CancellationTokenSource();

        // staging を書いた後に cancel する → CommitAsync の project 再 load が OCE で落ちる
        // （canonical には一切触れていない = transaction の Case A）。
        composer.OnCompose = async (request, _, _) =>
        {
            await File.WriteAllBytesAsync(request.OutputPath, StagingBytes);
            cts.Cancel();
        };

        var result = await CreateCoordinator(ctx, composer).GenerateAsync(ctx.Project.Id, progress: null, cts.Token);
        cts.Dispose();

        Assert.Equal(VideoGenerationStatus.Cancelled, result.Status);
        Assert.Null(result.Error);
        Assert.False(File.Exists(ctx.Transaction.CanonicalPath(ctx.Project.Id)));
        AssertNoTransactionLeftovers(ctx.Transaction, ctx.Project.Id);
    }

    // =====================================================================
    // V12 / V13 — commit 結果の分類（rollback 済み OCE は cancel / recovery は failure）
    // =====================================================================

    [Fact]
    public void V12_rollback_済みの_OCE_failure_は_Cancelled_へ分類される()
    {
        var cancelled = new VideoArtifactCommitResult(
            VideoArtifactCommitStatus.Failed,
            ErrorMessage: "rollback 済み",
            Error: new OperationCanceledException());

        Assert.True(VideoGenerationCommitClassifier.IsCancellation(cancelled));
        Assert.Equal(VideoGenerationStatus.Cancelled, VideoGenerationCommitClassifier.ToStatus(cancelled));
    }

    [Fact]
    public void V13_RecoveryRequired_な失敗は_Cancelled_へ丸めない()
    {
        var recovery = new VideoArtifactCommitResult(
            VideoArtifactCommitStatus.Failed,
            ErrorMessage: "rollback に失敗",
            Error: new OperationCanceledException(),
            RecoveryRequired: true,
            RecoveryDirectory: @"C:\transactions\recovery");

        Assert.False(VideoGenerationCommitClassifier.IsCancellation(recovery));
        Assert.Equal(VideoGenerationStatus.Failed, VideoGenerationCommitClassifier.ToStatus(recovery));
    }

    [Fact]
    public void V13b_OCE_以外の失敗と既存_status_の分類は変わらない()
    {
        var failed = new VideoArtifactCommitResult(
            VideoArtifactCommitStatus.Failed,
            ErrorMessage: "disk full",
            Error: new IOException("disk full"));

        Assert.False(VideoGenerationCommitClassifier.IsCancellation(failed));
        Assert.Equal(VideoGenerationStatus.Failed, VideoGenerationCommitClassifier.ToStatus(failed));

        Assert.Equal(
            VideoGenerationStatus.Failed,
            VideoGenerationCommitClassifier.ToStatus(
                new VideoArtifactCommitResult(VideoArtifactCommitStatus.StagingMissing)));

        Assert.Equal(
            VideoGenerationStatus.ProjectNotFound,
            VideoGenerationCommitClassifier.ToStatus(
                new VideoArtifactCommitResult(VideoArtifactCommitStatus.ProjectNotFound)));

        Assert.Equal(
            VideoGenerationStatus.SourceChanged,
            VideoGenerationCommitClassifier.ToStatus(
                new VideoArtifactCommitResult(VideoArtifactCommitStatus.SourceChanged)));

        // Committed はこの classifier の対象外（coordinator は成功経路へ分岐する）。
        Assert.False(VideoGenerationCommitClassifier.IsCancellation(
            new VideoArtifactCommitResult(VideoArtifactCommitStatus.Committed)));
    }
}
