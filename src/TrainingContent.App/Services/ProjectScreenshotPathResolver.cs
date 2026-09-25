// UseWPF=true の project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using Path = System.IO.Path;

namespace TrainingContent.App.Services;

/// <summary><see cref="ProjectScreenshotPathResolver.Resolve"/> の結果。</summary>
/// <param name="AbsolutePath">成功時の絶対パス。<see cref="Path.GetFullPath(string)"/> 済み。</param>
/// <param name="ErrorMessage">失敗理由（<b>Trace 用</b>。UI へは generic message を出す）。</param>
public sealed record ScreenshotPathResolution(bool Succeeded, string? AbsolutePath, string? ErrorMessage)
{
    public static ScreenshotPathResolution Success(string absolutePath) => new(true, absolutePath, null);

    public static ScreenshotPathResolution Failure(string message) => new(false, null, message);
}

/// <summary>
/// Review UI（C: Redaction）用の narrow な screenshot path resolver。
/// project-relative な <c>ScreenshotPath</c>（契約 §18）を、Project directory 配下の絶対パスへ解決する。
///
/// <para>
/// <b>安全条件</b>: null / blank、rooted、leading <c>/</c> ・ <c>\</c>、drive specification、backslash、
/// 空 segment、<c>.</c> / <c>..</c>、<c>.png</c> 以外をすべて reject し、解決後の絶対パスが
/// Project directory 配下であることも確認する（脱出 path・別 drive・別 directory を許さない）。
/// </para>
/// <para>
/// Storage の <c>ProjectStore.UpdateStepScreenshotAsync</c> 側にも project-relative 検査があるが、
/// <b>画像を読む前に</b> App 側で境界を通すためにここでも検査する（共有 ProjectValidator の既知 defect は
/// 変更しない）。
/// </para>
/// <para>
/// 純関数（filesystem を読まない）。file の存在確認は呼出側の責務。
/// </para>
/// </summary>
public static class ProjectScreenshotPathResolver
{
    /// <summary>許可する拡張子（小文字比較）。</summary>
    private const string PngExtension = ".png";

    /// <summary>
    /// <paramref name="projectRelativePath"/> を <paramref name="projectDirectory"/> 配下の絶対パスへ解決する。
    /// </summary>
    /// <returns>成功なら絶対パス、失敗なら理由（path 実値を含まない）。</returns>
    public static ScreenshotPathResolution Resolve(string projectDirectory, string? projectRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);

        if (string.IsNullOrWhiteSpace(projectRelativePath))
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath が未設定です。");
        }

        if (Path.IsPathRooted(projectRelativePath))
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath に絶対パスは指定できません。");
        }

        if (projectRelativePath[0] is '/' or '\\')
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath を directory 区切りで開始できません。");
        }

        if (projectRelativePath.Length >= 2 && projectRelativePath[1] == ':')
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath に drive 指定はできません。");
        }

        if (projectRelativePath.Contains('\\', StringComparison.Ordinal))
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath の区切りは '/' のみです。");
        }

        foreach (var segment in projectRelativePath.Split('/'))
        {
            if (segment.Length == 0)
            {
                return ScreenshotPathResolution.Failure("ScreenshotPath に空の segment は指定できません。");
            }

            if (segment is "." or "..")
            {
                return ScreenshotPathResolution.Failure("ScreenshotPath に '.' / '..' は指定できません。");
            }
        }

        if (!string.Equals(Path.GetExtension(projectRelativePath), PngExtension, StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath は .png のみ指定できます。");
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Path.Combine(projectDirectory, projectRelativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath を解決できません。");
        }

        // 解決後の絶対パスが Project directory 配下であること（"../" 等のすり抜けを最終的に塞ぐ）。
        var root = Path.GetFullPath(projectDirectory);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            return ScreenshotPathResolution.Failure("ScreenshotPath が Project directory の外を指しています。");
        }

        return ScreenshotPathResolution.Success(fullPath);
    }
}
