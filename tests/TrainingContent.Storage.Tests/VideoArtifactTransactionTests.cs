using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// D6-V1 Video artifact の staged replacement のテスト。
/// 実ユーザーの Projects directory や repository の projects/ は使わず、必ず temp directory を使う。
///
/// <para>
/// 対象は filesystem replacement と project.json metadata commit の transaction boundary のみ。
/// ffmpeg を使う合成そのものは対象外（<see cref="VideoArtifactTransaction"/> は staging file を
/// 受け取るだけなので、ここでは任意の bytes を「生成済み動画」として扱う）。
/// </para>
/// </summary>
public class VideoArtifactTransactionTests
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

    private static byte[] VideoBytes(byte fill = 0xAB, int length = 2048)
    {
        var bytes = new byte[length];
        Array.Fill(bytes, fill);
        return bytes;
    }

    /// <summary>staging に「生成済み動画」を置いて返す。</summary>
    private static async Task<VideoArtifactStaging> StageAsync(
        VideoArtifactTransaction transaction,
        Guid projectId,
        byte fill = 0xAB)
    {
        var staging = transaction.BeginStaging(projectId);
        await File.WriteAllBytesAsync(staging.StagingPath, VideoBytes(fill));
        return staging;
    }

    /// <summary>
    /// project.json を保持し、Save を必ず失敗させる。
    /// <para>
    /// <see cref="FileShare.Read"/> にするのが要点——<see cref="VideoArtifactTransaction.CommitAsync"/> は
    /// 最初に project.json を <b>読み込む</b>ので、<see cref="FileShare.None"/> にすると Load の時点で
    /// 失敗してしまい「Save 失敗時の rollback」を検証できない。
    /// 読みは許可しつつ <c>FileShare.Delete</c> を与えないため、Save 側の置換（File.Move overwrite）だけが失敗する。
    /// </para>
    /// </summary>
    private static FileStream LockProjectJson(ProjectStore store, Guid projectId) =>
        new(
            Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

    // =====================================================================
    // 配置 — staging は canonical Project directory の外（ProjectsRoot の sibling）
    // =====================================================================
    [Fact]
    public void T_D6V1_00_TransactionWorkspace_IsOutsideProjectDirectory()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);

        Assert.Equal(
            Path.Combine(Path.GetDirectoryName(store.ProjectsRoot)!, "transactions"),
            transaction.TransactionsRoot);

        // project directory 配下ではない（Contract §17 の directory 構造を変更しない）
        Assert.False(transaction.TransactionsRoot.StartsWith(
            store.ProjectsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    // =====================================================================
    // 1. 既存 video なし → commit 成功
    // =====================================================================
    [Fact]
    public async Task T_D6V1_01_Commit_WithoutExistingVideo_CreatesCanonical()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("動画生成");

        var staged = VideoBytes();
        var staging = transaction.BeginStaging(project.Id);
        await File.WriteAllBytesAsync(staging.StagingPath, staged);

        var result = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(VideoArtifactCommitStatus.Committed, result.Status);
        Assert.True(File.Exists(staging.CanonicalPath));
        Assert.Equal(staged, await File.ReadAllBytesAsync(staging.CanonicalPath));
    }

    // =====================================================================
    // 2. 既存 video あり → 置換成功
    // =====================================================================
    [Fact]
    public async Task T_D6V1_02_Commit_WithExistingVideo_ReplacesCanonical()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("再生成");

        var oldBytes = VideoBytes(0x11);
        var newBytes = VideoBytes(0x22);
        var canonical = transaction.CanonicalPath(project.Id);
        await File.WriteAllBytesAsync(canonical, oldBytes);

        var staging = transaction.BeginStaging(project.Id);
        await File.WriteAllBytesAsync(staging.StagingPath, newBytes);

        var result = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(VideoArtifactCommitStatus.Committed, result.Status);
        Assert.Equal(newBytes, await File.ReadAllBytesAsync(canonical));
    }

    // =====================================================================
    // 3. SourceRevision metadata / Revision は進めない
    // =====================================================================
    [Fact]
    public async Task T_D6V1_03_Commit_WritesTrainingVideoMetadata_WithoutRevisionIncrement()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("metadata");
        var sourceRevision = project.Revision;

        var staging = await StageAsync(transaction, project.Id);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var result = await transaction.CommitAsync(staging, sourceRevision);

        Assert.Equal(VideoArtifactCommitStatus.Committed, result.Status);

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.NotNull(loaded);
        var video = loaded!.Outputs.TrainingVideo;
        Assert.NotNull(video);
        Assert.Equal(VideoArtifactTransaction.CanonicalRelativePath, video!.Path);
        Assert.Equal(sourceRevision, video.SourceRevision);
        Assert.True(video.GeneratedAtUtc >= before, "GeneratedAtUtc が更新されていない");

        // output generation は teaching content の編集ではないので Revision を進めない
        Assert.Equal(sourceRevision, loaded.Revision);
    }

    // =====================================================================
    // 4. Revision mismatch → canonical / metadata とも変更しない
    // =====================================================================
    [Fact]
    public async Task T_D6V1_04_Commit_RevisionMismatch_LeavesCanonicalAndMetadataUnchanged()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("生成中に編集");

        var oldBytes = VideoBytes(0x33);
        var canonical = transaction.CanonicalPath(project.Id);
        await File.WriteAllBytesAsync(canonical, oldBytes);

        var staging = await StageAsync(transaction, project.Id, 0x44);

        // 生成中に Revision が進んだ状況を再現（開始時 = Revision - 1）
        var result = await transaction.CommitAsync(staging, sourceRevision: project.Revision + 1);

        Assert.Equal(VideoArtifactCommitStatus.SourceChanged, result.Status);
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(canonical));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Null(loaded!.Outputs.TrainingVideo);
    }

    // =====================================================================
    // 5. metadata Save 失敗 → 旧 video を rollback（既存 video あり）
    // =====================================================================
    [Fact]
    public async Task T_D6V1_05_Commit_SaveFailure_RestoresOldVideo()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("Save 失敗");

        var oldBytes = VideoBytes(0x55);
        var canonical = transaction.CanonicalPath(project.Id);
        await File.WriteAllBytesAsync(canonical, oldBytes);

        var staging = await StageAsync(transaction, project.Id, 0x66);

        VideoArtifactCommitResult result;
        using (LockProjectJson(store, project.Id))
        {
            result = await transaction.CommitAsync(staging, project.Revision);
        }

        Assert.Equal(VideoArtifactCommitStatus.Failed, result.Status);
        Assert.True(File.Exists(canonical), "旧 video が失われている");
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(canonical));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Null(loaded!.Outputs.TrainingVideo);
    }

    // =====================================================================
    // 6. metadata Save 失敗 → 新規 canonical は残さない（既存 video なし）
    // =====================================================================
    [Fact]
    public async Task T_D6V1_06_Commit_SaveFailure_WithoutExistingVideo_LeavesCanonicalAbsent()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("Save 失敗（新規）");

        var staging = await StageAsync(transaction, project.Id);
        Assert.False(File.Exists(staging.CanonicalPath));

        VideoArtifactCommitResult result;
        using (LockProjectJson(store, project.Id))
        {
            result = await transaction.CommitAsync(staging, project.Revision);
        }

        Assert.Equal(VideoArtifactCommitStatus.Failed, result.Status);
        Assert.False(File.Exists(staging.CanonicalPath), "失敗した生成物が canonical に残っている");
    }

    // =====================================================================
    // 7. transaction directory の cleanup
    // =====================================================================
    [Fact]
    public async Task T_D6V1_07_TransactionDirectory_IsCleanedUp()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("cleanup");

        // 成功時
        var committed = await StageAsync(transaction, project.Id);
        Assert.True(Directory.Exists(committed.TransactionDirectory));
        await transaction.CommitAsync(committed, project.Revision);
        Assert.False(Directory.Exists(committed.TransactionDirectory));

        // 生成失敗時（DiscardStaging）
        var discarded = await StageAsync(transaction, project.Id, 0x77);
        transaction.DiscardStaging(discarded);
        Assert.False(Directory.Exists(discarded.TransactionDirectory));

        // Project directory 配下に staging を作っていない
        Assert.Empty(Directory.GetDirectories(store.GetProjectDirectory(project.Id), "staging"));
        Assert.Empty(Directory.GetDirectories(store.GetProjectDirectory(project.Id), "backup"));
    }

    // =====================================================================
    // 8. 置換（staged → canonical）自体の失敗 → 旧 canonical を復元
    //
    // replacement より後（SaveProjectAsync）で失敗する 05/06 とは別の failure point。
    // staged file を掴んで File.Move の source 側を rename 不能にする（canonical は free なので
    // rollback の restore は成功する）。
    // =====================================================================
    [Fact]
    public async Task T_D6V1_08_Commit_ReplacementFailure_RestoresOldVideo()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("置換失敗");

        var oldBytes = VideoBytes(0x88);
        var canonical = transaction.CanonicalPath(project.Id);
        await File.WriteAllBytesAsync(canonical, oldBytes);

        var staging = await StageAsync(transaction, project.Id, 0x99);

        VideoArtifactCommitResult result;
        using (new FileStream(staging.StagingPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await transaction.CommitAsync(staging, project.Revision);
        }

        Assert.Equal(VideoArtifactCommitStatus.Failed, result.Status);
        Assert.False(result.RecoveryRequired, "rollback は成功しているはず");
        Assert.True(File.Exists(canonical), "旧 canonical が失われている");
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(canonical));

        var loaded = await store.LoadProjectAsync(project.Id);
        Assert.Null(loaded!.Outputs.TrainingVideo);
    }

    // =====================================================================
    // 9. rollback 失敗 → recovery backup の保全（最重要）
    //
    // canonical を掴むと置換も restore も失敗し、backup が旧 canonical の唯一の複製になる。
    // その状態で Coordinator が呼ぶ DiscardStaging を実行しても消えてはならない。
    // =====================================================================
    [Fact]
    public async Task T_D6V1_09_Commit_RollbackFailure_PreservesRecoveryBackup()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("rollback 失敗");

        var oldBytes = VideoBytes(0xAA);
        var canonical = transaction.CanonicalPath(project.Id);
        await File.WriteAllBytesAsync(canonical, oldBytes);

        var staging = await StageAsync(transaction, project.Id, 0xBB);

        using (new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = await transaction.CommitAsync(staging, project.Revision);

            Assert.Equal(VideoArtifactCommitStatus.Failed, result.Status);
            Assert.NotNull(result.Error);
            Assert.True(result.RecoveryRequired);
            Assert.Equal(staging.TransactionDirectory, result.RecoveryDirectory);
            Assert.True(File.Exists(staging.BackupPath), "recovery backup が残っていない");
            Assert.True(Directory.Exists(staging.TransactionDirectory));

            // Coordinator が呼ぶ DiscardStaging でも削除されない（defense-in-depth）
            transaction.DiscardStaging(staging);
            Assert.True(File.Exists(staging.BackupPath), "DiscardStaging が recovery backup を削除した");
            Assert.True(Directory.Exists(staging.TransactionDirectory));
        }

        // lock 解放後は backup から旧 canonical を復旧できる
        Assert.Equal(oldBytes, await File.ReadAllBytesAsync(staging.BackupPath));
    }

    // =====================================================================
    // 10. cleanup の 3 状態 — 成功 / rollback 成功 / rollback 失敗
    // =====================================================================
    [Fact]
    public async Task T_D6V1_10_Cleanup_DistinguishesThreeStates()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var transaction = new VideoArtifactTransaction(store);
        var project = await store.CreateProjectAsync("cleanup 3 状態");
        var canonical = transaction.CanonicalPath(project.Id);

        // (a) 成功時（既存 canonical あり）: workspace ごと片付く
        await File.WriteAllBytesAsync(canonical, VideoBytes(0x01));
        var ok = await StageAsync(transaction, project.Id, 0x02);
        var okResult = await transaction.CommitAsync(ok, project.Revision);
        Assert.Equal(VideoArtifactCommitStatus.Committed, okResult.Status);
        Assert.False(Directory.Exists(ok.TransactionDirectory), "成功時に workspace が残っている");

        // (b) rollback 成功時（Save 失敗 / canonical は free）: これも片付く
        var rolled = await StageAsync(transaction, project.Id, 0x03);
        VideoArtifactCommitResult rolledResult;
        using (LockProjectJson(store, project.Id))
        {
            rolledResult = await transaction.CommitAsync(rolled, project.Revision);
        }

        Assert.Equal(VideoArtifactCommitStatus.Failed, rolledResult.Status);
        Assert.False(rolledResult.RecoveryRequired);
        Assert.False(Directory.Exists(rolled.TransactionDirectory), "rollback 成功時に workspace が残っている");

        // (c) rollback 失敗時: workspace と backup を残す
        var kept = await StageAsync(transaction, project.Id, 0x04);
        using (new FileStream(canonical, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var keptResult = await transaction.CommitAsync(kept, project.Revision);

            Assert.True(keptResult.RecoveryRequired);
            Assert.True(Directory.Exists(kept.TransactionDirectory), "recovery 用 workspace が消えている");
            Assert.True(File.Exists(kept.BackupPath), "recovery backup が消えている");
        }
    }
}
