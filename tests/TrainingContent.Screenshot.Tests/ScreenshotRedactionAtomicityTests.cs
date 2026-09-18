using System.Drawing;
using TrainingContent.Screenshot.Redaction;
using Xunit;

namespace TrainingContent.Screenshot.Tests;

/// <summary>
/// BlackBox Redaction の atomic 性と既存成果物保護（C3-2）。
/// 「既存 Output を上書きしない」「失敗時に temp / Output を残さない」「既存 bytes を変えない」を確認する。
/// </summary>
public class ScreenshotRedactionAtomicityTests
{
    private static readonly Color Background = Color.FromArgb(255, 0, 128, 255);
    private static readonly Color ExistingOutputColor = Color.FromArgb(255, 255, 0, 0);

    private static BlackBoxScreenshotRedactor Redactor() => new();

    [Fact]
    public async Task ExistingOutputFile_IsErrorAndNotOverwritten()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 8, 8, Background);
        var output = workspace.CreatePng("output.png", 4, 4, ExistingOutputColor);
        var before = ScreenshotTestData.ReadBytes(output);

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 2, 2)));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("未使用のファイル", StringComparison.Ordinal));
        // 既存 Output は bytes もサイズも不変
        Assert.Equal(before, ScreenshotTestData.ReadBytes(output));
        Assert.Equal((4, 4), ScreenshotTestData.ReadSize(output));
        Assert.Equal(ExistingOutputColor, ScreenshotTestData.ReadPixel(output, 0, 0));
    }

    [Fact]
    public async Task ExistingOutputDirectory_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 8, 8, Background);
        var output = workspace.PathIn("output.png");
        Directory.CreateDirectory(output);

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 2, 2)));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("未使用のファイル", StringComparison.Ordinal));
        Assert.True(Directory.Exists(output), "既存 directory を削除しないこと");
    }

    [Fact]
    public async Task ExistingOutputError_DoesNotLeakPathInformation()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 8, 8, Background);
        var output = workspace.CreatePng("existing-output.png", 4, 4, ExistingOutputColor);

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 2, 2)));
        var errors = string.Join("\n", result.Errors);

        Assert.True(result.HasErrors);
        Assert.DoesNotContain(workspace.Root, errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("existing-output.png", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", errors, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.UserName, errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidationError_LeavesNoOutputAndNoTemp()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 8, 8, Background);
        var output = workspace.PathIn("output.png");
        var sourceBefore = ScreenshotTestData.ReadBytes(source);

        // 完全に画像外の region → validation Error
        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(100, 100, 4, 4)));

        Assert.True(result.HasErrors);
        Assert.False(File.Exists(output));
        Assert.Equal(["source.png"], workspace.ListEntries());
        Assert.Equal(sourceBefore, ScreenshotTestData.ReadBytes(source));
    }

    [Fact]
    public async Task Success_LeavesOnlySourceAndOutput()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 8, 8, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(1, 1, 3, 3)));

        Assert.False(result.HasErrors);
        Assert.True(File.Exists(output));
        // temp file が残らないこと（Output と同じ directory に作られるため、一覧で確認できる）
        Assert.Equal(["output.png", "source.png"], workspace.ListEntries());
    }

    [Fact]
    public async Task RepeatedRunOnNewOutput_DoesNotTouchPreviousResults()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 8, 8, Background);
        var first = workspace.PathIn("first.png");
        var second = workspace.PathIn("second.png");

        var firstResult = await Redactor().RedactAsync(Request(source, first, new RedactionRectangle(0, 0, 2, 2)));
        var firstBytes = ScreenshotTestData.ReadBytes(first);
        var secondResult = await Redactor().RedactAsync(Request(source, second, new RedactionRectangle(4, 4, 2, 2)));

        Assert.False(firstResult.HasErrors);
        Assert.False(secondResult.HasErrors);
        Assert.Equal(firstBytes, ScreenshotTestData.ReadBytes(first));
        Assert.Equal(["first.png", "second.png", "source.png"], workspace.ListEntries());
    }

    [Fact]
    public async Task FailureAfterSourceRead_KeepsSourceAndExistingOutputUntouched()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var corruptSource = workspace.CreateCorruptPng("source.png");
        var output = workspace.CreatePng("output.png", 4, 4, ExistingOutputColor);
        var sourceBefore = ScreenshotTestData.ReadBytes(corruptSource);
        var outputBefore = ScreenshotTestData.ReadBytes(output);

        var result = await Redactor().RedactAsync(
            Request(corruptSource, output, new RedactionRectangle(0, 0, 1, 1)));

        Assert.True(result.HasErrors);
        Assert.Equal(sourceBefore, ScreenshotTestData.ReadBytes(corruptSource));
        Assert.Equal(outputBefore, ScreenshotTestData.ReadBytes(output));
        Assert.Equal(["output.png", "source.png"], workspace.ListEntries());
    }

    private static ScreenshotRedactionRequest Request(
        string sourceImagePath,
        string outputPath,
        params RedactionRectangle[] regions) => new()
        {
            SourceImagePath = sourceImagePath,
            OutputPath = outputPath,
            Regions = regions,
        };
}
