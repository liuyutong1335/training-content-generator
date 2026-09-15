using System.Globalization;
using System.Text;

namespace TrainingContent.Manual.Markdown;

/// <summary>
/// MarkdownManualWriter の結果。Error が 1 件でもある場合 <see cref="Content"/> は空であり、
/// 部分的な Markdown を返さない（不完全な教材を生成しない）。
/// </summary>
public sealed class MarkdownWriteResult
{
    public string Content { get; init; } = "";

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// ManualDocument → Markdown 文字列（docs/development-plan.md §19、phase0-contract.md §17 / §18）。
/// <para>
/// 出力構成（この順序で固定）: タイトル → 学習目標 → 対象者 → 事前準備 → 操作手順（Step ごとに
/// 見出し・Description・Screenshot・Caution・ExpectedResult）→ 完了確認。
/// Objective / TargetAudience / Prerequisites と Step の Optional 項目は、値が無ければセクション・行ごと省略する。
/// </para>
/// <para>
/// 出力は atomic: 全 Step を先に検証し、Error が 1 件でもあれば Content は完全に空で返す
/// （正常 Step だけの部分 Markdown も返さない）。検出可能な Error は全件収集する。
/// </para>
/// <para>
/// 安全性: ユーザー由来の文字列は Markdown として解釈させない（構文文字を backslash escape、改行は空白へ、
/// raw HTML を有効なタグとして残さない）。改行は LF 固定・末尾は改行1つ。
/// Error メッセージには Step.Order・field 名・理由のみを含め、受け取った path 全文は含めない。
/// 入力 <see cref="ManualDocument"/> は変更しない（出力時のみ安全化する）。
/// ファイル I/O と HTML 生成は本クラスの責務外。
/// </para>
/// </summary>
public static class MarkdownManualWriter
{
    private const string LineSeparator = "\n";

    private const string CompletionSectionBody = "以上の手順で操作が完了します。";

    // CommonMark の escapable punctuation（バックスラッシュで literal にできる ASCII 記号）。
    // '.' は行頭の ordered list のみが問題で、改行を空白へ置換して行頭を作らせないため対象外（可読性優先）。
    private const string EscapableCharacters = "\\`*_{}[]()#+-!|<>~";


    // 行頭記号（見出し・リスト）の注入を防ぐため空白へ置換する文字。
    // LF / CR / VT / FF / NEL(U+0085) / LS(U+2028) / PS(U+2029)。
    private static readonly char[] LineBreakCharacters = ['\n', '\r', '\v', '\f', '\u0085', '\u2028', '\u2029'];

    public static MarkdownWriteResult Write(ManualDocument? document)
    {
        if (document is null)
        {
            return new MarkdownWriteResult { Errors = ["ManualDocument が null のため Markdown を生成できません。"] };
        }

        var errors = new List<string>();

        IReadOnlyList<ManualStep> steps = document.Steps is null ? [] : document.Steps;

        // 検証を全 Step 分先に実施する。Error が 1 件でもあれば Markdown を一切生成しない（atomic）。
        // 不完全な教材を出さないため、正常 Step だけの部分 Markdown も返さない。
        // 判定・Error 文言は HTML と共通（ManualScreenshotPath）。
        ManualScreenshotPath.CollectErrors(steps, errors);

        if (errors.Count > 0)
        {
            return new MarkdownWriteResult { Errors = errors };
        }

        var blocks = new List<string> { $"# {Escape(document.Title)}" };

        AddOptionalSection(blocks, "学習目標", document.Objective);
        AddOptionalSection(blocks, "対象者", document.TargetAudience);

        IReadOnlyList<string> prerequisites = document.Prerequisites is null
            ? []
            : [.. document.Prerequisites.Where(item => !string.IsNullOrWhiteSpace(item))];
        if (prerequisites.Count > 0)
        {
            var lines = new List<string> { "## 事前準備" };
            lines.AddRange(prerequisites.Select(item => $"- {Escape(item)}"));
            blocks.Add(string.Join(LineSeparator, lines));
        }

        blocks.Add("## 操作手順");

        foreach (var step in steps)
        {
            blocks.Add(BuildStep(step));
        }

        blocks.Add(string.Join(LineSeparator, "## 完了確認", CompletionSectionBody));

        return new MarkdownWriteResult
        {
            Content = string.Join(LineSeparator + LineSeparator, blocks) + LineSeparator,
            Errors = errors,
        };
    }

    private static void AddOptionalSection(List<string> blocks, string heading, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        blocks.Add(string.Join(LineSeparator, $"## {heading}", Escape(value)));
    }

    /// <summary>
    /// Step 1 件分。見出し直下は Description を 1 行で続け、以降の要素は空行で区切る（§19 の構成）。
    /// 呼び出し前に <see cref="ManualScreenshotPath.CollectErrors"/> で検証済みであること。
    /// </summary>
    private static string BuildStep(ManualStep step)
    {
        var block = new StringBuilder($"### {step.Order.ToString(CultureInfo.InvariantCulture)}. {Escape(step.Title)}");

        if (!string.IsNullOrWhiteSpace(step.Description))
        {
            block.Append(LineSeparator).Append(Escape(step.Description));
        }

        var image = BuildImageLink(step);
        if (image is not null)
        {
            block.Append(LineSeparator).Append(LineSeparator).Append(image);
        }

        if (!string.IsNullOrWhiteSpace(step.Caution))
        {
            block.Append(LineSeparator).Append(LineSeparator).Append($"**注意:** {Escape(step.Caution)}");
        }

        if (!string.IsNullOrWhiteSpace(step.ExpectedResult))
        {
            block.Append(LineSeparator).Append(LineSeparator).Append($"**完了条件:** {Escape(step.ExpectedResult)}");
        }

        return block.ToString();
    }

    /// <summary>
    /// 画像リンク（確定仕様: <c>../{screenshotPath}</c>）。未設定は null。
    /// 検証と percent-encoding は HTML と共通（ManualScreenshotPath）。検証済みの値のみ渡すこと。
    /// </summary>
    private static string? BuildImageLink(ManualStep step)
    {
        var screenshotPath = step.ScreenshotPath;
        if (ManualScreenshotPath.IsUnset(screenshotPath))
        {
            return null;
        }

        return $"![{Escape(step.Title)}]({ManualScreenshotPath.ToRelativeReference(screenshotPath)})";
    }

    /// <summary>
    /// ユーザー由来の文字列を Markdown として解釈させない。
    /// 構文文字は backslash escape し、改行類は空白へ置換して行頭記号（見出し・リスト）の注入を防ぐ。
    /// 値そのものは変更しない（出力時のみの安全化）。
    /// </summary>
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length + 8);

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

            if (EscapableCharacters.Contains(character, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(character);
        }

        return builder.ToString();
    }
}
