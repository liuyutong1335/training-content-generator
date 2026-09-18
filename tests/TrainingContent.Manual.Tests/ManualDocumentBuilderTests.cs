using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Core.Models;
using TrainingContent.Core.Validation;
using Xunit;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// ManualDocumentBuilder（docs/development-plan.md §19、phase0-contract.md §6 / §12 / §16 / §28）。
/// TrainingProject → 検証 → ManualDocument。Markdown / HTML の生成は本クラスの責務外。
/// </summary>
public class ManualDocumentBuilderTests
{
    // --- 正常系 ---

    [Fact]
    public void ValidProject_BuildsDocument()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "「新規申請」をクリックします", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "「社員番号」に入力します"));

        var result = ManualDocumentBuilder.Build(project);

        Assert.Empty(result.Errors);
        Assert.False(result.HasErrors);
        var document = Assert.IsType<ManualDocument>(result.Document);
        Assert.Equal(2, document.Steps.Count);
    }

    [Fact]
    public void ProjectFields_AreCarriedToDocument()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Equal(ManualTestData.DefaultTitle, document.Title);
        Assert.Equal(ManualTestData.DefaultObjective, document.Objective);
        Assert.Equal(ManualTestData.DefaultTargetAudience, document.TargetAudience);
        Assert.Equal(["PC の基本操作", "社内ネットワークへの接続"], document.Prerequisites);
    }

    [Fact]
    public void StepFields_AreCarriedToDocument()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "「新規申請」をクリックします", "screenshots/edited/step-001.png"));
        var source = project.Steps[0];

        var step = Assert.Single(ManualDocumentBuilder.Build(project).Document!.Steps);

        Assert.Equal(source.Order, step.Order);
        Assert.Equal(source.Title, step.Title);
        Assert.Equal(source.Description, step.Description);
        Assert.Equal(source.Caution, step.Caution);
        Assert.Equal(source.ExpectedResult, step.ExpectedResult);
        Assert.Equal("screenshots/edited/step-001.png", step.ScreenshotPath);
    }

    [Fact]
    public void StepWithoutScreenshot_HasNullScreenshotPath()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));

        var step = Assert.Single(ManualDocumentBuilder.Build(project).Document!.Steps);

        Assert.Null(step.ScreenshotPath);
    }

    // --- Optional 項目は補完しない ---

    [Fact]
    public void NullOptionalFields_StayNull()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Objective = null;
        project.TargetAudience = null;
        project.Steps[0].Description = null;
        project.Steps[0].Caution = null;
        project.Steps[0].ExpectedResult = null;

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Null(document.Objective);
        Assert.Null(document.TargetAudience);
        Assert.Null(document.Steps[0].Description);
        Assert.Null(document.Steps[0].Caution);
        Assert.Null(document.Steps[0].ExpectedResult);
    }

    [Fact]
    public void BlankOptionalFields_AreNormalizedToNull_WithoutFillingText()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Objective = "";
        project.TargetAudience = "   ";
        project.Steps[0].Description = "";
        project.Steps[0].Caution = "   ";
        project.Steps[0].ExpectedResult = "";

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Null(document.Objective);
        Assert.Null(document.TargetAudience);
        Assert.Null(document.Steps[0].Description);
        Assert.Null(document.Steps[0].Caution);
        Assert.Null(document.Steps[0].ExpectedResult);
    }

    [Fact]
    public void EmptyPrerequisites_StaysEmpty()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Prerequisites = [];

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Empty(document.Prerequisites);
    }

    [Fact]
    public void NonBlankOptionalFields_AreNotModified()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Objective = "  前後に空白がある目標  ";

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Equal("  前後に空白がある目標  ", document.Objective);
    }

    // --- 入力順・Order を維持（並べ替え・採番・修正をしない） ---

    [Fact]
    public void StepOrderAndInputOrder_ArePreserved()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1"),
            ManualTestData.Step(2, "手順2"),
            ManualTestData.Step(3, "手順3"));

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Equal([1, 2, 3], document.Steps.Select(s => s.Order));
        Assert.Equal(["手順1", "手順2", "手順3"], document.Steps.Select(s => s.Title));
    }

    [Fact]
    public void NonSequentialOrder_IsNotRenumbered()
    {
        // Order の妥当性は ProjectValidator の責務。Builder は自動採番・自動修正をしない。
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1"),
            ManualTestData.Step(5, "手順2"));

        var result = ManualDocumentBuilder.Build(project);

        if (result.Document is { } document)
        {
            Assert.Equal([1, 5], document.Steps.Select(s => s.Order));
        }
        else
        {
            // ProjectValidator が弾く場合も、Builder が勝手に直さないことを確認する。
            Assert.NotEmpty(result.Errors);
        }
    }

    // --- 境界: Error 時は Document を生成しない ---

    [Fact]
    public void ValidatorError_ProducesNoDocument()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Title = "";

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Title", StringComparison.Ordinal));
    }

    // --- schemaVersion は supported との完全一致のみ正常（契約 §29） ---

    [Fact]
    public void SchemaVersionZero_IsError()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.SchemaVersion = 0;

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("schemaVersion 0", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsupportedSchemaVersion_IsError()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.SchemaVersion = 2;

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("schemaVersion 2", StringComparison.Ordinal));
    }

    [Fact]
    public void SupportedSchemaVersion_IsAccepted()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.SchemaVersion = ProjectValidator.SupportedSchemaVersion;

        var result = ManualDocumentBuilder.Build(project);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Document);
    }

    [Fact]
    public void SchemaVersionIsNotAutoCorrected()
    {
        // 自動補完・書き換えをしない（入力の schemaVersion は変更されない）。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.SchemaVersion = 2;

        _ = ManualDocumentBuilder.Build(project);

        Assert.Equal(2, project.SchemaVersion);
    }

    [Fact]
    public void DuplicateStepOrder_ProducesNoDocument()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1"),
            ManualTestData.Step(1, "手順2"));

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.Contains(result.Errors, error => error.Contains("Order", StringComparison.Ordinal));
    }

    [Fact]
    public void AbsoluteScreenshotPath_ProducesNoDocument()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "C:\\temp\\step-001.png"));

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.NotEmpty(result.Errors);
    }

    // --- screenshotPath は Manual 境界で先に検証し、Error に path 全文を含めない ---

    [Theory]
    [InlineData("C:\\Users\\yamada\\secret.png", "yamada", "absolute")]
    [InlineData("\\\\fileserver\\private\\secret.png", "fileserver", "absolute")]
    [InlineData("/home/localuser/secret.png", "localuser", "absolute")]
    [InlineData("screenshots\\private\\secret.png", "screenshots\\private", "backslash")]
    [InlineData("../private/secret.png", "private", "traversal")]
    public void InvalidScreenshotPath_IsErrorWithoutLeakingThePath(
        string screenshotPath,
        string sensitiveToken,
        string expectedReason)
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", screenshotPath));

        var result = ManualDocumentBuilder.Build(project);
        var errors = string.Join("\n", result.Errors);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        // Error には Step.Order・field 名・理由のみを含める。
        Assert.Contains("Step 1", errors, StringComparison.Ordinal);
        Assert.Contains("screenshotPath", errors, StringComparison.Ordinal);
        Assert.Contains(expectedReason, errors, StringComparison.Ordinal);
        // 元 path・ユーザー名・drive・server / share・ファイル名を含めない。
        Assert.DoesNotContain(screenshotPath, errors, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveToken, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("localuser", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("share", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.png", errors, StringComparison.Ordinal);
        Assert.DoesNotContain(".png", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleInvalidScreenshotPaths_CollectAllErrors()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "手順2", "C:\\Users\\yamada\\secret.png"),
            ManualTestData.Step(3, "手順3", "../private/secret.png"));

        var result = ManualDocumentBuilder.Build(project);
        var errors = string.Join("\n", result.Errors);

        Assert.Null(result.Document);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("Step 2", errors, StringComparison.Ordinal);
        Assert.Contains("Step 3", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("yamada", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("private", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.png", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void ScreenshotPathError_StopsBeforeProjectValidator()
    {
        // screenshotPath の Error がある場合は ProjectValidator へ進まず、
        // path を含まない安全な Error だけを返す（ProjectValidator の文言は path 全文を含むため）。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "C:\\temp\\secret.png"));
        project.Title = "";

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.Contains(result.Errors, error => error.Contains("screenshotPath", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("Title", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Errors, error => error.Contains("secret.png", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidScreenshotPath_HasNoErrorsAndIsCarried()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));

        var result = ManualDocumentBuilder.Build(project);

        Assert.Empty(result.Errors);
        Assert.Equal("screenshots/edited/step-001.png", result.Document!.Steps[0].ScreenshotPath);
    }

    [Fact]
    public void ZeroSteps_IsError()
    {
        var project = ManualTestData.Project();

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        Assert.Contains(result.Errors, error => error.Contains("Steps", StringComparison.Ordinal));
    }

    [Fact]
    public void NullProject_IsErrorInsteadOfThrowing()
    {
        var result = ManualDocumentBuilder.Build(null);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void NullSteps_IsErrorInsteadOfThrowing()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Steps = null!;

        var result = ManualDocumentBuilder.Build(project);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
    }

    // --- ProjectValidator が返した path 実値の redaction（defense-in-depth） ---

    [Theory]
    [InlineData("C:\\Users\\yamada\\secret.mp4", "yamada")]
    [InlineData("\\\\fileserver\\private\\secret.mp4", "fileserver")]
    public void InvalidRecordingMediaPath_IsRedactedInValidatorErrors(string mediaPath, string sensitiveToken)
    {
        // Recording.MediaPath は Shared の ProjectValidator が検証し、その Error 文言は path 全文を含む。
        // Manual 境界で [redacted] へ置換されることを、実際の Validator Error を通して確認する。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));
        project.Recording!.MediaPath = mediaPath;

        var result = ManualDocumentBuilder.Build(project);
        var errors = string.Join("\n", result.Errors);

        Assert.Null(result.Document);
        Assert.True(result.HasErrors);
        // path 以外の理由・field 名は保持される。
        Assert.Contains("recording.mediaPath", errors, StringComparison.Ordinal);
        Assert.Contains("絶対パス", errors, StringComparison.Ordinal);
        // path 実値は伏せられる。
        Assert.Contains("[redacted]", errors, StringComparison.Ordinal);
        Assert.DoesNotContain(mediaPath, errors, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveToken, errors, StringComparison.Ordinal);
        Assert.DoesNotContain("Users", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("share", errors, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret.mp4", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("\\\\", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidRecordingMediaPath_BackslashSeparator_IsRedacted()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Recording!.MediaPath = "raw\\secret.mp4";

        var result = ManualDocumentBuilder.Build(project);
        var errors = string.Join("\n", result.Errors);

        Assert.Null(result.Document);
        Assert.Contains("recording.mediaPath", errors, StringComparison.Ordinal);
        Assert.Contains("パス区切り", errors, StringComparison.Ordinal);
        Assert.Contains("[redacted]", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("raw\\secret.mp4", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.mp4", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionPrefersLongestPath_NoPartialLeak()
    {
        // ScreenshotPath "raw" は Error 文言中の "raw\secret.mp4" の接頭辞になる。
        // 長い path から置換しないと "[redacted]\secret.mp4" となりファイル名が残る。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "raw"));
        project.Recording!.MediaPath = "raw\\secret.mp4";

        var result = ManualDocumentBuilder.Build(project);
        var error = Assert.Single(result.Errors, item => item.Contains("recording.mediaPath", StringComparison.Ordinal));

        Assert.Null(result.Document);
        Assert.EndsWith("[redacted]", error, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.mp4", error, StringComparison.Ordinal);
        Assert.DoesNotContain("\\secret", error, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicatePaths_AreRedactedWithoutLeak()
    {
        // 同一 path が複数箇所（2 Step の ScreenshotPath）にあり、かつ Validator Error が
        // 接頭辞を共有する path を含む場合でも漏洩しない。
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1", "raw"),
            ManualTestData.Step(2, "手順2", "raw"));
        project.Recording!.MediaPath = "raw\\secret.mp4";

        var result = ManualDocumentBuilder.Build(project);
        var errors = string.Join("\n", result.Errors);

        Assert.Null(result.Document);
        Assert.Contains("[redacted]", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.mp4", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void OutputsPaths_DoNotAppearInErrors()
    {
        // ProjectValidator は Outputs を検証しないため Error には現れない（下のテストで確認）。
        // 将来 Validator が Outputs を検証しても漏れないよう redaction 対象には含めている。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Outputs.ManualMarkdown = new GeneratedArtifact { Path = "C:\\Users\\yamada\\old.md", SourceRevision = 1 };
        project.Outputs.ManualHtml = new GeneratedArtifact { Path = "\\\\fileserver\\share\\old.html", SourceRevision = 1 };
        project.Outputs.TrainingVideo = new GeneratedArtifact { Path = "C:\\Users\\yamada\\old.mp4", SourceRevision = 1 };
        project.Title = "";

        var result = ManualDocumentBuilder.Build(project);
        var errors = string.Join("\n", result.Errors);

        Assert.Null(result.Document);
        Assert.Contains(result.Errors, error => error.Contains("Title", StringComparison.Ordinal));
        Assert.DoesNotContain("yamada", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("fileserver", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("old.md", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("old.html", errors, StringComparison.Ordinal);
        Assert.DoesNotContain("old.mp4", errors, StringComparison.Ordinal);
    }

    [Fact]
    public void WeirdOutputsPaths_DoNotProduceValidatorErrors()
    {
        // 実挙動の確認: ProjectValidator は Outputs を検証しないため、不正な Outputs path でも
        // Manual 生成は成功し Error は増えない。
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Outputs.ManualMarkdown = new GeneratedArtifact { Path = "C:\\Users\\yamada\\old.md", SourceRevision = 1 };
        project.Outputs.TrainingVideo = new GeneratedArtifact { Path = "\\\\fileserver\\share\\old.mp4", SourceRevision = 1 };

        var result = ManualDocumentBuilder.Build(project);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Document);
    }

    [Fact]
    public void ValidProject_ProducesNoRedactionAndNoExtraErrors()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));

        var result = ManualDocumentBuilder.Build(project);

        Assert.Empty(result.Errors);
        Assert.NotNull(result.Document);
        Assert.Equal("raw/recording.mp4", project.Recording!.MediaPath);
    }

    // --- 入力を変更しない ---

    [Fact]
    public void InputProject_IsNotModified()
    {
        var project = ManualTestData.Project(
            ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"),
            ManualTestData.Step(2, "手順2"));
        var before = JsonSerializer.Serialize(project, TrainingJson.Compact);

        _ = ManualDocumentBuilder.Build(project);

        Assert.Equal(before, JsonSerializer.Serialize(project, TrainingJson.Compact));
    }

    [Fact]
    public void InputOutputsAndRevision_AreNotModified()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        var revision = project.Revision;
        var manualMarkdown = project.Outputs.ManualMarkdown;

        var document = ManualDocumentBuilder.Build(project).Document!;

        Assert.Equal(revision, project.Revision);
        Assert.Null(project.Outputs.ManualMarkdown);
        Assert.Same(manualMarkdown, project.Outputs.ManualMarkdown);
        Assert.NotNull(document);
    }

    [Fact]
    public void Document_IsASnapshot_NotAnAliasOfInput()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1", "screenshots/edited/step-001.png"));

        var document = ManualDocumentBuilder.Build(project).Document!;

        // 生成後に元 Project を変更しても Document は影響を受けない。
        project.Title = "変更後のタイトル";
        project.Prerequisites.Add("後から追加した前提");
        project.Steps[0].Title = "変更後の手順";
        project.Steps[0].Description = "変更後の説明";
        project.Steps.Clear();

        Assert.Equal(ManualTestData.DefaultTitle, document.Title);
        Assert.Equal(["PC の基本操作", "社内ネットワークへの接続"], document.Prerequisites);
        Assert.Equal("手順1", document.Steps[0].Title);
        Assert.Equal("手順1 の説明。", document.Steps[0].Description);
    }

    // --- 出力に含めない項目（契約 §12 / §16 / §28） ---

    [Fact]
    public void ManualDocument_HasExactlyTheContractedProperties()
    {
        // プロパティ順序ではなく集合として契約項目を固定する（順序は契約しない）。
        Assert.Equal(
            ["Objective", "Prerequisites", "Steps", "TargetAudience", "Title"],
            typeof(ManualDocument).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.Equal(
            ["Caution", "Description", "ExpectedResult", "Order", "ScreenshotPath", "Title"],
            typeof(ManualStep).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ManualDocument_HasNoExcludedProperties()
    {
        var names = typeof(ManualDocument).GetProperties().Select(p => p.Name)
            .Concat(typeof(ManualStep).GetProperties().Select(p => p.Name))
            .ToList();

        // Target は Title と重複するため含めない。SourceEventIds / timestamp / Recording /
        // Outputs / Revision / 日時は Manual の表示対象外。
        string[] excludedExactly =
        [
            "Target", "SourceEventIds", "StartMs", "EndMs", "Recording", "ProjectOutputs", "Outputs",
            "Revision", "CreatedAtUtc", "UpdatedAtUtc", "Id", "SchemaVersion", "Action", "ScreenshotPaths",
        ];
        Assert.DoesNotContain(names, name => excludedExactly.Contains(name, StringComparer.Ordinal));

        // sensitive な内容・実入力文字を保持しうる名前も存在しない。
        string[] excludedFragments = ["keycount", "keyCount", "password", "passwd", "secret", "credential", "rawtext", "input"];
        Assert.DoesNotContain(names, name => excludedFragments.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    // --- 入力の sensitive 情報を文書へ持ち込まない ---

    [Fact]
    public void SensitiveTargetNameInStep_IsNotCarriedToDocument()
    {
        var project = ManualTestData.Project(ManualTestData.Step(1, "手順1"));
        project.Steps[0].Target = "パスワード";

        var document = ManualDocumentBuilder.Build(project).Document!;
        var serialized = JsonSerializer.Serialize(document, TrainingJson.Compact);

        Assert.DoesNotContain("パスワード", serialized, StringComparison.Ordinal);
    }
}
