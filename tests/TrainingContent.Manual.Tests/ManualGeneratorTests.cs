using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// ManualGenerator（docs/development-plan.md §19）。
/// TrainingProject → ManualDocumentBuilder → Markdown / HTML Writer → 結果オブジェクト。
/// ファイル I/O・ProjectOutputs 更新は担当D の責務のため本クラスでは扱わない。
/// </summary>
public class ManualGeneratorTests
{
    // --- 正常系 ---

    [Fact]
    public void Generate_ProducesMarkdownAndHtml()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "「新規申請」をクリックします", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "「社員番号」に入力します"));

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
        Assert.NotNull(result.Markdown);
        Assert.NotNull(result.Html);
        Assert.StartsWith("# 経費申請登録", result.Markdown.Content, StringComparison.Ordinal);
        Assert.StartsWith("<!doctype html>", result.Html.Content, StringComparison.Ordinal);
        Assert.Contains("### 2. 「社員番号」に入力します", result.Markdown.Content, StringComparison.Ordinal);
        Assert.Contains("<h3>2. 「社員番号」に入力します</h3>", result.Html.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_UsesFixedOutputPaths()
    {
        Assert.Equal("manual/manual.md", ManualGenerator.MarkdownPath);
        Assert.Equal("manual/manual.html", ManualGenerator.HtmlPath);

        var result = ManualGenerator.Generate(ManualTestData.Project(ManualTestData.Step(1, "手順1")));

        Assert.Equal("manual/manual.md", result.Markdown!.Path);
        Assert.Equal("manual/manual.html", result.Html!.Path);
    }

    [Fact]
    public void Generate_MultipleSteps_AreWrittenInInputOrder()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1"),
            ManualTestData.Step(2, "手順2"),
            ManualTestData.Step(3, "手順3"));

        var result = ManualGenerator.Generate(project);

        Assert.Equal(
            ["### 1. 手順1", "### 2. 手順2", "### 3. 手順3"],
            result.Markdown!.Content.Split('\n').Where(line => line.StartsWith("### ", StringComparison.Ordinal)));
        Assert.Equal(
            ["<h3>1. 手順1</h3>", "<h3>2. 手順2</h3>", "<h3>3. 手順3</h3>"],
            result.Html!.Content.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("<h3>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Generate_OptionalSections_AreOmittedInBothOutputs()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Objective = null;
        project.TargetAudience = "   ";
        project.Prerequisites = [];

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain("学習目標", result.Markdown!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("学習目標", result.Html!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("対象者", result.Markdown.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("対象者", result.Html.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("事前準備", result.Markdown.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("事前準備", result.Html.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_WithOptionalFields_KeepsThemInBothOutputs()
    {
        var result = ManualGenerator.Generate(ManualTestData.Project(ManualTestData.Step(1, "手順1")));

        Assert.Contains("## 学習目標", result.Markdown!.Content, StringComparison.Ordinal);
        Assert.Contains("<h2>学習目標</h2>", result.Html!.Content, StringComparison.Ordinal);
        Assert.Contains("- PC の基本操作", result.Markdown.Content, StringComparison.Ordinal);
        Assert.Contains("<li>PC の基本操作</li>", result.Html.Content, StringComparison.Ordinal);
    }

    // --- Builder の Error（部分出力なし） ---

    [Fact]
    public void BuilderError_ReturnsNoFiles()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Title = "";

        var result = ManualGenerator.Generate(project);

        Assert.True(result.HasErrors);
        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
        Assert.Contains(result.Errors, error => error.Contains("Title", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void UnsupportedSchemaVersion_ReturnsNoFiles(int schemaVersion)
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.SchemaVersion = schemaVersion;

        var result = ManualGenerator.Generate(project);

        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
        Assert.Contains(result.Errors, error => error.Contains($"schemaVersion {schemaVersion}", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroSteps_ReturnsNoFiles()
    {
        var result = ManualGenerator.Generate(ManualTestData.Project());

        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
        Assert.Contains(result.Errors, error => error.Contains("Steps", StringComparison.Ordinal));
    }

    [Fact]
    public void NullProject_IsErrorInsteadOfThrowing()
    {
        var result = ManualGenerator.Generate(null);

        Assert.True(result.HasErrors);
        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
        Assert.NotEmpty(result.Errors);
    }

    // --- Writer の Error（atomic） ---

    [Fact]
    public void WriterPathError_ReturnsNoFilesAtomically()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "手順2", "../secret.png"));

        var result = ManualGenerator.Generate(project);

        Assert.True(result.HasErrors);
        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
    }

    [Fact]
    public void WriterPathError_IsDeduplicatedAndOrdered()
    {
        // Markdown / HTML の両 Writer が同じ Error を返すため、重複排除されること（4 件ではなく 2 件）。
        // ProjectValidator を通過し、Writer 側で検出される traversal を使う。
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "手順2", "../secret.png"),
            ManualTestData.Step(3, "手順3", "screenshots/../other.png"));

        var result = ManualGenerator.Generate(project);

        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("Step 2", result.Errors[0], StringComparison.Ordinal);
        Assert.Contains("Step 3", result.Errors[1], StringComparison.Ordinal);
        Assert.Equal(result.Errors.Distinct(StringComparer.Ordinal), result.Errors);
    }

    [Fact]
    public void WriterPathError_DoesNotLeakThePath()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "../secret.png"));

        var result = ManualGenerator.Generate(project);
        var errors = string.Join("\n", result.Errors);

        Assert.DoesNotContain("secret.png", errors, StringComparison.Ordinal);
        Assert.Contains("screenshotPath", errors, StringComparison.Ordinal);
    }

    // --- 入力を変更しない / 副作用なし ---

    [Fact]
    public void Generate_DoesNotModifyInputProjectFields()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        var revision = project.Revision;
        var title = project.Title;
        var steps = project.Steps.ToList();

        _ = ManualGenerator.Generate(project);

        Assert.Equal(revision, project.Revision);
        Assert.Equal(title, project.Title);
        Assert.Equal(steps, project.Steps);
    }

    [Fact]
    public void Generate_DoesNotModifyProjectOutputs()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        var before = project.Outputs;

        _ = ManualGenerator.Generate(project);

        Assert.Same(before, project.Outputs);
        Assert.Null(project.Outputs.ManualMarkdown);
        Assert.Null(project.Outputs.ManualHtml);
        Assert.Null(project.Outputs.TrainingVideo);
    }

    [Fact]
    public void Generate_IsDeterministic_NoEmbeddedTime()
    {
        // 2 回生成しても完全一致（GeneratedAtUtc 等の時刻を埋め込んでいないこと）。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));

        var first = ManualGenerator.Generate(project);
        var second = ManualGenerator.Generate(project);

        Assert.Equal(first.Markdown!.Content, second.Markdown!.Content);
        Assert.Equal(first.Html!.Content, second.Html!.Content);
    }

    [Fact]
    public void Generate_DoesNotCreateFilesOrDirectories()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        var before = Directory.GetFileSystemEntries(Environment.CurrentDirectory).Order(StringComparer.Ordinal).ToList();

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.False(Directory.Exists(Path.Combine(Environment.CurrentDirectory, "manual")));
        Assert.Equal(
            before,
            Directory.GetFileSystemEntries(Environment.CurrentDirectory).Order(StringComparer.Ordinal));
    }
}
