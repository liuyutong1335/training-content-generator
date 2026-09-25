using System.IO;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// G: <see cref="ManualArtifactTransaction"/> の pair atomicity（M1〜M13）。
///
/// <para>
/// Manual は Markdown + HTML の pair。片方だけ canonical / current になる状態を正常成功として作らないこと、
/// 失敗時は commit 前の exact filesystem state（非対称な既存状態も含む）へ戻すことを file 単位で確認する。
/// </para>
/// </summary>
public class ManualArtifactTransactionTests
{
    private const string MarkdownBytes = "# manual markdown";
    private const string HtmlBytes = "<html>manual</html>";
    private const string OldMarkdown = "OLD-MD";
    private const string OldHtml = "OLD-HTML";

    // =====================================================================
    // 共通インフラ
    // =====================================================================

    private sealed class TempProjectsRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tc-storage-tests", Guid.NewGuid().ToString("N"));

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

    private static async Task<TrainingProject> CreateProjectAsync(ProjectStore store)
    {
        var project = await store.CreateProjectAsync("Manual テスト");
        project.Steps.Add(new TrainingStep
        {
            Id = Guid.NewGuid(),
            Order = 1,
            StartMs = 0,
            Action = StepActions.Click,
            Title = "手順 1",
        });
        await store.SaveProjectAsync(project);

        return project;
    }

    private static ManualArtifactStaging BeginStaging(ManualArtifactTransaction transaction, Guid projectId)
    {
        var staging = transaction.BeginStaging(projectId);
        File.WriteAllText(staging.StagingMarkdownPath, MarkdownBytes);
        File.WriteAllText(staging.StagingHtmlPath, HtmlBytes);
        return staging;
    }

    /// <summary>
    /// project.json を read 共有で掴む。読み込みは通るが置換（Save）は失敗する
    /// （Video test と同じ手法。FileShare.None だと LoadProjectAsync まで失敗してしまう）。
    /// </summary>
    private static FileStream LockProjectJson(ProjectStore store, Guid projectId) =>
        new(Path.Combine(store.GetProjectDirectory(projectId), ProjectStore.ProjectFileName),
            FileMode.Open, FileAccess.Read, FileShare.Read);

    private static void WriteCanonical(ManualArtifactStaging staging, string? markdown = OldMarkdown, string? html = OldHtml)
    {
        if (markdown is not null)
        {
            File.WriteAllText(staging.CanonicalMarkdownPath, markdown);
        }

        if (html is not null)
        {
            File.WriteAllText(staging.CanonicalHtmlPath, html);
        }
    }

    private static void AssertNoTransactionLeftovers(ManualArtifactTransaction transaction, Guid projectId)
    {
        var projectTransactions = Path.Combine(transaction.TransactionsRoot, projectId.ToString("D"));
        if (!Directory.Exists(projectTransactions))
        {
            return;
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(projectTransactions));
    }

    // =====================================================================
    // M1 / M2 — 初回生成
    // =====================================================================

