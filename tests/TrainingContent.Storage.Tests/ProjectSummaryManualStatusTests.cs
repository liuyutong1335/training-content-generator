using System.IO;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Storage.Tests;

/// <summary>
/// G: <see cref="ProjectSummary.ManualStatus"/>（S1〜S8）。
///
/// <para>
/// Manual は Markdown + HTML の pair。metadata が片方でも欠ける / 実ファイルが片方でも無い場合は Missing、
/// pair はあるが SourceRevision が Revision または互いに食い違う場合は Stale。
/// <see cref="ProjectSummary.HasManual"/> は <c>ManualStatus != Missing</c> と一致する。
/// </para>
/// </summary>
public class ProjectSummaryManualStatusTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

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

    /// <summary>metadata を指定どおりに設定し、指定した file だけを実在させる。</summary>
    private static async Task<ProjectSummary> SetupAsync(
        TempProjectsRoot temp,
        int? markdownSourceRevision,
        int? htmlSourceRevision,
        bool writeMarkdownFile,
        bool writeHtmlFile)
    {
        var store = new ProjectStore(temp.Root);
        var project = await store.CreateProjectAsync("Manual status");
        project.Steps.Add(new TrainingStep { Id = Guid.NewGuid(), Order = 1, Action = StepActions.Click, Title = "手順 1" });

        if (markdownSourceRevision is { } mdRevision)
        {
            project.Outputs.ManualMarkdown = new GeneratedArtifact
            {
                Path = ManualArtifactTransaction.MarkdownRelativePath,
                GeneratedAtUtc = Now,
                SourceRevision = mdRevision,
            };
        }

        if (htmlSourceRevision is { } htmlRevision)
        {
            project.Outputs.ManualHtml = new GeneratedArtifact
            {
                Path = ManualArtifactTransaction.HtmlRelativePath,
                GeneratedAtUtc = Now,
                SourceRevision = htmlRevision,
            };
        }

        await store.SaveProjectAsync(project);

        var directory = store.GetProjectDirectory(project.Id);
        if (writeMarkdownFile)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "manual", "manual.md"), "# manual");
        }

        if (writeHtmlFile)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "manual", "manual.html"), "<html></html>");
        }

        // Revision を進めたいケースは metadata の SourceRevision 側で表現する（生成物は Revision を進めない）。
        return Assert.Single(await store.ListProjectsAsync());
    }

    [Fact]
    public async Task S1_metadata_が無ければ_Missing()
    {
        using var temp = new TempProjectsRoot();
        var summary = await SetupAsync(temp, null, null, writeMarkdownFile: false, writeHtmlFile: false);

        Assert.Equal(ArtifactGenerationState.Missing, summary.ManualStatus);
        Assert.False(summary.HasManual);
    }

    [Fact]
    public async Task S2_pair_metadata_と_pair_file_があり_Revision_が一致すれば_Current()
    {
        using var temp = new TempProjectsRoot();
        var store = new ProjectStore(temp.Root);

        // 実 store の Revision（= 1）と一致させる。
        var summary = await SetupAsync(temp, 1, 1, writeMarkdownFile: true, writeHtmlFile: true);
        _ = store;

        Assert.Equal(ArtifactGenerationState.Current, summary.ManualStatus);
        Assert.True(summary.HasManual);
    }

    [Fact]
    public async Task S3_pair_はあるが_SourceRevision_が古ければ_Stale()
    {
        using var temp = new TempProjectsRoot();
        var summary = await SetupAsync(temp, 0, 0, writeMarkdownFile: true, writeHtmlFile: true);

        Assert.Equal(ArtifactGenerationState.Stale, summary.ManualStatus);
        Assert.True(summary.HasManual);
    }

    [Fact]
    public async Task S4_markdown_だけでは_Missing()
    {
        using var temp = new TempProjectsRoot();
        var summary = await SetupAsync(temp, 1, null, writeMarkdownFile: true, writeHtmlFile: false);

        Assert.Equal(ArtifactGenerationState.Missing, summary.ManualStatus);
        Assert.False(summary.HasManual);
    }

    [Fact]
    public async Task S5_html_だけでは_Missing()
    {
        using var temp = new TempProjectsRoot();
        var summary = await SetupAsync(temp, null, 1, writeMarkdownFile: false, writeHtmlFile: true);

        Assert.Equal(ArtifactGenerationState.Missing, summary.ManualStatus);
        Assert.False(summary.HasManual);
    }

    [Fact]
    public async Task S6_metadata_pair_があっても実ファイルが片方無ければ_Missing()
    {
        using var temp = new TempProjectsRoot();
        var summary = await SetupAsync(temp, 1, 1, writeMarkdownFile: true, writeHtmlFile: false);

        Assert.Equal(ArtifactGenerationState.Missing, summary.ManualStatus);
        Assert.False(summary.HasManual);
    }

    [Fact]
    public async Task S7_markdown_と_html_の_SourceRevision_が食い違えば_Stale()
    {
        using var temp = new TempProjectsRoot();
        var summary = await SetupAsync(temp, 1, 2, writeMarkdownFile: true, writeHtmlFile: true);

        Assert.Equal(ArtifactGenerationState.Stale, summary.ManualStatus);
        Assert.True(summary.HasManual);
    }

    [Fact]
    public async Task S8_HasManual_は_Current_と_Stale_で_true_Missing_で_false()
    {
        using var temp1 = new TempProjectsRoot();
        using var temp2 = new TempProjectsRoot();
        using var temp3 = new TempProjectsRoot();

        var current = await SetupAsync(temp1, 1, 1, writeMarkdownFile: true, writeHtmlFile: true);
        var stale = await SetupAsync(temp2, 0, 0, writeMarkdownFile: true, writeHtmlFile: true);
        var missing = await SetupAsync(temp3, null, null, writeMarkdownFile: false, writeHtmlFile: false);

        Assert.True(current.HasManual);
        Assert.True(stale.HasManual);
        Assert.False(missing.HasManual);
    }
}
