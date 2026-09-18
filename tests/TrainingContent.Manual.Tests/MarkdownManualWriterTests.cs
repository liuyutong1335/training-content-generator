using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Manual.Markdown;
using Xunit;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// MarkdownManualWriter（docs/development-plan.md §19）。
/// ManualDocument → Markdown 文字列。ファイル I/O と HTML は本クラスの責務外。
/// </summary>
public class MarkdownManualWriterTests
{
    private const string Title = "経費申請登録";
    private const string Objective = "経費申請を登録できるようになる";
    private const string Audience = "新入社員";

    // --- 正常系: 全セクションを規定順で出力 ---

    [Fact]
    public void FullDocument_MatchesExpectedMarkdown()
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

        var result = MarkdownManualWriter.Write(document);

        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
        Assert.Equal(
            """
            # 経費申請登録

            ## 学習目標
            経費申請を登録できるようになる

            ## 対象者
            新入社員

            ## 事前準備
            - PC の基本操作
            - 社内ネットワークへの接続

            ## 操作手順

            ### 1. 「新規申請」をクリックします
            新しい申請を作成します。

            ![「新規申請」をクリックします](../screenshots/edited/step-001.png)

            **注意:** 入力漏れに注意します。

            **完了条件:** 申請入力画面が表示されます。

            ## 完了確認
            以上の手順で操作が完了します。
            """ + "\n",
            result.Content);
    }

    [Fact]
    public void RequiredSections_AreAlwaysWritten()
    {
        // Title・操作手順・完了確認は必ず出力する（Steps が 0 件でも見出しは出す）。
        var document = new ManualDocument { Title = Title, Steps = [] };

        var result = MarkdownManualWriter.Write(document);

        Assert.StartsWith($"# {Title}\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("## 操作手順", result.Content, StringComparison.Ordinal);
        Assert.Contains("## 完了確認", result.Content, StringComparison.Ordinal);
        Assert.Contains("以上の手順で操作が完了します。", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("### ", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void SectionOrder_IsFixed()
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1")]));

        var indices = new[]
        {
            result.Content.IndexOf("# 経費申請登録", StringComparison.Ordinal),
            result.Content.IndexOf("## 学習目標", StringComparison.Ordinal),
            result.Content.IndexOf("## 対象者", StringComparison.Ordinal),
            result.Content.IndexOf("## 事前準備", StringComparison.Ordinal),
            result.Content.IndexOf("## 操作手順", StringComparison.Ordinal),
            result.Content.IndexOf("### 1. 手順1", StringComparison.Ordinal),
            result.Content.IndexOf("## 完了確認", StringComparison.Ordinal),
        };

        Assert.All(indices, index => Assert.True(index >= 0));
        Assert.Equal(indices.OrderBy(index => index), indices);
    }

    // --- Optional セクションの省略 ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankObjective_OmitsSection(string? objective)
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1")], objective: objective));

        Assert.DoesNotContain("## 学習目標", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(Objective, result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTargetAudience_OmitsSection(string? audience)
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1")], audience: audience));

        Assert.DoesNotContain("## 対象者", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPrerequisites_OmitsSection()
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1")], prerequisites: []));

        Assert.DoesNotContain("## 事前準備", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void AllBlankPrerequisites_OmitsSection()
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1")], prerequisites: ["", "   ", null!]));

        Assert.DoesNotContain("## 事前準備", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankPrerequisites_AreNotOutput()
    {
        var result = MarkdownManualWriter.Write(
            Document([StepItem(1, "手順1")], prerequisites: ["PC の基本操作", "", "   ", "社内ネットワークへの接続"]));

        Assert.Contains("## 事前準備", result.Content, StringComparison.Ordinal);
        Assert.Contains("- PC の基本操作\n", result.Content, StringComparison.Ordinal);
        Assert.Contains("- 社内ネットワークへの接続\n", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("- \n", result.Content, StringComparison.Ordinal);
    }

    // --- Step の Optional 項目 ---

    [Fact]
    public void BlankDescription_IsOmitted()
    {
        var result = MarkdownManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "   ", caution: "注意です。", expectedResult: "結果です。")]));

        Assert.Contains("### 1. 手順1\n\n**注意:** 注意です。", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankCaution_IsOmitted()
    {
        var result = MarkdownManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "説明です。", caution: null, expectedResult: "結果です。")]));

        Assert.DoesNotContain("**注意:**", result.Content, StringComparison.Ordinal);
        Assert.Contains("**完了条件:** 結果です。", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankExpectedResult_IsOmitted()
    {
        var result = MarkdownManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "説明です。", caution: "注意です。", expectedResult: "")]));

        Assert.DoesNotContain("**完了条件:**", result.Content, StringComparison.Ordinal);
        Assert.Contains("**注意:** 注意です。", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "   ", "")]
    public void AllBlankStepOptionalFields_AreOmitted(string? description, string? caution, string? expectedResult)
    {
        var result = MarkdownManualWriter.Write(Document(
            [StepItem(1, "手順1", description: description, caution: caution, expectedResult: expectedResult)]));

        Assert.Contains("### 1. 手順1\n", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("**注意:**", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("**完了条件:**", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("![", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void StepOptionalFields_AreWrittenWhenPresent()
    {
        var result = MarkdownManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "説明です。", caution: "注意です。", expectedResult: "結果です。")]));

        Assert.Contains("説明です。", result.Content, StringComparison.Ordinal);
        Assert.Contains("**注意:** 注意です。", result.Content, StringComparison.Ordinal);
        Assert.Contains("**完了条件:** 結果です。", result.Content, StringComparison.Ordinal);
    }

    // --- 複数 Step / 順序維持 ---

    [Fact]
    public void MultipleSteps_AreWrittenInInputOrder()
    {
        var document = Document([StepItem(1, "手順1"), StepItem(2, "手順2"), StepItem(3, "手順3")]);

        var result = MarkdownManualWriter.Write(document);

        Assert.Equal(
            ["### 1. 手順1", "### 2. 手順2", "### 3. 手順3"],
            result.Content.Split('\n').Where(line => line.StartsWith("### ", StringComparison.Ordinal)));
    }

    [Fact]
    public void StepOrderIsNotRenumbered()
    {
        // 並べ替え・再採番はしない（Order の妥当性は ManualDocumentBuilder / ProjectValidator の責務）。
        var document = Document([StepItem(5, "手順A"), StepItem(1, "手順B")]);

        var result = MarkdownManualWriter.Write(document);

        Assert.Equal(
            ["### 5. 手順A", "### 1. 手順B"],
            result.Content.Split('\n').Where(line => line.StartsWith("### ", StringComparison.Ordinal)));
    }

    // --- ScreenshotPath ---

    [Fact]
    public void ScreenshotPath_BecomesRelativeImageLink()
    {
        var document = Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/step-001.png")]);

        var result = MarkdownManualWriter.Write(document);

        Assert.Empty(result.Errors);
        Assert.Contains("![手順1](../screenshots/edited/step-001.png)", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankScreenshotPath_OutputsNoImage(string? screenshotPath)
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]));

        Assert.DoesNotContain("![", result.Content, StringComparison.Ordinal);
        Assert.Empty(result.Errors);
    }

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
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]));

        // atomic: Error がある場合は Markdown を一切生成しない。
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

        var result = MarkdownManualWriter.Write(document);

        Assert.Equal("", result.Content);
        Assert.Single(result.Errors);
        // 正常 Step の本文・見出し・画像も含め、部分 Markdown を返さない。
        Assert.DoesNotContain("手順1", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("手順3", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("# 経費申請登録", result.Content, StringComparison.Ordinal);
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

        var result = MarkdownManualWriter.Write(document);

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
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1", screenshotPath: screenshotPath)]));

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
    public void ScreenshotPathWithLinkBreakingCharacters_IsEncoded()
    {
        var document = Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/my step (1).png")]);

        var result = MarkdownManualWriter.Write(document);

        Assert.Empty(result.Errors);
        // ディレクトリ区切り / は維持し、link を壊す文字だけを出力時に percent-encode する。
        Assert.Contains("](../screenshots/edited/my%20step%20%281%29.png)", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenshotPathWithJapaneseName_IsKeptReadable()
    {
        var document = Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/申請画面.png")]);

        var result = MarkdownManualWriter.Write(document);

        Assert.Empty(result.Errors);
        Assert.Contains("](../screenshots/edited/申請画面.png)", result.Content, StringComparison.Ordinal);
    }

    // --- 改行コードと末尾 ---

    [Fact]
    public void Output_UsesLfOnlyAndSingleTrailingNewline()
    {
        var result = MarkdownManualWriter.Write(
            Document([StepItem(1, "手順1", screenshotPath: "screenshots/edited/step-001.png", description: "説明。")]));

        Assert.DoesNotContain("\r", result.Content, StringComparison.Ordinal);
        Assert.EndsWith("。\n", result.Content, StringComparison.Ordinal);
        Assert.False(result.Content.EndsWith("\n\n", StringComparison.Ordinal));
    }

    // --- Markdown / HTML への注入対策 ---

    [Fact]
    public void MarkdownSymbolsInUserText_AreEscaped()
    {
        var document = Document([StepItem(1, "*太字* _斜体_ `code` #タグ | 表")], title: "タイトル *強調*");

        var result = MarkdownManualWriter.Write(document);

        Assert.Contains(@"# タイトル \*強調\*", result.Content, StringComparison.Ordinal);
        Assert.Contains(@"### 1. \*太字\* \_斜体\_ \`code\` \#タグ \| 表", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("*太字*", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("_斜体_", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void RawHtmlInUserText_IsNotLeftAsActiveHtml()
    {
        var result = MarkdownManualWriter.Write(
            Document([StepItem(1, "手順1", description: "<script>alert(1)</script>")]));

        Assert.DoesNotContain("<script>", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("</script>", result.Content, StringComparison.Ordinal);
        Assert.Contains(@"\<script\>", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("手順1\n## 注入見出し")]
    [InlineData("手順1\r\n## 注入見出し")]
    [InlineData("手順1\r## 注入見出し")]
    [InlineData("手順1\u2028## 注入見出し")]
    public void LineBreakInjection_DoesNotCreateNewBlocks(string title)
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, title)]));

        Assert.DoesNotContain("\n## 注入見出し", result.Content, StringComparison.Ordinal);
        Assert.All(
            result.Content.Split('\n').Where(line => line.StartsWith("## ", StringComparison.Ordinal)),
            line => Assert.DoesNotContain("注入", line, StringComparison.Ordinal));
    }

    [Fact]
    public void ListInjectionInPrerequisite_DoesNotCreateNewListItem()
    {
        var result = MarkdownManualWriter.Write(Document([StepItem(1, "手順1")], prerequisites: ["- 偽の項目"]));

        Assert.Equal(
            ["- \\- 偽の項目"],
            result.Content.Split('\n').Where(line => line.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public void LinkAndImageInjectionInUserText_DoesNotCreateLink()
    {
        var result = MarkdownManualWriter.Write(Document(
            [StepItem(1, "手順1", description: "[click](javascript:alert(1)) ![img](javascript:alert(2))")]));

        Assert.DoesNotContain("](javascript:", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("![img]", result.Content, StringComparison.Ordinal);
        Assert.Contains(@"\[click\]", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public void ImageAltText_IsEscaped()
    {
        var result = MarkdownManualWriter.Write(
            Document([StepItem(1, "*手順*", screenshotPath: "screenshots/edited/step-001.png")]));

        Assert.Contains(@"![\*手順\*](../screenshots/edited/step-001.png)", result.Content, StringComparison.Ordinal);
    }

    // --- 入力不変 / 不正入力 ---

    [Fact]
    public void InputDocument_IsNotModified()
    {
        var document = Document(
            [StepItem(1, "*手順*", screenshotPath: "screenshots/edited/step-001.png", description: "説明\n改行")]);
        var before = JsonSerializer.Serialize(document, TrainingJson.Compact);

        _ = MarkdownManualWriter.Write(document);

        Assert.Equal(before, JsonSerializer.Serialize(document, TrainingJson.Compact));
    }

    [Fact]
    public void NullDocument_IsReportedInsteadOfThrowing()
    {
        var result = MarkdownManualWriter.Write(null);

        Assert.True(result.HasErrors);
        Assert.NotEmpty(result.Errors);
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
