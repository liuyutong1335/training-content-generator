using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Core.Models;
using Xunit;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// ManualGenerator 経由でも禁止事項（契約 §11.1 / §15 / §16 / §18 / §19）が守られることの検証。
/// メタデータ非出力・injection 対策・path 非漏洩・入力不変を確認する。
/// </summary>
public class ManualSecurityTests
{
    // --- 出力してはいけない情報 ---

    [Fact]
    public void MetadataIsNotOutput()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));
        project.Revision = 777_777;
        project.CreatedAtUtc = new DateTimeOffset(2026, 8, 1, 1, 2, 3, TimeSpan.Zero);
        project.UpdatedAtUtc = new DateTimeOffset(2026, 8, 2, 4, 5, 6, TimeSpan.Zero);
        project.Recording!.MediaPath = "raw/recording-20260801.mp4";
        project.Recording.DurationMs = 987_654_321;
        project.Steps[0].StartMs = 111_222_333;
        project.Steps[0].EndMs = 444_555_666;
        project.Outputs.ManualMarkdown = new GeneratedArtifact
        {
            Path = "manual/previous-manual.md",
            GeneratedAtUtc = new DateTimeOffset(2026, 8, 3, 7, 8, 9, TimeSpan.Zero),
            SourceRevision = 1,
        };
        project.Outputs.TrainingVideo = new GeneratedArtifact { Path = "output/previous-video.mp4", SourceRevision = 1 };

        var result = ManualGenerator.Generate(project);
        var contents = Contents(result);
        var sourceEventId = project.Steps[0].SourceEventIds[0].ToString();

        Assert.Empty(result.Errors);
        Assert.NotEmpty(contents);

        // SourceEventIds / timestamp / Recording / Revision / 日時 / Outputs の既存 path を出力しない。
        Assert.DoesNotContain(sourceEventId, contents, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("111222333", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("444555666", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("recording-20260801.mp4", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("987654321", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("777777", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-01", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-08-02", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("previous-manual", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("previous-video", contents, StringComparison.Ordinal);
        Assert.DoesNotContain(project.Id.ToString(), contents, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StepTargetIsNotOutput()
    {
        // Target は Title と重複するため ManualDocument に含めない（契約 §12 / §19 の表示項目）。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Steps[0].Target = "パスワード";

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.DoesNotContain("パスワード", Contents(result), StringComparison.Ordinal);
    }

    [Fact]
    public void SensitiveStepValuesAreNotOutput()
    {
        // Review UI 追加 Step や手編集の project.json に sensitive な語が入っていても、
        // Step の表示項目以外は出力しない。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Steps[0].Target = "PASSWORD_FIELD";
        project.Steps[0].SourceEventIds = [Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")];

        var result = ManualGenerator.Generate(project);
        var contents = Contents(result);

        Assert.DoesNotContain("PASSWORD_FIELD", contents, StringComparison.Ordinal);
        Assert.DoesNotContain("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", contents, StringComparison.OrdinalIgnoreCase);
    }

    // --- Error 時の path 非漏洩 ---

    [Theory]
    [InlineData("../secret.png")]
    [InlineData("screenshots/../secret.png")]
    [InlineData("./screenshots/secret.png")]
    public void WriterPathError_DoesNotLeakThePath(string screenshotPath)
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", screenshotPath));

        var result = ManualGenerator.Generate(project);

        Assert.True(result.HasErrors);
        Assert.Null(result.Markdown);
        Assert.Null(result.Html);

        var errors = string.Join("\n", result.Errors);
        Assert.DoesNotContain(screenshotPath, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.png", errors, StringComparison.Ordinal);
        Assert.DoesNotContain(".png", errors, StringComparison.Ordinal);
        Assert.Contains("Step 1", errors, StringComparison.Ordinal);
        Assert.Contains("screenshotPath", errors, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("C:\\Users\\yamada\\secret.png", "yamada")]
    [InlineData("\\\\fileserver\\private\\secret.png", "fileserver")]
    [InlineData("/home/localuser/secret.png", "localuser")]
    public void AbsolutePathError_DoesNotLeakThePath(string screenshotPath, string sensitiveToken)
    {
        // 絶対パスは Manual 境界（ManualDocumentBuilder の screenshotPath 事前検証）で検出するため、
        // Shared ProjectValidator の path 全文を含む文言は結果に現れない。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", screenshotPath));
        var before = JsonSerializer.Serialize(project, TrainingJson.Compact);

        var result = ManualGenerator.Generate(project);
        var errors = string.Join("\n", result.Errors);

        Assert.True(result.HasErrors);
        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
        Assert.Contains("Step 1", errors, StringComparison.Ordinal);
        Assert.Contains("screenshotPath", errors, StringComparison.Ordinal);
        Assert.DoesNotContain(screenshotPath, errors, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveToken, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("home", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("share", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.png", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("[redacted]", errors, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(project, TrainingJson.Compact));
    }

    [Theory]
    [InlineData("C:\\Users\\yamada\\secret.mp4", "yamada")]
    [InlineData("\\\\fileserver\\private\\secret.mp4", "fileserver")]
    public void InvalidRecordingMediaPath_IsRedactedThroughGenerator(string mediaPath, string sensitiveToken)
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));
        project.Recording!.MediaPath = mediaPath;
        var before = JsonSerializer.Serialize(project, TrainingJson.Compact);

        var result = ManualGenerator.Generate(project);
        var errors = string.Join("\n", result.Errors);

        Assert.True(result.HasErrors);
        Assert.Null(result.Markdown);
        Assert.Null(result.Html);
        Assert.Contains("recording.mediaPath", errors, StringComparison.Ordinal);
        Assert.Contains("[redacted]", errors, StringComparison.Ordinal);
        Assert.DoesNotContain(mediaPath, errors, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveToken, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.mp4", errors, StringComparison.Ordinal);
        // 入力 Project の path 実値は変更しない。
        Assert.Equal(before, JsonSerializer.Serialize(project, TrainingJson.Compact));
        Assert.Equal(mediaPath, project.Recording.MediaPath);
    }

    // --- injection 対策（Generator 経由でも維持されること） ---

    [Fact]
    public void MarkdownInjection_IsNeutralizedThroughGenerator()
    {
        var step = ManualTestData.Step(1, "手順1");
        step.Description = "[click](javascript:alert(1))\n## 注入見出し";
        step.Caution = "<script>alert(2)</script>";

        var result = ManualGenerator.Generate(ManualTestData.Project(step));
        var markdown = result.Markdown!.Content;

        Assert.Empty(result.Errors);
        Assert.DoesNotContain("](javascript:", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n## 注入見出し", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("<script>", markdown, StringComparison.Ordinal);
        Assert.Contains(@"\[click\]", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlInjection_IsNeutralizedThroughGenerator()
    {
        var step = ManualTestData.Step(1, "手順<script>alert(1)</script>");
        step.Description = "<img src=x onerror=alert(2)>";
        step.Caution = "<iframe src=\"https://evil.example\"></iframe>";

        var result = ManualGenerator.Generate(ManualTestData.Project(step));
        var html = result.Html!.Content;

        Assert.Empty(result.Errors);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img src=x", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        // 注入 URL は属性値にならず、inert な本文テキストとしてのみ現れる。
        Assert.DoesNotContain("src=\"https://evil.example\"", html, StringComparison.Ordinal);
        Assert.Contains("&lt;iframe src=&quot;https://evil.example&quot;&gt;", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenshotSrcIsAlwaysRelativeEvenWithInjectedProjectValues()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));
        project.Outputs.TrainingVideo = new GeneratedArtifact { Path = "output/training_video.mp4", SourceRevision = 1 };

        var result = ManualGenerator.Generate(project);

        Assert.Contains("src=\"../screenshots/edited/step-001.png\"", result.Html!.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("src=\"http", result.Html.Content, StringComparison.Ordinal);
    }

    // --- 入力不変 ---

    [Fact]
    public void InputProject_IsNotModified()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "手順2"));
        project.Outputs.ManualHtml = new GeneratedArtifact { Path = "manual/manual.html", SourceRevision = 2 };
        var before = JsonSerializer.Serialize(project, TrainingJson.Compact);

        var result = ManualGenerator.Generate(project);

        Assert.Empty(result.Errors);
        Assert.Equal(before, JsonSerializer.Serialize(project, TrainingJson.Compact));
    }

    [Fact]
    public void InputProject_IsNotModifiedOnError()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "C:\\temp\\step-001.png"));
        var before = JsonSerializer.Serialize(project, TrainingJson.Compact);

        var result = ManualGenerator.Generate(project);

        Assert.True(result.HasErrors);
        Assert.Equal(before, JsonSerializer.Serialize(project, TrainingJson.Compact));
    }

    private static string Contents(ManualGenerationResult result) =>
        string.Join("\n", new[] { result.Markdown?.Content, result.Html?.Content }.Where(c => c is not null));
}
