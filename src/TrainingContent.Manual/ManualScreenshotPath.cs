using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace TrainingContent.Manual;

/// <summary>
/// screenshotPath の検証と出力用変換の共通処理（phase0-contract.md §17 / §18）。
/// Markdown / HTML の両 Writer が同じ判定・同じ Error 文言・同じ percent-encoding を使うことを保証する。
/// <para>
/// Error 文言には Step.Order・field 名・理由のみを含め、受け取った path 全文は含めない
/// （ユーザー名・drive path・UNC server/share・ローカルディレクトリをログへ漏らさないため）。
/// 未設定（null / 空 / whitespace）は未設定として扱い Error にしない。
/// </para>
/// </summary>
internal static class ManualScreenshotPath
{
    private const char PosixSeparator = '/';

    // Markdown の link destination と URL の区切りを壊す文字（'(' ')' は destination を閉じてしまう）。
    // '/' は階層区切りのため維持する。
    private const string PathUnsafeCharacters = "\"#%<>()[\\]^`{|}?";

    /// <summary>未設定（null / 空 / whitespace）か。未設定は Error ではない。</summary>
    public static bool IsUnset([NotNullWhen(false)] string? path) => string.IsNullOrWhiteSpace(path);

    /// <summary>不正な場合のみ理由（path を含まない）を返す。正常なら false。</summary>
    public static bool TryGetInvalidReason(string path, out string reason)
    {
        if (IsAbsolutePath(path))
        {
            reason = "absolute path（絶対パス）は使用できない";
            return true;
        }

        if (path.Contains('\\'))
        {
            reason = "backslash 区切りは使用できない（/ に統一すること）";
            return true;
        }

        if (path.Split(PosixSeparator).Any(segment => segment is "." or ".."))
        {
            reason = "traversal（'.' / '..'）セグメントは使用できない";
            return true;
        }

        reason = "";
        return false;
    }

    /// <summary>
    /// 出力用の相対参照（<c>../{percent-encoded path}</c>）。
    /// 検証済みの値に対してのみ呼び出すこと（不正な path を黙って修正する用途では使わない）。
    /// </summary>
    public static string ToRelativeReference(string path) =>
        "../" + string.Join(PosixSeparator.ToString(), path.Split(PosixSeparator).Select(EncodeSegment));

    /// <summary>Error 文言。Step.Order・field 名・理由のみを含み、path 全文は含めない。</summary>
    public static string BuildErrorMessage(int order, string reason) =>
        $"Step {order.ToString(CultureInfo.InvariantCulture)}: screenshotPath が不正です（{reason}）。";

    /// <summary>
    /// 全 Step を検証し、検出可能な Error を収集する（atomic 出力のための事前検証）。
    /// 未設定の Step は Error にしない。
    /// </summary>
    public static void CollectErrors(IEnumerable<ManualStep> steps, List<string> errors) =>
        CollectErrors(steps.Select(step => (step.Order, step.ScreenshotPath)), errors);

    /// <summary>
    /// Order と screenshotPath の組で検証する overload。
    /// TrainingStep（Core モデル）から直接検証したい呼び出し元（ManualDocumentBuilder）向け。
    /// </summary>
    public static void CollectErrors(IEnumerable<(int Order, string? ScreenshotPath)> steps, List<string> errors)
    {
        foreach (var (order, screenshotPath) in steps)
        {
            if (IsUnset(screenshotPath))
            {
                continue;
            }

            if (TryGetInvalidReason(screenshotPath, out var reason))
            {
                errors.Add(BuildErrorMessage(order, reason));
            }
        }
    }

    private static bool IsAbsolutePath(string path) =>
        path.StartsWith(PosixSeparator)
        || path.StartsWith('\\')
        || (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    /// <summary>
    /// link / src を壊す文字のみ percent-encode する。'/' は階層区切りとして維持し、
    /// 日本語などの非 ASCII は可読性のためそのまま残す。自動修正ではなく出力時の escape。
    /// </summary>
    private static string EncodeSegment(string segment)
    {
        var builder = new System.Text.StringBuilder(segment.Length);

        foreach (var character in segment)
        {
            if (character <= ' ' || character == '\u007F' || PathUnsafeCharacters.Contains(character, StringComparison.Ordinal))
            {
                builder.Append('%').Append(((int)character).ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
