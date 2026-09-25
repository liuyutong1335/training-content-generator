// UseWPF=true の test project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using Path = System.IO.Path;
using TrainingContent.App.Services;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// C: <see cref="ProjectScreenshotPathResolver"/> の安全性（R1 / R2）。
/// project-relative な ScreenshotPath だけを Project directory 配下の絶対パスへ解決する。
/// </summary>
public class ProjectScreenshotPathResolverTests
{
    private static readonly string ProjectDirectory =
        Path.Combine(Path.GetTempPath(), "tc-app-tests", "resolver-proj");

    // =====================================================================
    // R1 — 正常系
    // =====================================================================

    [Fact]
    public void R1_original_の相対パスは_Project_配下の絶対パスへ解決される()
    {
        var resolved = ProjectScreenshotPathResolver.Resolve(
            ProjectDirectory, "screenshots/original/event-000001.png");

        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.Equal(
            Path.Combine(ProjectDirectory, "screenshots", "original", "event-000001.png"),
            resolved.AbsolutePath);
    }

    [Fact]
    public void R1b_edited_の相対パスも解決できる()
    {
        var resolved = ProjectScreenshotPathResolver.Resolve(
            ProjectDirectory,
            "screenshots/edited/step-0123456789abcdef0123456789abcdef-aabbccddeeff00112233445566778899.png");

        Assert.True(resolved.Succeeded, resolved.ErrorMessage);
        Assert.StartsWith(
            Path.Combine(ProjectDirectory, "screenshots", "edited") + Path.DirectorySeparatorChar,
            resolved.AbsolutePath!,
            StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // R2 — 拒否すべき入力
    // =====================================================================

    [Theory]
    [InlineData(@"C:\Windows\evil.png", "absolute")]
    [InlineData("C:/Windows/evil.png", "drive")]
    [InlineData("/etc/evil.png", "leading slash")]
    [InlineData(@"\server\share\evil.png", "leading backslash")]
    [InlineData(@"screenshots\original\event-000001.png", "backslash separator")]
    [InlineData("../evil.png", "parent segment")]
    [InlineData("screenshots/../../evil.png", "parent segment in middle")]
    [InlineData("./evil.png", "current segment")]
    [InlineData("screenshots//event-000001.png", "empty segment")]
    [InlineData("screenshots/", "trailing separator")]
    [InlineData("screenshots/original/event-000001.txt", "not png")]
    [InlineData("screenshots/original/event-000001", "no extension")]
    [InlineData("   ", "blank")]
    public void R2_安全でない_path_は拒否される(string path, string reason)
    {
        var resolved = ProjectScreenshotPathResolver.Resolve(ProjectDirectory, path);

        Assert.False(resolved.Succeeded, $"拒否されるべき path が通りました（{reason}）: {path}");
        Assert.Null(resolved.AbsolutePath);
    }

    [Fact]
    public void R2b_null_は拒否される()
    {
        var resolved = ProjectScreenshotPathResolver.Resolve(ProjectDirectory, null);

        Assert.False(resolved.Succeeded);
        Assert.Null(resolved.AbsolutePath);
    }

    [Fact]
    public void R2c_拒否理由に_path_実値を含めない()
    {
        // UI へ出さない前提の message なので、absolute path の実値を載せない。
        var resolved = ProjectScreenshotPathResolver.Resolve(ProjectDirectory, @"C:\secret\place\evil.png");

        Assert.False(resolved.Succeeded);
        Assert.DoesNotContain("secret", resolved.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", resolved.ErrorMessage!, StringComparison.Ordinal);
    }
}
