using TrainingContent.Screenshot.Redaction;
using Xunit;

namespace TrainingContent.Screenshot.Tests;

/// <summary>
/// ScreenshotRedactionValidator（C3-1）。
/// 画像加工（BlackBox / Pixelate 等）・PNG 書き込み・Project 更新は本クラスの範囲外。
/// </summary>
public class ScreenshotRedactionValidatorTests
{
    private const int ImageWidth = 100;
    private const int ImageHeight = 50;

    // --- 正常系 ---

    [Fact]
    public void RegionInsideImage_IsKeptAsIs()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(10, 5, 30, 20)),
            ImageWidth,
            ImageHeight);

        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
        Assert.Equal([new RedactionRectangle(10, 5, 30, 20)], result.NormalizedRegions);
    }

    [Theory]
    [InlineData(-10, -5, 30, 20, 0, 0, 20, 15)]     // 左上にはみ出す
    [InlineData(80, 40, 50, 40, 80, 40, 20, 10)]     // 右下にはみ出す
    [InlineData(-100, -100, 50, 50, 0, 0, 0, 0)]     // 完全に画像外（Error 側の分岐を確認）
    public void OutOfBoundsRegion_IsClampedToImage(
        int x,
        int y,
        int width,
        int height,
        int expectedX,
        int expectedY,
        int expectedWidth,
        int expectedHeight)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(x, y, width, height)),
            ImageWidth,
            ImageHeight);

        if (expectedWidth == 0)
        {
            // 交差しない入力は Error（下の OutOfImage 系テストで詳細を確認）。
            Assert.True(result.HasErrors);
            Assert.Empty(result.NormalizedRegions);
            return;
        }

        Assert.Empty(result.Errors);
        Assert.Equal([new RedactionRectangle(expectedX, expectedY, expectedWidth, expectedHeight)], result.NormalizedRegions);
    }

    [Fact]
    public void MultipleRegions_PreserveInputOrder()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(
                source,
                workspace.PathIn("output.png"),
                new RedactionRectangle(0, 0, 10, 10),
                new RedactionRectangle(-5, -5, 10, 10),
                new RedactionRectangle(90, 45, 10, 10)),
            ImageWidth,
            ImageHeight);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [
                new RedactionRectangle(0, 0, 10, 10),
                new RedactionRectangle(0, 0, 5, 5),
                new RedactionRectangle(90, 45, 10, 5),
            ],
            result.NormalizedRegions);
    }

    [Fact]
    public void DuplicateRegions_AreKeptWithoutMergeOrDedup()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(
                source,
                workspace.PathIn("output.png"),
                new RedactionRectangle(1, 2, 3, 4),
                new RedactionRectangle(1, 2, 3, 4)),
            ImageWidth,
            ImageHeight);

        Assert.Empty(result.Errors);
        Assert.Equal(
            [new RedactionRectangle(1, 2, 3, 4), new RedactionRectangle(1, 2, 3, 4)],
            result.NormalizedRegions);
    }

    [Fact]
    public void UppercasePngExtension_IsAccepted()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng("source.PNG");

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.PNG"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void OutputFileNotCreatedYet_IsAccepted()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();
        var output = workspace.PathIn("output.png");

        Assert.False(File.Exists(output));

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, output, new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.Empty(result.Errors);
    }

    // --- 異常系: request / image ---

    [Fact]
    public void NullRequest_IsError()
    {
        var result = ScreenshotRedactionValidator.Validate(null, ImageWidth, ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveImageWidth_IsError(int width)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            width,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("imageWidth", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveImageHeight_IsError(int height)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            height);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("imageHeight", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankSourceImagePath_IsError(string? source)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source!, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("SourceImagePath", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankOutputPath_IsError(string? output)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, output!, new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("OutputPath", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void NullRegions_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            new ScreenshotRedactionRequest
            {
                SourceImagePath = source,
                OutputPath = workspace.PathIn("output.png"),
                Regions = null!,
            },
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Regions", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void EmptyRegions_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png")),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Regions", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    // --- 異常系: region ---

    [Theory]
    [InlineData(0, 5)]
    [InlineData(-3, 5)]
    public void NonPositiveRegionWidth_IsError(int width, int height)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, width, height)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Region 1", StringComparison.Ordinal) && error.Contains("Width", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(5, 0)]
    [InlineData(5, -3)]
    public void NonPositiveRegionHeight_IsError(int width, int height)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, width, height)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Region 1", StringComparison.Ordinal) && error.Contains("Height", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(-200, 0, 10, 10)]      // 完全に左
    [InlineData(0, -200, 10, 10)]      // 完全に上
    [InlineData(200, 0, 10, 10)]       // 完全に右
    [InlineData(0, 200, 10, 10)]       // 完全に下
    [InlineData(100, 0, 10, 10)]       // 右端に接する（交差しない）
    [InlineData(0, 50, 10, 10)]        // 下端に接する（交差しない）
    public void RegionCompletelyOutsideImage_IsError(int x, int y, int width, int height)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(x, y, width, height)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Region 1", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData(int.MaxValue, 0, int.MaxValue, 1)]
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue)]
    [InlineData(0, 0, int.MaxValue, int.MaxValue)]
    public void ExtremeCoordinates_DoNotOverflow(int x, int y, int width, int height)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        // 例外を投げずに評価できること（結果は範囲内なら clamp、範囲外なら Error）。
        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(x, y, width, height)),
            ImageWidth,
            ImageHeight);

        Assert.NotNull(result);
        Assert.All(
            result.NormalizedRegions,
            region => Assert.True(region.Width > 0 && region.Height > 0, "clamp 後のサイズは正であること"));
    }

    // --- 異常系: path ---

    [Fact]
    public void RelativeSourceImagePath_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();

        var result = ScreenshotRedactionValidator.Validate(
            Request("screenshots/original/event-000001.png", workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("SourceImagePath", StringComparison.Ordinal) && error.Contains("絶対パス", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void RelativeOutputPath_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, "screenshots/edited/step-001.png", new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("OutputPath", StringComparison.Ordinal) && error.Contains("絶対パス", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData("source.jpg")]
    [InlineData("source.bmp")]
    [InlineData("source")]
    public void NonPngSourceImagePath_IsError(string fileName)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng(fileName);

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("SourceImagePath", StringComparison.Ordinal) && error.Contains(".png", StringComparison.Ordinal));
    }

    [Fact]
    public void NonPngOutputPath_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.PathIn("output.jpg"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("OutputPath", StringComparison.Ordinal) && error.Contains(".png", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingSourceFile_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();

        var result = ScreenshotRedactionValidator.Validate(
            Request(workspace.PathIn("no-such.png"), workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("SourceImagePath", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void MissingOutputParentDirectory_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, workspace.NonExistentPath("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("OutputPath", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void SameSourceAndOutputPath_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, source, new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(
            result.Errors,
            error => error.Contains("同じファイル", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void SamePathWithDifferentCase_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng("source.png");
        var sameDifferentCase = Path.Combine(workspace.Root, "SOURCE.PNG");

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, sameDifferentCase, new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("同じファイル", StringComparison.Ordinal));
    }

    [Fact]
    public void SamePathWithDotSegment_IsError()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng("source.png");
        var sameWithDotSegment = Path.Combine(workspace.Root, ".", "source.png");

        Assert.NotEqual(source, sameWithDotSegment, StringComparer.Ordinal);

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, sameWithDotSegment, new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("同じファイル", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidPathCharacters_DoNotThrow()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();
        var invalidPath = Path.Combine(workspace.Root, "bad\0name.png");

        var exception = Record.Exception(() => ScreenshotRedactionValidator.Validate(
            Request(invalidPath, workspace.PathIn("output.png"), new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight));

        Assert.Null(exception);
    }

    // --- atomic / Error 収集 / 非漏洩 ---

    [Fact]
    public void MultipleErrors_AreAllCollected()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();

        var result = ScreenshotRedactionValidator.Validate(
            new ScreenshotRedactionRequest
            {
                SourceImagePath = "relative-source.jpg",
                OutputPath = workspace.NonExistentPath("output.bmp"),
                Regions = [new RedactionRectangle(0, 0, 0, 0), new RedactionRectangle(-500, -500, 10, 10)],
            },
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.True(result.Errors.Count >= 4, $"複数の Error を全件収集すること（実際: {result.Errors.Count}）");
        Assert.Contains(result.Errors, error => error.Contains("SourceImagePath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("OutputPath", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Region 1", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Region 2", StringComparison.Ordinal));
        Assert.Empty(result.NormalizedRegions);
    }

    [Fact]
    public void AnyError_YieldsEmptyNormalizedRegions()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();

        // 1件目は正常、2件目が完全に画像外 → 全体が Error になり部分 plan を返さない。
        var result = ScreenshotRedactionValidator.Validate(
            Request(
                source,
                workspace.PathIn("output.png"),
                new RedactionRectangle(1, 1, 2, 2),
                new RedactionRectangle(500, 500, 10, 10)),
            ImageWidth,
            ImageHeight);

        Assert.True(result.HasErrors);
        Assert.Empty(result.NormalizedRegions);
    }

    [Theory]
    [InlineData("source-not-exist")]
    [InlineData("relative")]
    [InlineData("nonpng")]
    [InlineData("same")]
    public void ErrorMessages_DoNotLeakPathInformation(string scenario)
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();
        var output = workspace.PathIn("output.png");

        var request = scenario switch
        {
            "source-not-exist" => Request(workspace.PathIn("missing-source.png"), output, new RedactionRectangle(1, 1, 2, 2)),
            "relative" => Request("relative/source.png", "relative/output.png", new RedactionRectangle(1, 1, 2, 2)),
            "nonpng" => Request(source, workspace.PathIn("output.bmp"), new RedactionRectangle(1, 1, 2, 2)),
            _ => Request(source, source, new RedactionRectangle(1, 1, 2, 2)),
        };

        var result = ScreenshotRedactionValidator.Validate(request, ImageWidth, ImageHeight);
        var errors = string.Join("\n", result.Errors);

        Assert.True(result.HasErrors);
        Assert.DoesNotContain(workspace.Root, errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("source.png", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("output.png", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing-source", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("relative/", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("AppData", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Temp", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Users", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, errors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Validator_DoesNotCreateOutputFileOrDirectory()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();
        var output = workspace.PathIn("output.png");
        var before = Directory.GetFileSystemEntries(workspace.Root).Order(StringComparer.Ordinal).ToList();

        var result = ScreenshotRedactionValidator.Validate(
            Request(source, output, new RedactionRectangle(1, 1, 2, 2)),
            ImageWidth,
            ImageHeight);

        Assert.Empty(result.Errors);
        Assert.False(File.Exists(output), "validator は output file を作成しないこと");
        Assert.Equal(
            before,
            Directory.GetFileSystemEntries(workspace.Root).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void RequestAndRegions_AreNotModified()
    {
        using var workspace = ScreenshotTestData.CreateWorkspace();
        var source = workspace.CreateDummyPng();
        var output = workspace.PathIn("output.png");
        var regions = new List<RedactionRectangle>
        {
            new(-10, -10, 40, 40),
            new(0, 0, 10, 10),
        };
        var request = new ScreenshotRedactionRequest
        {
            SourceImagePath = source,
            OutputPath = output,
            Regions = regions,
        };
        var regionsSnapshot = regions.ToList();
        var regionsInstance = request.Regions;

        _ = ScreenshotRedactionValidator.Validate(request, ImageWidth, ImageHeight);

        Assert.Equal(regionsSnapshot, regions);
        Assert.Same(regionsInstance, request.Regions);
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
