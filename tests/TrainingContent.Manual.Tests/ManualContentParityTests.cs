using System.Net;
using System.Text;
using Xunit;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// Markdown と HTML の内容一致（docs/development-plan.md §19 / G4 Output Gate の前提）。
/// 構文文字そのものは比較せず、外部 parser を使わずに「表示される論理的内容」を
/// 最小限の抽出処理で取り出して比較する。
/// </summary>
public class ManualContentParityTests
{
    /// <summary>Markdown の escapable punctuation（Writer と同じ集合。比較のため literal 化する）。</summary>
    private const string MarkdownEscapableCharacters = "\\`*_{}[]()#+-!|<>~";

    [Fact]
    public void LogicalContent_MatchesBetweenMarkdownAndHtml()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "「新規申請」をクリックします", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "「社員番号」に入力します"));

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.Equal(ExtractMarkdown(result.Markdown!.Content), ExtractHtml(result.Html!.Content));
    }

    [Fact]
    public void LogicalContent_MatchesWhenOptionalSectionsAreOmitted()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Objective = null;
        project.TargetAudience = "";
        project.Prerequisites = ["", "   "];

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        var markdown = ExtractMarkdown(result.Markdown!.Content);
        var html = ExtractHtml(result.Html!.Content);

        Assert.Equal(markdown, html);
        Assert.DoesNotContain(markdown, item => item.Value.Contains("学習目標", StringComparison.Ordinal));
        Assert.DoesNotContain(markdown, item => item.Value.Contains("対象者", StringComparison.Ordinal));
        Assert.DoesNotContain(markdown, item => item.Value.Contains("事前準備", StringComparison.Ordinal));
    }

    [Fact]
    public void StepOptionalFields_AreOmittedInBothFormats()
    {
        var step = ManualTestData.Step(1, "手順1");
        step.Description = null;
        step.Caution = "   ";
        step.ExpectedResult = null;

        var result = ManualGenerator.Generate(ManualTestData.Project(step));

        var markdown = ExtractMarkdown(result.Markdown!.Content);
        var html = ExtractHtml(result.Html!.Content);

        Assert.Equal(markdown, html);
        Assert.DoesNotContain(markdown, item => item.Kind is "caution" or "expected");
        Assert.DoesNotContain(markdown, item => item.Kind == "img");
    }

    [Fact]
    public void LogicalContent_MatchesWithMarkdownAndHtmlSyntaxCharacters()
    {
        // Markdown 構文文字・HTML 構文文字を含む値でも、表示される論理的内容が一致すること。
        var step = ManualTestData.Step(1, "*太字* _斜体_ `code` | 表 <b>タグ</b> & \"引用\"");
        step.Description = "[link](javascript:alert(1)) <script>alert(2)</script>";
        step.Caution = "# 見出し風 & 'アポストロフィ'";
        step.ExpectedResult = "> 引用風 <img src=x onerror=alert(3)>";

        var project = ManualTestData.Project(step);
        project.Objective = "目標 *重要* & <強調>";
        project.Prerequisites = ["<b>前提</b>", "1. 箇条書き風"];

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.Equal(ExtractMarkdown(result.Markdown!.Content), ExtractHtml(result.Html!.Content));
    }

    [Fact]
    public void JapaneseContent_IsPreservedInBothFormats()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "日本語の手順", "screenshots/edited/申請画面.png"));

        var result = ManualGenerator.Generate(project);

        Assert.Contains("日本語の手順", result.Markdown!.Content, StringComparison.Ordinal);
        Assert.Contains("日本語の手順", result.Html!.Content, StringComparison.Ordinal);
        Assert.Contains("../screenshots/edited/申請画面.png", result.Markdown.Content, StringComparison.Ordinal);
        Assert.Contains("src=\"../screenshots/edited/申請画面.png\"", result.Html.Content, StringComparison.Ordinal);
    }

    // --- 抽出（最小限。外部 parser は使わない） ---

    private static List<(string Kind, string Value)> ExtractMarkdown(string content)
    {
        var items = new List<(string Kind, string Value)>();

        foreach (var line in content.Split('\n').Select(line => line.TrimEnd()))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                items.Add(("h1", UnescapeMarkdown(line[2..])));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                items.Add(("h2", UnescapeMarkdown(line[3..])));
            }
            else if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                items.Add(("h3", UnescapeMarkdown(line[4..])));
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                items.Add(("li", UnescapeMarkdown(line[2..])));
            }
            else if (line.StartsWith("**注意:** ", StringComparison.Ordinal))
            {
                items.Add(("caution", UnescapeMarkdown(line["**注意:** ".Length..])));
            }
            else if (line.StartsWith("**完了条件:** ", StringComparison.Ordinal))
            {
                items.Add(("expected", UnescapeMarkdown(line["**完了条件:** ".Length..])));
            }
            else if (line.StartsWith("![", StringComparison.Ordinal))
            {
                var close = line.IndexOf("](", StringComparison.Ordinal);
                items.Add(("img", UnescapeMarkdown(line[2..close]) + "|" + line[(close + 2)..^1]));
            }
            else
            {
                items.Add(("p", UnescapeMarkdown(line)));
            }
        }

        return items;
    }

    private static List<(string Kind, string Value)> ExtractHtml(string content)
    {
        var items = new List<(string Kind, string Value)>();

        foreach (var line in content.Split('\n').Select(line => line.Trim()))
        {
            if (line.StartsWith("<h1>", StringComparison.Ordinal))
            {
                items.Add(("h1", Decode(Inner(line, "h1"))));
            }
            else if (line.StartsWith("<h2>", StringComparison.Ordinal))
            {
                items.Add(("h2", Decode(Inner(line, "h2"))));
            }
            else if (line.StartsWith("<h3>", StringComparison.Ordinal))
            {
                items.Add(("h3", Decode(Inner(line, "h3"))));
            }
            else if (line.StartsWith("<li>", StringComparison.Ordinal))
            {
                items.Add(("li", Decode(Inner(line, "li"))));
            }
            else if (line.StartsWith("<p class=\"caution\">", StringComparison.Ordinal))
            {
                items.Add(("caution", Decode(AfterLabel(line, "注意:"))));
            }
            else if (line.StartsWith("<p><strong>完了条件:</strong>", StringComparison.Ordinal))
            {
                items.Add(("expected", Decode(AfterLabel(line, "完了条件:"))));
            }
            else if (line.StartsWith("<p><img ", StringComparison.Ordinal))
            {
                items.Add(("img", Decode(Attribute(line, "alt")) + "|" + Decode(Attribute(line, "src"))));
            }
            else if (line.StartsWith("<p>", StringComparison.Ordinal))
            {
                items.Add(("p", Decode(Inner(line, "p"))));
            }
        }

        return items;
    }

    private static string Inner(string line, string tag)
    {
        var open = $"<{tag}>";
        var close = $"</{tag}>";
        return line[open.Length..^close.Length];
    }

    private static string AfterLabel(string line, string label)
    {
        var marker = $"<strong>{label}</strong>";
        var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var close = line.LastIndexOf("</p>", StringComparison.Ordinal);
        return line[start..close].TrimStart();
    }

    private static string Attribute(string line, string name)
    {
        var marker = $"{name}=\"";
        var start = line.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = line.IndexOf('"', start);
        return line[start..end];
    }

    private static string Decode(string value) => WebUtility.HtmlDecode(value);

    private static string UnescapeMarkdown(string value)
    {
        var builder = new StringBuilder(value.Length);

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\'
                && i + 1 < value.Length
                && MarkdownEscapableCharacters.Contains(value[i + 1], StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }
}
