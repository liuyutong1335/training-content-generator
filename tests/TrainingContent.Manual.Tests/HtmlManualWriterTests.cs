using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Manual.Html;
using TrainingContent.Manual.Markdown;
using Xunit;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// HtmlManualWriter（docs/development-plan.md §19）。
/// ManualDocument → 完全な単一 HTML 文字列。ファイル I/O は本クラスの責務外。
/// 表示順序・省略条件・atomic Error は Markdown と揃っていることを併せて検証する。
/// </summary>
public class HtmlManualWriterTests
{
    private const string Title = "経費申請登録";
    private const string Objective = "経費申請を登録できるようになる";
    private const string Audience = "新入社員";

    // --- 正常系: 完全な HTML ---

    [Fact]
    public void FullDocument_MatchesExpectedHtml()
    {
        var document = Document(
            [
                StepItem(
                    1,
                    "「新規申請」をクリックします",
                    screenshotPath: "screenshots/edited/step-001.png",
                    description: "新しい申請を作成します。",
                    caution: "入力漏れに注意します。",
                    expectedResult: "申請入力画面が表示されます。"),
            ]);

        var result = HtmlManualWriter.Write(document);

        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
        Assert.Equal(
            """
            <!doctype html>
            <html lang="ja">
              <head>
                <meta charset="utf-8">
                <meta name="viewport" content="width=device-width, initial-scale=1">
                <title>経費申請登録</title>
                <style>
                  body { font-family: sans-serif; line-height: 1.6; margin: 0 auto; max-width: 48rem; padding: 1rem; }
                  h1 { border-bottom: 2px solid #cccccc; padding-bottom: 0.5rem; }
                  section { margin-top: 1.5rem; }
                  article { border-top: 1px solid #dddddd; padding: 1rem 0 0; }
                  img { max-width: 100%; height: auto; }
                  .caution { color: #a00000; }
                </style>
              </head>
              <body>
                <main>
                  <h1>経費申請登録</h1>
                  <section>
                    <h2>学習目標</h2>
                    <p>経費申請を登録できるようになる</p>
                  </section>
                  <section>
                    <h2>対象者</h2>
                    <p>新入社員</p>
                  </section>
                  <section>
                    <h2>事前準備</h2>
                    <ul>
                      <li>PC の基本操作</li>
                      <li>社内ネットワークへの接続</li>
                    </ul>
                  </section>
                  <section>
                    <h2>操作手順</h2>
                    <article>
                      <h3>1. 「新規申請」をクリックします</h3>
                      <p>新しい申請を作成します。</p>
                      <p><img src="../screenshots/edited/step-001.png" alt="「新規申請」をクリックします"></p>
                      <p class="caution"><strong>注意:</strong> 入力漏れに注意します。</p>
                      <p><strong>完了条件:</strong> 申請入力画面が表示されます。</p>
                    </article>
                  </section>
                  <section>
                    <h2>完了確認</h2>
                    <p>以上の手順で操作が完了します。</p>
                  </section>
                </main>
              </body>
            </html>
            """ + "\n",
            result.Content);
    }

    [Fact]
    public void Head_ContainsLangCharsetViewportAndTitle()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")]));

