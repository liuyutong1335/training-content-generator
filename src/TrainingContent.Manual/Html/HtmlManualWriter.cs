using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace TrainingContent.Manual.Html;

/// <summary>
/// HtmlManualWriter の結果。Error が 1 件でもある場合 <see cref="Content"/> は空であり、
/// 部分的な HTML を返さない（不完全な教材を生成しない）。
/// </summary>
public sealed class HtmlWriteResult
{
    public string Content { get; init; } = "";

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// ManualDocument → 完全な単一 HTML 文字列（docs/development-plan.md §19、phase0-contract.md §17 / §18）。
/// <para>
/// 表示順序と内容は Markdown と同一: タイトル → 学習目標 → 対象者 → 事前準備 → 操作手順
/// （Step ごとに 見出し・Description・Screenshot・注意・完了条件）→ 完了確認。
/// Optional 項目の省略条件も Markdown と同一。
/// </para>
/// <para>
/// 出力は atomic: 全 Step を先に検証し、Error が 1 件でもあれば Content は完全に空で返す。
/// 判定と Error 文言・screenshotPath の percent-encoding は <see cref="ManualScreenshotPath"/> を
/// Markdown と共有する。
/// </para>
/// <para>
/// 安全性: ユーザー由来の文字列は <see cref="HtmlEncoder"/> で text node / attribute の両方で escape する。
/// 外部通信・JavaScript・外部 CSS・data URI は使わず、固定 CSS のみをインラインで持つ。
/// ユーザー入力を CSS へ埋め込まない（CSS は定数のみ）。改行は LF 固定・末尾は改行1つ。
/// 入力 <see cref="ManualDocument"/> は変更しない。ファイル I/O は本クラスの責務外。
/// </para>
/// </summary>
public static class HtmlManualWriter
{
    private const int IndentSize = 2;

    private const string CompletionSectionBody = "以上の手順で操作が完了します。";

    /// <summary>HTML の sensitive 文字（&lt; &gt; &amp; " '）は常に escape しつつ、日本語は可読なまま残す。</summary>
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);

    // 改行類（LF / CR / VT / FF / NEL(U+0085) / LS(U+2028) / PS(U+2029)）は空白へ置換し、
    // 1 行 1 要素の出力へユーザー文字列が改行を持ち込まないようにする（Markdown と同じ扱い）。
    private static readonly char[] LineBreakCharacters = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    // 固定の最小 CSS。外部 CSS・外部フォントは参照しない（ユーザー入力は一切埋め込まない）。
    private static readonly string[] StyleSheet =
    [
        "body { font-family: sans-serif; line-height: 1.6; margin: 0 auto; max-width: 48rem; padding: 1rem; }",
        "h1 { border-bottom: 2px solid #cccccc; padding-bottom: 0.5rem; }",
        "section { margin-top: 1.5rem; }",
        "article { border-top: 1px solid #dddddd; padding: 1rem 0 0; }",
        "img { max-width: 100%; height: auto; }",
        ".caution { color: #a00000; }",
    ];

