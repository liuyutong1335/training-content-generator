using System.Drawing;
using TrainingContent.Screenshot.Redaction;
using Xunit;

namespace TrainingContent.Screenshot.Tests;

/// <summary>
/// BlackBoxScreenshotRedactor（C3-2）。MVP は BlackBox（不透明な純黒）のみ。
/// Pixelate / Blur / marker 等は Post-MVP のため扱わない。
/// </summary>
public class BlackBoxScreenshotRedactorTests
{
    private const int Width = 20;
    private const int Height = 10;

    private static readonly Color Background = Color.FromArgb(255, 0, 128, 255);
    private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);

    private static BlackBoxScreenshotRedactor Redactor() => new();

    // --- 正常系 ---

    [Fact]
    public async Task SingleRegion_IsBlackenedAndOthersAreUnchanged()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(5, 2, 4, 3)));

        Assert.False(result.HasErrors);
        Assert.True(result.Succeeded);
        Assert.Equal(Width, result.Width);
        Assert.Equal(Height, result.Height);
        Assert.Equal(1, result.AppliedRegionCount);

        // region 内は不透明な純黒
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 5, 2));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 8, 4));
        // region 外は元の色を維持
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 4, 2));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 9, 2));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 5, 1));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 5, 5));
    }

    [Fact]
    public async Task OutputSize_MatchesInput()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 13, 7, Background);

        var result = await Redactor().RedactAsync(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(0, 0, 1, 1)));

        Assert.False(result.HasErrors);
        Assert.Equal((13, 7), ScreenshotTestData.ReadSize(workspace.PathIn("output.png")));
    }

    [Fact]
    public async Task Output_IsReDecodableAsPng()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(1, 1, 2, 2)));

        Assert.False(result.HasErrors);
        var bytes = ScreenshotTestData.ReadBytes(output);
        Assert.True(bytes.Length > 8);
        Assert.Equal(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A],
            bytes[..8]);
    }

    [Fact]
    public async Task MultipleRegions_AreAllBlackened()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(Request(
            source,
            output,
            new RedactionRectangle(0, 0, 2, 2),
            new RedactionRectangle(10, 5, 3, 3),
            new RedactionRectangle(18, 8, 2, 2)));

        Assert.Equal(3, result.AppliedRegionCount);
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 0, 0));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 12, 7));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 19, 9));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 5, 5));
    }

    [Fact]
    public async Task OverlappingRegions_AreBlackenedOnce()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(Request(
            source,
            output,
            new RedactionRectangle(2, 2, 6, 4),
            new RedactionRectangle(5, 3, 6, 4)));

        Assert.Equal(2, result.AppliedRegionCount);
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 3, 3));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 6, 4));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 10, 6));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 1, 1));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 11, 7));
    }

    [Fact]
    public async Task ClampedRegion_IsBlackenedOnlyInsideImage()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        // (-3, -3, 8, 8) → 画像内は (0, 0, 5, 5)
        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(-3, -3, 8, 8)));

        Assert.Equal(1, result.AppliedRegionCount);
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 0, 0));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 4, 4));
        Assert.Equal(Background, ScreenshotTestData.ReadPixel(output, 5, 5));
    }

    [Fact]
    public async Task SinglePixelImage_CanBeBlackened()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", 1, 1, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 1, 1)));

        Assert.False(result.HasErrors);
        Assert.Equal((1, 1), ScreenshotTestData.ReadSize(output));
        Assert.Equal(Black, ScreenshotTestData.ReadPixel(output, 0, 0));
    }

    // --- 異常系: 入力 ---

    [Fact]
    public async Task NullRequest_IsError()
    {
        var result = await Redactor().RedactAsync(null!);

        Assert.True(result.HasErrors);
        Assert.Null(result.Width);
        Assert.Null(result.Height);
    }

    [Fact]
    public async Task MissingSource_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(workspace.PathIn("no-such.png"), output, new RedactionRectangle(0, 0, 1, 1)));

        Assert.True(result.HasErrors);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task RegionOutsideImage_IsError_AndNoOutput()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(500, 500, 5, 5)));

        Assert.True(result.HasErrors);
        Assert.False(File.Exists(output));
        Assert.Equal(["source.png"], workspace.ListEntries());
    }

    [Fact]
    public async Task EmptyRegions_IsError_AndNoOutput()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(Request(source, output));

        Assert.True(result.HasErrors);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task SameSourceAndOutput_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var before = ScreenshotTestData.ReadBytes(source);

        var result = await Redactor().RedactAsync(
            Request(source, source, new RedactionRectangle(0, 0, 2, 2)));

        Assert.True(result.HasErrors);
        Assert.Equal(before, ScreenshotTestData.ReadBytes(source));
    }

    [Fact]
    public async Task MissingOutputParentDirectory_IsError_AndNoDirectoryCreated()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);

        var result = await Redactor().RedactAsync(
            Request(source, workspace.NonExistentPath("output.png"), new RedactionRectangle(0, 0, 2, 2)));

        Assert.True(result.HasErrors);
        Assert.False(Directory.Exists(workspace.PathIn("missing")));
    }

    // --- 異常系: PNG decode / format ---

    [Fact]
    public async Task NonImageFileWithPngExtension_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateNonImagePng();
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 1, 1)));

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("SourceImagePath", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task CorruptPng_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateCorruptPng();
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 1, 1)));

        Assert.True(result.HasErrors);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task JpegRenamedToPng_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateJpegNamedPng("source.png");
        var output = workspace.PathIn("output.png");

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 1, 1)));

        Assert.True(result.HasErrors);
        Assert.False(File.Exists(output));
    }

    // --- cancellation ---

    [Fact]
    public async Task PreCancelledToken_IsError_WithoutSideEffects()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");
        var before = ScreenshotTestData.ReadBytes(source);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await Redactor().RedactAsync(
            Request(source, output, new RedactionRectangle(0, 0, 2, 2)),
            cts.Token);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("キャンセル", StringComparison.Ordinal));
        Assert.False(File.Exists(output));
        Assert.Equal(["source.png"], workspace.ListEntries());
        Assert.Equal(before, ScreenshotTestData.ReadBytes(source));
    }

    // --- Error 非漏洩 ---

    [Theory]
    [InlineData("missing")]
    [InlineData("not-image")]
    [InlineData("corrupt")]
    [InlineData("same")]
    public async Task ErrorMessages_DoNotLeakPathOrExceptionDetails(string scenario)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");

        var request = scenario switch
        {
            "missing" => Request(workspace.PathIn("missing-source.png"), output, new RedactionRectangle(0, 0, 1, 1)),
            "not-image" => Request(workspace.CreateNonImagePng(), output, new RedactionRectangle(0, 0, 1, 1)),
            "corrupt" => Request(workspace.CreateCorruptPng(), output, new RedactionRectangle(0, 0, 1, 1)),
            _ => Request(source, source, new RedactionRectangle(0, 0, 1, 1)),
        };

        var result = await Redactor().RedactAsync(request);
        var errors = string.Join("\n", result.Errors);

        Assert.True(result.HasErrors);
        Assert.DoesNotContain(workspace.Root, errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source.png", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("output.png", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("not-an-image", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corrupt", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Temp", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppData", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GDI+", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Drawing", errors, StringComparison.OrdinalIgnoreCase);
    }

    // --- 入力不変 ---

    [Fact]
    public async Task SourceBytesAndRequest_AreNotModified()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreatePng("source.png", Width, Height, Background);
        var output = workspace.PathIn("output.png");
        var before = ScreenshotTestData.ReadBytes(source);
        var regions = new List<RedactionRectangle> { new(-2, -2, 6, 6), new(10, 5, 3, 3) };
        var request = new ScreenshotRedactionRequest
        {
            SourceImagePath = source,
            OutputPath = output,
            Regions = regions,
        };
        var snapshot = regions.ToList();

        var result = await Redactor().RedactAsync(request);

        Assert.False(result.HasErrors);
        Assert.Equal(before, ScreenshotTestData.ReadBytes(source));
        Assert.Equal(snapshot, regions);
        Assert.Equal(source, request.SourceImagePath);
        Assert.Equal(output, request.OutputPath);
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