    [Fact]
    public async Task M1_既存_manual_が無い場合は_pair_が新規作成される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        Assert.False(File.Exists(staging.CanonicalMarkdownPath));
        Assert.False(File.Exists(staging.CanonicalHtmlPath));

        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Committed, commit.Status);
        Assert.Equal(MarkdownBytes, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(HtmlBytes, File.ReadAllText(staging.CanonicalHtmlPath));
        AssertNoTransactionLeftovers(transaction, project.Id);
    }

    [Fact]
    public async Task M2_metadata_は_pair_で同一_GeneratedAtUtc_同一_SourceRevision_になる()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);
        var revisionBefore = project.Revision;

        var staging = BeginStaging(transaction, project.Id);
        var commit = await transaction.CommitAsync(staging, project.Revision);

        var saved = await store.LoadProjectAsync(project.Id);
        var markdown = saved!.Outputs.ManualMarkdown;
        var html = saved.Outputs.ManualHtml;

        Assert.NotNull(markdown);
        Assert.NotNull(html);
        Assert.Equal(ManualArtifactTransaction.MarkdownRelativePath, markdown!.Path);
        Assert.Equal(ManualArtifactTransaction.HtmlRelativePath, html!.Path);
        Assert.Equal(markdown.GeneratedAtUtc, html.GeneratedAtUtc);

        // commit が返した Project も同一 Revision / 同一 value を持つ。
        Assert.Equal(markdown.GeneratedAtUtc, commit.Project!.Outputs.ManualMarkdown!.GeneratedAtUtc);
        Assert.Equal(markdown.SourceRevision, html.SourceRevision);
        Assert.Equal(revisionBefore, markdown.SourceRevision);

        // Manual 生成では Revision を進めない（UpdatedAtUtc は保存時刻で更新される）。
        Assert.Equal(revisionBefore, saved.Revision);
    }

    // =====================================================================
    // M3 / M4 — 再生成と revision gate
    // =====================================================================

    [Fact]
    public async Task M3_再生成では_両方の_old_file_が置換される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var first = BeginStaging(transaction, project.Id);
        Assert.Equal(ManualArtifactCommitStatus.Committed, (await transaction.CommitAsync(first, project.Revision)).Status);

        // Review 編集で Revision が進んだ想定。
        project.Revision += 1;
        await store.SaveProjectAsync(project);

        var second = transaction.BeginStaging(project.Id);
        File.WriteAllText(second.StagingMarkdownPath, "# regenerated markdown");
        File.WriteAllText(second.StagingHtmlPath, "<html>regenerated</html>");

        var commit = await transaction.CommitAsync(second, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Committed, commit.Status);
        Assert.Equal("# regenerated markdown", File.ReadAllText(second.CanonicalMarkdownPath));
        Assert.Equal("<html>regenerated</html>", File.ReadAllText(second.CanonicalHtmlPath));

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Equal(project.Revision, saved!.Outputs.ManualMarkdown!.SourceRevision);
        Assert.Equal(project.Revision, saved.Outputs.ManualHtml!.SourceRevision);
        AssertNoTransactionLeftovers(transaction, project.Id);
    }

    [Fact]
    public async Task M4_Revision_不一致では_canonical_も_metadata_も変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging);

        // 生成中に teaching content が編集された状況。
        project.Revision += 1;
        await store.SaveProjectAsync(project);

        var commit = await transaction.CommitAsync(staging, sourceRevision: project.Revision - 1);

        Assert.Equal(ManualArtifactCommitStatus.SourceChanged, commit.Status);
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(OldHtml, File.ReadAllText(staging.CanonicalHtmlPath));

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Null(saved!.Outputs.ManualMarkdown);
        Assert.Null(saved.Outputs.ManualHtml);

        // SourceChanged では canonical を触っていないが、staging は呼出側が破棄する。
        transaction.DiscardStaging(staging);
        AssertNoTransactionLeftovers(transaction, project.Id);
    }

    // =====================================================================
    // M5 / M6 — staging preflight
    // =====================================================================

    [Fact]
    public async Task M5_staged_markdown_が無いと_canonical_を変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging);
        File.Delete(staging.StagingMarkdownPath);

        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.StagingMissing, commit.Status);
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(OldHtml, File.ReadAllText(staging.CanonicalHtmlPath));
    }

    [Fact]
    public async Task M6_staged_html_が無いと_canonical_を変更しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging);
        File.WriteAllText(staging.StagingHtmlPath, string.Empty); // 空 file も invalid として扱う

        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.StagingMissing, commit.Status);
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(OldHtml, File.ReadAllText(staging.CanonicalHtmlPath));
    }

    // =====================================================================
    // M7 / M8 — project save failure の rollback
    // =====================================================================

    [Fact]
    public async Task M7_保存失敗時は_old_pair_が復元され_old_metadata_も残る()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        // 先に成功させて metadata pair を作る。
        var first = BeginStaging(transaction, project.Id);
        await transaction.CommitAsync(first, project.Revision);
        var before = await store.LoadProjectAsync(project.Id);

        // 旧 pair を判別できる内容に置き換える。
        File.WriteAllText(first.CanonicalMarkdownPath, OldMarkdown);
        File.WriteAllText(first.CanonicalHtmlPath, OldHtml);

        var staging = BeginStaging(transaction, project.Id);
        using var lockStream = LockProjectJson(store, project.Id); // SaveProjectAsync を失敗させる

        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Failed, commit.Status);
        Assert.False(commit.RecoveryRequired);
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(OldHtml, File.ReadAllText(staging.CanonicalHtmlPath));

        var after = await store.LoadProjectAsync(project.Id);
        Assert.Equal(before!.Outputs.ManualMarkdown!.GeneratedAtUtc, after!.Outputs.ManualMarkdown!.GeneratedAtUtc);
        Assert.Equal(before.Outputs.ManualHtml!.GeneratedAtUtc, after.Outputs.ManualHtml!.GeneratedAtUtc);

        // rollback 成功時は workspace ごと片付く。
        AssertNoTransactionLeftovers(transaction, project.Id);
    }

    [Fact]
    public async Task M8_保存失敗時で_old_pair_が無い場合は_両方_absent_へ戻る()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        using var lockStream = LockProjectJson(store, project.Id);

        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Failed, commit.Status);
        Assert.False(File.Exists(staging.CanonicalMarkdownPath));
        Assert.False(File.Exists(staging.CanonicalHtmlPath));

        var saved = await store.LoadProjectAsync(project.Id);
        Assert.Null(saved!.Outputs.ManualMarkdown);
        Assert.Null(saved.Outputs.ManualHtml);
        AssertNoTransactionLeftovers(transaction, project.Id);
    }

    // =====================================================================
    // M9 / M10 — pair rollback と非対称な既存状態
    // =====================================================================

    [Fact]
    public async Task M9_置換自体が失敗しても_pair_が_commit_前の状態へ戻る()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging);

        // staged markdown を掴んで File.Move を失敗させる（canonical は free なので restore は成功する）。
        ManualArtifactCommitResult commit;
        using (new FileStream(staging.StagingMarkdownPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            commit = await transaction.CommitAsync(staging, project.Revision);
        }

        Assert.Equal(ManualArtifactCommitStatus.Failed, commit.Status);
        Assert.False(commit.RecoveryRequired, "rollback は成功しているはず");

        // 片方だけ戻して終了しない（pair 全体が commit 前の状態）。
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(OldHtml, File.ReadAllText(staging.CanonicalHtmlPath));
        Assert.Null((await store.LoadProjectAsync(project.Id))!.Outputs.ManualMarkdown);
    }

    [Fact]
    public async Task M10_非対称な既存状態はそのまま復元される()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging, markdown: OldMarkdown, html: null); // markdown だけ存在
        Assert.False(File.Exists(staging.CanonicalHtmlPath));

        using var lockStream = LockProjectJson(store, project.Id);
        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Failed, commit.Status);

        // markdown は backup から復元、html は「元々無かった」ので削除される（勝手に修復しない）。
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.False(File.Exists(staging.CanonicalHtmlPath));
        AssertNoTransactionLeftovers(transaction, project.Id);
    }

    [Fact]
    public async Task M10b_非対称な既存状態は正常_commit_で新しい_pair_に揃う()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging, markdown: null, html: OldHtml); // html だけ存在

        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Committed, commit.Status);
        Assert.Equal(MarkdownBytes, File.ReadAllText(staging.CanonicalMarkdownPath));
        Assert.Equal(HtmlBytes, File.ReadAllText(staging.CanonicalHtmlPath));
    }

    [Fact]
    public async Task M11_rollback_失敗時は_RecoveryRequired_になり_backup_を保持する()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        WriteCanonical(staging);

        // canonical markdown を掴むと置換も restore も失敗し、backup が旧 file の唯一の複製になる。
        using (new FileStream(staging.CanonicalMarkdownPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var commit = await transaction.CommitAsync(staging, project.Revision);

            Assert.Equal(ManualArtifactCommitStatus.Failed, commit.Status);
            Assert.NotNull(commit.Error);
            Assert.True(commit.RecoveryRequired);
            Assert.Equal(staging.TransactionDirectory, commit.RecoveryDirectory);
            Assert.True(File.Exists(staging.BackupMarkdownPath), "recovery backup が残っていない");
            Assert.True(Directory.Exists(staging.TransactionDirectory));

            // Coordinator が呼ぶ DiscardStaging でも削除されない（defense-in-depth）。
            transaction.DiscardStaging(staging);
            Assert.True(File.Exists(staging.BackupMarkdownPath), "DiscardStaging が recovery backup を削除した");
            Assert.True(Directory.Exists(staging.TransactionDirectory));
        }

        // lock 解放後は backup から旧 markdown を復旧できる。
        Assert.Equal(OldMarkdown, File.ReadAllText(staging.BackupMarkdownPath));
    }

    [Fact]
    public async Task M12_DiscardStaging_は_recovery_backup_を削除しない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = transaction.BeginStaging(project.Id);

        // recovery backup が残っている transaction を模す。
        File.WriteAllText(staging.BackupMarkdownPath, OldMarkdown);

        transaction.DiscardStaging(staging);

        Assert.True(Directory.Exists(staging.TransactionDirectory));
        Assert.True(File.Exists(staging.BackupMarkdownPath));

        // backup を消せば削除できる（defense-in-depth は backup の有無だけを見る）。
        File.Delete(staging.BackupMarkdownPath);
        transaction.DiscardStaging(staging);
        Assert.False(Directory.Exists(staging.TransactionDirectory));
    }

    [Fact]
    public async Task M13_成功時は_transaction_child_が残らない()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);
        var project = await CreateProjectAsync(store);
        var transaction = new ManualArtifactTransaction(store);

        var staging = BeginStaging(transaction, project.Id);
        var commit = await transaction.CommitAsync(staging, project.Revision);

        Assert.Equal(ManualArtifactCommitStatus.Committed, commit.Status);
        Assert.False(Directory.Exists(staging.TransactionDirectory));
        AssertNoTransactionLeftovers(transaction, project.Id);

        // project-level parent directory は housekeeping として残ってよい。
        Assert.True(Directory.Exists(transaction.TransactionsRoot));
    }
}