    public static HtmlWriteResult Write(ManualDocument? document)
    {
        if (document is null)
        {
            return new HtmlWriteResult { Errors = ["ManualDocument が null のため HTML を生成できません。"] };
        }

        var errors = new List<string>();

        IReadOnlyList<ManualStep> steps = document.Steps is null ? [] : document.Steps;

        // atomic: Markdown と同一の判定・Error 文言で全 Step を先に検証する。
        ManualScreenshotPath.CollectErrors(steps, errors);
        if (errors.Count > 0)
        {
            return new HtmlWriteResult { Errors = errors };
        }

        var lines = new List<string>
        {
            "<!doctype html>",
            "<html lang=\"ja\">",
            Line(1, "<head>"),
            Line(2, "<meta charset=\"utf-8\">"),
            Line(2, "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"),
            Line(2, $"<title>{Encode(document.Title)}</title>"),
            Line(2, "<style>"),
        };

        lines.AddRange(StyleSheet.Select(cssLine => Line(3, cssLine)));

        lines.Add(Line(2, "</style>"));
        lines.Add(Line(1, "</head>"));
        lines.Add(Line(1, "<body>"));
        lines.Add(Line(2, "<main>"));
        lines.Add(Line(3, $"<h1>{Encode(document.Title)}</h1>"));

        AddTextSection(lines, "学習目標", document.Objective);
        AddTextSection(lines, "対象者", document.TargetAudience);
        AddPrerequisitesSection(lines, document.Prerequisites);

        lines.Add(Line(3, "<section>"));
        lines.Add(Line(4, "<h2>操作手順</h2>"));
        lines.AddRange(steps.SelectMany(BuildStep));
        lines.Add(Line(3, "</section>"));

        lines.Add(Line(3, "<section>"));
        lines.Add(Line(4, "<h2>完了確認</h2>"));
        lines.Add(Line(4, $"<p>{Encode(CompletionSectionBody)}</p>"));
        lines.Add(Line(3, "</section>"));

        lines.Add(Line(2, "</main>"));
        lines.Add(Line(1, "</body>"));
        lines.Add("</html>");

        return new HtmlWriteResult
        {
            Content = string.Join("\n", lines) + "\n",
            Errors = errors,
        };
    }

    private static string Line(int level, string content) => new string(' ', level * IndentSize) + content;

    private static void AddTextSection(List<string> lines, string heading, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        lines.Add(Line(3, "<section>"));
        lines.Add(Line(4, $"<h2>{Encode(heading)}</h2>"));
        lines.Add(Line(4, $"<p>{Encode(value)}</p>"));
        lines.Add(Line(3, "</section>"));
    }

    private static void AddPrerequisitesSection(List<string> lines, IReadOnlyList<string>? prerequisites)
    {
        IReadOnlyList<string> items = prerequisites is null
            ? []
            : [.. prerequisites.Where(item => !string.IsNullOrWhiteSpace(item))];
        if (items.Count == 0)
        {
            return;
        }

        lines.Add(Line(3, "<section>"));
        lines.Add(Line(4, "<h2>事前準備</h2>"));
        lines.Add(Line(4, "<ul>"));
        lines.AddRange(items.Select(item => Line(5, $"<li>{Encode(item)}</li>")));
        lines.Add(Line(4, "</ul>"));
        lines.Add(Line(3, "</section>"));
    }

    /// <summary>Step 1 件分（§19 の表示項目）。検証済みの値のみ渡すこと。</summary>
    private static IEnumerable<string> BuildStep(ManualStep step)
    {
        yield return Line(4, "<article>");
        yield return Line(
            5,
            $"<h3>{Encode(step.Order.ToString(CultureInfo.InvariantCulture))}. {Encode(step.Title)}</h3>");

        if (!string.IsNullOrWhiteSpace(step.Description))
        {
            yield return Line(5, $"<p>{Encode(step.Description)}</p>");
        }

        if (!ManualScreenshotPath.IsUnset(step.ScreenshotPath))
        {
            var reference = Encode(ManualScreenshotPath.ToRelativeReference(step.ScreenshotPath));
            yield return Line(5, $"<p><img src=\"{reference}\" alt=\"{Encode(step.Title)}\"></p>");
        }

        if (!string.IsNullOrWhiteSpace(step.Caution))
        {
            yield return Line(5, $"<p class=\"caution\"><strong>注意:</strong> {Encode(step.Caution)}</p>");
        }

        if (!string.IsNullOrWhiteSpace(step.ExpectedResult))
        {
            yield return Line(5, $"<p><strong>完了条件:</strong> {Encode(step.ExpectedResult)}</p>");
        }

        yield return Line(4, "</article>");
    }

    /// <summary>改行類を空白へ置換したうえで HTML escape する（text node / attribute 共通）。</summary>
    private static string Encode(string value) => Encoder.Encode(NeutralizeLineBreaks(value));

    private static string NeutralizeLineBreaks(string value)
    {
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            var character = value[i];

            if (Array.IndexOf(LineBreakCharacters, character) >= 0)
            {
                if (character == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append(' ');
                continue;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