        Assert.StartsWith("<!doctype html>\n<html lang=\"ja\">\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("<meta charset=\"utf-8\">", result.Content, StringComparison.Ordinal);
        Assert.Contains(
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">",
            result.Content,
            StringComparison.Ordinal);
        Assert.Contains("<title>経費申請登録</title>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void Output_ContainsNoExternalResourcesOrScripts()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")]));

        Assert.DoesNotContain("<script", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<link", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http://", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data:", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RequiredSections_AreAlwaysWritten()
    {
        var result = HtmlManualWriter.Write(new ManualDocument { Title = Title, Steps = [] });

        Assert.Contains($"<h1>{Title}</h1>", result.Content, StringComparison.Ordinal);
        Assert.Contains("<h2>操作手順</h2>", result.Content, StringComparison.Ordinal);
        Assert.Contains("<h2>完了確認</h2>", result.Content, StringComparison.Ordinal);
        Assert.Contains("<p>以上の手順で操作が完了します。</p>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<article>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionOrder_IsFixed()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")]));

        var indices = new[]
        {
            result.Content.IndexOf($"<h1>{Title}</h1>", StringComparison.Ordinal),
            result.Content.IndexOf("<h2>学習目標</h2>", StringComparison.Ordinal),
            result.Content.IndexOf("<h2>対象者</h2>", StringComparison.Ordinal),
            result.Content.IndexOf("<h2>事前準備</h2>", StringComparison.Ordinal),
            result.Content.IndexOf("<h2>操作手順</h2>", StringComparison.Ordinal),
            result.Content.IndexOf("<h3>1. 手順1</h3>", StringComparison.Ordinal),
            result.Content.IndexOf("<h2>完了確認</h2>", StringComparison.Ordinal),
        };

        Assert.All(indices, index => Assert.True(index >= 0));
        Assert.Equal(indices.OrderBy(index => index), indices);
    }

    // --- Optional の省略（Markdown と同一条件） ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankObjective_OmitsSection(string? objective)
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")], objective: objective));

        Assert.DoesNotContain("<h2>学習目標</h2>", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTargetAudience_OmitsSection(string? audience)
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")], audience: audience));

        Assert.DoesNotContain("<h2>対象者</h2>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPrerequisites_OmitsSection()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")], prerequisites: []));

        Assert.DoesNotContain("<h2>事前準備</h2>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<ul>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AllBlankPrerequisites_OmitsSection()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")], prerequisites: ["", "   ", null!]));

        Assert.DoesNotContain("<h2>事前準備</h2>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankPrerequisites_AreNotOutput()
    {
        var result = HtmlManualWriter.Write(
            Document([StepItem(1, "手順1")], prerequisites: ["PC の基本操作", "", "   ", "社内ネットワークへの接続"]));

        Assert.Contains("<h2>事前準備</h2>", result.Content, StringComparison.Ordinal);
        Assert.Equal(
            ["<li>PC の基本操作</li>", "<li>社内ネットワークへの接続</li>"],
            result.Content.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("<li>", StringComparison.Ordinal)));
    }

    [Fact]
    public void Prerequisites_AreRenderedAsUnorderedList()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1")]));

        Assert.Contains("<ul>\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("<li>PC の基本操作</li>\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("</ul>\n", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalSections_OmittedInBothWriters()
    {
        var document = Document([StepItem(1, "手順1")], objective: null, audience: null, prerequisites: []);

        var markdown = MarkdownManualWriter.Write(document).Content;
        var html = HtmlManualWriter.Write(document).Content;

        Assert.DoesNotContain("学習目標", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("学習目標", html, StringComparison.Ordinal);
        Assert.DoesNotContain("対象者", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("対象者", html, StringComparison.Ordinal);
        Assert.DoesNotContain("事前準備", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("事前準備", html, StringComparison.Ordinal);
    }

    // --- Step の省略項目 ---

    [Fact]
    public void BlankStepOptionalFields_AreOmitted()
    {
        var result = HtmlManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "   ", caution: null, expectedResult: "")]));

        Assert.Contains("<h3>1. 手順1</h3>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>注意:</strong>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<strong>完了条件:</strong>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void CautionAndExpectedResult_DistinguishLabelAndBody()
    {
        var result = HtmlManualWriter.Write(Document(
            [StepItem(1, "手順1", caution: "注意です。", expectedResult: "結果です。")]));

        Assert.Contains("<p class=\"caution\"><strong>注意:</strong> 注意です。</p>", result.Content, StringComparison.Ordinal);
        Assert.Contains("<p><strong>完了条件:</strong> 結果です。</p>", result.Content, StringComparison.Ordinal);
    }

    // --- 複数 Step / 順序維持 ---

    [Fact]
    public void MultipleSteps_AreWrittenInInputOrder()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1"), StepItem(2, "手順2"), StepItem(3, "手順3")]));

        Assert.Equal(
            ["<h3>1. 手順1</h3>", "<h3>2. 手順2</h3>", "<h3>3. 手順3</h3>"],
            result.Content.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("<h3>", StringComparison.Ordinal)));
        Assert.Equal(3, result.Content.Split("<article>").Length - 1);
    }

    [Fact]
    public void StepOrderIsNotRenumbered()
    {
        // 並べ替え・再採番はしない（Order の妥当性は ManualDocumentBuilder / ProjectValidator の責務）。
        var result = HtmlManualWriter.Write(Document([StepItem(5, "手順A"), StepItem(1, "手順B")]));

        Assert.Equal(
            ["<h3>5. 手順A</h3>", "<h3>1. 手順B</h3>"],
            result.Content.Split('\n').Select(line => line.Trim()).Where(line => line.StartsWith("<h3>", StringComparison.Ordinal)));
    }

    // --- ScreenshotPath ---

    [Fact]
    public void Screenshot_BecomesImgWithRelativeSrc()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/step-001.png")]));

        Assert.Empty(result.Errors);
        Assert.Contains(
            "<p><img src=\"../screenshots/edited/step-001.png\" alt=\"手順1\"></p>",
            result.Content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankScreenshotPath_OutputsNoImg(string? screenshotPath)
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]));

        Assert.DoesNotContain("<img", result.Content, StringComparison.Ordinal);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ScreenshotPathWithLinkBreakingCharacters_IsEncodedInSrc()
    {
        var result = HtmlManualWriter.Write(
            Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/my step (1).png")]));

        Assert.Empty(result.Errors);
        Assert.Contains("src=\"../screenshots/edited/my%20step%20%281%29.png\"", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenshotPathWithJapaneseName_IsKeptReadable()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/申請画面.png")]));

        Assert.Empty(result.Errors);
        Assert.Contains("src=\"../screenshots/edited/申請画面.png\"", result.Content, StringComparison.Ordinal);
    }

    // --- escape ---

    [Fact]
    public void Japanese_IsPreservedReadable()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "「新規申請」をクリックします", description: "日本語の説明です。")]));

        Assert.Contains("<h1>経費申請登録</h1>", result.Content, StringComparison.Ordinal);
        Assert.Contains("<h3>1. 「新規申請」をクリックします</h3>", result.Content, StringComparison.Ordinal);
        Assert.Contains("<p>日本語の説明です。</p>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("&#x", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlTextEscape_EncodesMetacharacters()
    {
        var result = HtmlManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "<b>太字</b> & \"引用\" 'アポストロフィ'")]));

        Assert.Contains("<p>&lt;b&gt;太字&lt;/b&gt; &amp; &quot;引用&quot; &#x27;アポストロフィ&#x27;</p>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlAttributeEscape_EncodesQuotesInAlt()
    {
        var result = HtmlManualWriter.Write(Document(
            [StepItem(1, "タイトル\" onerror=\"alert(1)", screenshotPath: "screenshots/edited/step-001.png")]));

        var line = result.Content.Split('\n').Single(l => l.Contains("<img ", StringComparison.Ordinal));

        // 属性値の " は &quot; になり、属性を閉じない（新たな属性 onerror を作らない）。
        Assert.Contains("alt=\"タイトル&quot; onerror=&quot;alert(1)\"", line, StringComparison.Ordinal);
        Assert.DoesNotContain("onerror=\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void ScriptAndEventHandlerInjection_IsNotExecutable()
    {
        var result = HtmlManualWriter.Write(Document(
            [
                StepItem(
                    1,
                    "手順<script>alert(1)</script>",
                    description: "<img src=x onerror=alert(1)>",
                    caution: "<iframe src=\"https://evil.example\"></iframe>",
                    expectedResult: "javascript:alert(1)"),
            ]));

        Assert.DoesNotContain("<script", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"javascript:", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", result.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", result.Content, StringComparison.Ordinal);
        Assert.Contains("&lt;iframe", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StyleInjectionInUserText_DoesNotReachTheStylesheet()
    {
        var result = HtmlManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "</style><script>alert(1)</script>")]));

        // style 要素の中身は固定 CSS のみ。ユーザー入力は style 内に現れない。
        var styleStart = result.Content.IndexOf("<style>", StringComparison.Ordinal);
        var styleEnd = result.Content.IndexOf("</style>", StringComparison.Ordinal);
        var styleBlock = result.Content[styleStart..styleEnd];

        Assert.Contains("body { font-family: sans-serif;", styleBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("script", styleBlock, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, result.Content.Split("</style>").Length - 1);
    }

    [Theory]
    [InlineData("手順1\n<h1>注入</h1>")]
    [InlineData("手順1\r\n<h1>注入</h1>")]
    [InlineData("手順1\r<h1>注入</h1>")]
    [InlineData("手順1\u2028<h1>注入</h1>")]
    public void LineBreakInjection_DoesNotCreateNewLines(string title)
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, title)]));

        Assert.Contains("<h3>1. 手順1 &lt;h1&gt;注入&lt;/h1&gt;</h3>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1>注入", result.Content, StringComparison.Ordinal);
        Assert.Equal(1, result.Content.Split("<h1>").Length - 1);
    }

    // --- 改行コードと末尾 ---

    [Fact]
    public void Output_UsesLfOnlyAndSingleTrailingNewline()
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1", description: "説明。")]));

        Assert.DoesNotContain("\r", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("</html>\n", result.Content, StringComparison.Ordinal);
        Assert.False(result.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }

    // --- atomic Error ---

    [Theory]
    [InlineData("C:\\temp\\step-001.png", "absolute")]
    [InlineData("\\\\server\\share\\step-001.png", "absolute")]
    [InlineData("/screenshots/step-001.png", "absolute")]
    [InlineData("screenshots\\edited\\step-001.png", "backslash")]
    [InlineData("../secret.png", "traversal")]
    [InlineData("screenshots/../secret.png", "traversal")]
    [InlineData("./screenshots/step-001.png", "traversal")]
    public void InvalidScreenshotPath_IsErrorWithNoContent(string screenshotPath, string expectedReason)
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]));

        Assert.True(result.HasErrors);
        Assert.Equal("", result.Content);
        var error = Assert.Single(result.Errors);
        Assert.Contains("Step 1", error, StringComparison.Ordinal);
        Assert.Contains("screenshotPath", error, StringComparison.Ordinal);
        Assert.Contains(expectedReason, error, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedValidAndInvalidSteps_ProduceNoPartialOutput()
    {
        var document = Document(
            [
                StepItem(1, "手順1", screenshotPath: "screenshots/edited/step-001.png"),
                StepItem(2, "手順2", screenshotPath: "C:\\temp\\step-002.png"),
                StepItem(3, "手順3", screenshotPath: "screenshots/edited/step-003.png"),
            ]);

        var result = HtmlManualWriter.Write(document);

        Assert.Equal("", result.Content);
        Assert.Single(result.Errors);
        Assert.DoesNotContain("手順1", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("<main>", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleInvalidSteps_CollectAllErrors()
    {
        var document = Document(
            [
                StepItem(1, "手順1", screenshotPath: "screenshots/edited/step-001.png"),
                StepItem(2, "手順2", screenshotPath: "C:\\temp\\step-002.png"),
                StepItem(3, "手順3", screenshotPath: "../secret.png"),
                StepItem(4, "手順4", screenshotPath: "screenshots\\edited\\step-004.png"),
            ]);

        var result = HtmlManualWriter.Write(document);

        Assert.Equal("", result.Content);
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, error => error.Contains("Step 2", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Step 3", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("Step 4", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("C:\\Users\\yamada\\pictures\\step-001.png", "yamada")]
    [InlineData("\\\\fileserver\\share01\\screenshots\\step-001.png", "fileserver")]
    [InlineData("/home/localuser/screenshots/step-001.png", "localuser")]
    public void ErrorMessages_DoNotLeakThePath(string screenshotPath, string sensitiveToken)
    {
        var result = HtmlManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]));

        var errors = string.Join("\n", result.Errors);

        Assert.True(result.HasErrors);
        Assert.DoesNotContain(screenshotPath, errors, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveToken, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("home", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("share", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".png", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void NullDocument_IsReportedInsteadOfThrowing()
    {
        var result = HtmlManualWriter.Write(null);

        Assert.True(result.HasErrors);
        Assert.Equal("", result.Content);
        Assert.NotEmpty(result.Errors);
    }

    // --- Markdown との一致 ---

    [Theory]
    [InlineData("screenshots/edited/step-001.png")]
    [InlineData("screenshots/edited/my step (1).png")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("C:\\temp\\step-001.png")]
    [InlineData("\\\\server\\share\\step-001.png")]
    [InlineData("../secret.png")]
    [InlineData("screenshots\\edited\\step-001.png")]
    public void ScreenshotPathJudgement_MatchesMarkdown(string? screenshotPath)
    {
        var document = Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]);

        var markdown = MarkdownManualWriter.Write(document);
        var html = HtmlManualWriter.Write(document);

        Assert.Equal(markdown.HasErrors, html.HasErrors);
        Assert.Equal(markdown.Errors, html.Errors);
    }

    [Fact]
    public void RelativeReference_MatchesMarkdown()
    {
        var document = Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/my step (1).png")]);

        var markdown = MarkdownManualWriter.Write(document).Content;
        var html = HtmlManualWriter.Write(document).Content;

        Assert.Contains("](../screenshots/edited/my%20step%20%281%29.png)", markdown, StringComparison.Ordinal);
        Assert.Contains("src=\"../screenshots/edited/my%20step%20%281%29.png\"", html, StringComparison.Ordinal);
    }

    // --- 入力不変 ---

    [Fact]
    public void InputDocument_IsNotModified()
    {
        var document = Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/step-001.png", description: "説明\n改行")]);
        var before = JsonSerializer.Serialize(document, TrainingJson.Compact);

        _ = HtmlManualWriter.Write(document);

        Assert.Equal(before, JsonSerializer.Serialize(document, TrainingJson.Compact));
    }

    // --- テスト用の ManualDocument 組み立て（Writer は ManualDocument を直接受け取る） ---

    private static ManualDocument Document(
        ManualStep[] steps,
        string title = Title,
        string? objective = Objective,
        string? audience = Audience,
        IReadOnlyList<string>? prerequisites = null) => new()
        {
            Title = title,
            Objective = objective,
            TargetAudience = audience,
            Prerequisites = prerequisites ?? ["PC の基本操作", "社内ネットワークへの接続"],
            Steps = [.. steps],
        };

    private static ManualStep StepItem(
        int order,
        string title,
        string? screenshotPath = null,
        string? description = null,
        string? caution = null,
        string? expectedResult = null) => new()
        {
            Order = order,
            Title = title,
            Description = description,
            Caution = caution,
            ExpectedResult = expectedResult,
            ScreenshotPath = screenshotPath,
        };
}
