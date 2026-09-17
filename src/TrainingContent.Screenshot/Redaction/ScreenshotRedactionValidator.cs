using System.Security;

namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// Redaction 入力の事前検証（画像の加工・PNG の書き込み・file / directory の作成は行わない）。
/// <list type="bullet">
///   <item>検出可能な Error を全件収集する。Error が 1 件でもあれば <c>NormalizedRegions</c> は空（atomic）</item>
///   <item><c>SourceImagePath</c> / <c>OutputPath</c> は App / Storage 境界から渡される
///   <b>ファイルシステム上の実パス</b>（project-relative path ではない）。絶対パスかつ <c>.png</c> のみ許可し、
///   自動補正はしない</item>
///   <item>Region は入力順を維持し、画像境界への clamp のみ行う（統合・並べ替え・重複排除はしない）</item>
///   <item>Error 文言には field 名と理由のみを含め、path 実値（drive・ユーザー名・server/share・
///   ディレクトリ・ファイル名）を含めない</item>
///   <item>symlink / hardlink による同一性判定は本クラスの範囲外（実体 path の文字列比較まで）</item>
///   <item>画像内容の decode は行わない（ファイルの存在確認まで）</item>
/// </list>
/// </summary>
public static class ScreenshotRedactionValidator
{
    public static ScreenshotRedactionValidationResult Validate(
        ScreenshotRedactionRequest? request,
        int imageWidth,
        int imageHeight)
    {
        if (request is null)
        {
            return Failure("Redaction request が未設定です。");
        }

        var errors = new List<string>();

        // image
        if (imageWidth <= 0)
        {
            errors.Add($"imageWidth は 1 以上であること: {imageWidth}。");
        }

        if (imageHeight <= 0)
        {
            errors.Add($"imageHeight は 1 以上であること: {imageHeight}。");
        }

        // path
        var sourceFullPath = ValidateSourceImagePath(request.SourceImagePath, errors);
        var outputFullPath = ValidateOutputPath(request.OutputPath, errors);

        if (sourceFullPath is not null
            && outputFullPath is not null
            && string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add("SourceImagePath と OutputPath に同じファイルは指定できません。");
        }

        // regions
        var normalizedRegions = new List<RedactionRectangle>();
        var regions = request.Regions;
        if (regions is null || regions.Count == 0)
        {
            errors.Add("Regions は 1 件以上指定してください。");
        }
        else
        {
            for (var index = 0; index < regions.Count; index++)
            {
                ValidateRegion(regions[index], index + 1, imageWidth, imageHeight, normalizedRegions, errors);
            }
        }

        if (errors.Count > 0)
        {
            // 部分的な plan を返さない。
            return new ScreenshotRedactionValidationResult { Errors = errors };
        }

        return new ScreenshotRedactionValidationResult { NormalizedRegions = normalizedRegions };
    }

    // --- path ---

    private static string? ValidateSourceImagePath(string? sourceImagePath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath))
        {
            errors.Add("SourceImagePath が未設定です。");
            return null;
        }

        if (!TryGetFullPath(sourceImagePath, "SourceImagePath", errors, out var fullPath))
        {
            return null;
        }

        if (!HasPngExtension(fullPath))
        {
            errors.Add("SourceImagePath は .png を指定してください。");
            return null;
        }

        if (!FileExists(fullPath))
        {
            errors.Add("SourceImagePath のファイルが存在しません。");
            return null;
        }

        return fullPath;
    }

    private static string? ValidateOutputPath(string? outputPath, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            errors.Add("OutputPath が未設定です。");
            return null;
        }

        if (!TryGetFullPath(outputPath, "OutputPath", errors, out var fullPath))
        {
            return null;
        }

        if (!HasPngExtension(fullPath))
        {
            errors.Add("OutputPath は .png を指定してください。");
            return null;
        }

        if (!ParentDirectoryExists(fullPath))
        {
            errors.Add("OutputPath の親ディレクトリが存在しません。");
            return null;
        }

        return fullPath;
    }

    /// <summary>
    /// 絶対パスであることを確認して実体 path を返す。path の自動補正は行わない。
    /// path として解釈できない入力は例外を外へ出さず Error にする。
    /// </summary>
    private static bool TryGetFullPath(string path, string field, List<string> errors, out string fullPath)
    {
        fullPath = "";

        bool isFullyQualified;
        try
        {
            isFullyQualified = Path.IsPathFullyQualified(path);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            errors.Add($"{field} は絶対パスで指定してください。");
            return false;
        }

        if (!isFullyQualified)
        {
            errors.Add($"{field} は絶対パスで指定してください。");
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            // path 実値は Error に含めない。
            errors.Add($"{field} を解釈できません（指定を確認してください）。");
            return false;
        }
    }

    private static bool HasPngExtension(string fullPath)
    {
        try
        {
            return string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }
    }

    /// <summary>存在確認できない場合（権限等）も例外を外へ出さず false を返す。</summary>
    private static bool FileExists(string fullPath)
    {
        try
        {
            return File.Exists(fullPath);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }
    }

    private static bool ParentDirectoryExists(string fullPath)
    {
        try
        {
            var parent = Path.GetDirectoryName(fullPath);
            return !string.IsNullOrEmpty(parent) && Directory.Exists(parent);
        }
        catch (Exception ex) when (IsPathException(ex))
        {
            return false;
        }
    }

    /// <summary>path 操作で投げられうる例外（想定外例外は握り潰さない）。</summary>
    private static bool IsPathException(Exception ex) =>
        ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or SecurityException
            or IOException
            or UnauthorizedAccessException;

    // --- region ---

    private static void ValidateRegion(
        RedactionRectangle region,
        int regionNumber,
        int imageWidth,
        int imageHeight,
        List<RedactionRectangle> normalizedRegions,
        List<string> errors)
    {
        var sizeIsValid = true;

        if (region.Width <= 0)
        {
            errors.Add($"Region {regionNumber} の Width は 1 以上であること: {region.Width}。");
            sizeIsValid = false;
        }

        if (region.Height <= 0)
        {
            errors.Add($"Region {regionNumber} の Height は 1 以上であること: {region.Height}。");
            sizeIsValid = false;
        }

        if (!sizeIsValid)
        {
            return;
        }

        if (imageWidth <= 0 || imageHeight <= 0)
        {
            // 画像サイズが不正なため clamp できない（image の Error は収集済み）。
            return;
        }

        // int overflow を避けるため右端・下端は long で計算する。
        var right = (long)region.X + region.Width;
        var bottom = (long)region.Y + region.Height;

        var left = Math.Max(0L, region.X);
        var top = Math.Max(0L, region.Y);
        var clampedRight = Math.Min(imageWidth, right);
        var clampedBottom = Math.Min(imageHeight, bottom);

        if (left >= clampedRight || top >= clampedBottom)
        {
            errors.Add($"Region {regionNumber} は画像の範囲外です（画像 {imageWidth}x{imageHeight}）。");
            return;
        }

        normalizedRegions.Add(new RedactionRectangle(
            (int)left,
            (int)top,
            (int)(clampedRight - left),
            (int)(clampedBottom - top)));
    }

    private static ScreenshotRedactionValidationResult Failure(string error) =>
        new() { Errors = [error] };
}
