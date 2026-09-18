using TrainingContent.Core.Models;
using TrainingContent.Core.Validation;

namespace TrainingContent.Manual;

/// <summary>
/// TrainingProject → 検証 → ManualDocument（docs/development-plan.md §19、phase0-contract.md §28）。
/// <list type="bullet">
///   <item>入力は TrainingProject のみ。Raw Event・events.jsonl を参照しない（§19）</item>
///   <item>schemaVersion は supported（= <see cref="ProjectValidator.SupportedSchemaVersion"/>）
///   と完全一致する場合のみ正常。自動補完・Migration・書き換えは行わない（§29）</item>
///   <item>screenshotPath は ProjectValidator より先に Manual 境界で検証する。
///   これは ProjectValidator の Error 文言が path 全文を含むため（情報漏洩の回避）。
///   Error には Step.Order・field 名・理由のみを含め、path 全文は含めない</item>
///   <item>ProjectValidator の Error が 1 件でもあれば Document を生成しない。
///   その Error に Step.ScreenshotPath の実値が含まれる場合は [redacted] へ置換する（defense-in-depth）</item>
///   <item>Steps が 0 件の場合も Error とし Document は null</item>
///   <item>入力順・Order を並べ替え・採番・修正しない（Order の妥当性は ProjectValidator の責務）</item>
///   <item>TrainingProject / TrainingStep を変更しない（Document は値のスナップショット）</item>
///   <item>例外ではなく結果オブジェクトの Errors で返す</item>
/// </list>
/// Markdown / HTML の生成、ファイル I/O、ProjectOutputs 更新は本クラスの責務外。
/// </summary>
public static class ManualDocumentBuilder
{
    /// <summary>Error 文言で実 path を伏せるときの置換文字列。</summary>
    private const string RedactedPlaceholder = "[redacted]";

    public static ManualDocumentBuildResult Build(TrainingProject? project)
    {
        if (project is null)
        {
            return Failure("TrainingProject が null のため ManualDocument を生成できません。");
        }

        // 契約 §29 は「schemaVersion == supported」を要求する。既存 ProjectValidator は
        // "> supported" のみを拒否するため、入力境界として不足分（不一致）をここで検証する。
        // Shared の ProjectValidator.cs は変更しない。
        if (project.SchemaVersion != ProjectValidator.SupportedSchemaVersion)
        {
            return Failure(
                $"schemaVersion {project.SchemaVersion} は未対応です（対応: {ProjectValidator.SupportedSchemaVersion} のみ）。"
                + "Migration・自動補完は行いません。");
        }

        // Steps が無い Project は Manual の対象外（§19 / §28）。
        // null 配列は空として扱い（契約 §30: null array は Normalize 可）、自動補完は行わない。
        var steps = project.Steps;
        if (steps is null || steps.Count == 0)
        {
            return Failure("Steps が 0 件のため ManualDocument を生成しません（契約 §19 / §28）。");
        }

        // screenshotPath は ProjectValidator より先に Manual 境界で検証する。
        // ProjectValidator の Error 文言は path 全文（ユーザー名・drive・UNC 等）を含むため、
        // 情報漏洩を避ける目的で、安全な Error（Order・field 名・理由のみ）を先に確定させる。
        var screenshotPathErrors = new List<string>();
        ManualScreenshotPath.CollectErrors(steps.Select(step => (step.Order, step.ScreenshotPath)), screenshotPathErrors);
        if (screenshotPathErrors.Count > 0)
        {
            return new ManualDocumentBuildResult { Errors = screenshotPathErrors };
        }

        // その他の契約違反の判定は ProjectValidator に委ねる（検証規則を二重定義しない）。
        var validationErrors = ProjectValidator.Validate(project);
        if (validationErrors.Count > 0)
        {
            // defense-in-depth: Validator の Error に path 実値（Recording.MediaPath / Steps[*].ScreenshotPath /
            // Outputs.*.Path）が含まれていた場合は伏せる。
            return new ManualDocumentBuildResult { Errors = RedactProjectPaths(validationErrors, project) };
        }

        return new ManualDocumentBuildResult
        {
            Document = new ManualDocument
            {
                Title = project.Title,
                Objective = Normalize(project.Objective),
                TargetAudience = Normalize(project.TargetAudience),

                // 入力とは別インスタンスのスナップショットを作る（入力の変更が Document に波及しない）。
                // null 配列は空として扱う（契約 §6.3 / §30）。
                Prerequisites = project.Prerequisites is null ? [] : [.. project.Prerequisites],
                Steps = [.. steps.Select(ToManualStep)],
            },
        };
    }

    private static ManualStep ToManualStep(TrainingStep step) => new()
    {
        Order = step.Order,
        Title = step.Title,
        Description = Normalize(step.Description),
        Caution = Normalize(step.Caution),
        ExpectedResult = Normalize(step.ExpectedResult),
        ScreenshotPath = Normalize(step.ScreenshotPath),
    };

    /// <summary>
    /// null / 空 / whitespace は null として保持する。値の trim や文章の補完は行わない
    /// （契約 §22: 不要な空文字を使わない）。
    /// </summary>
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// defense-in-depth: ProjectValidator 等が返した Error に Project の path 実値が含まれていた場合、
    /// その値（ユーザー名・drive・UNC server/share・ディレクトリ・ファイル名を含む）を
    /// <c>[redacted]</c> へ置換する。Shared の ProjectValidator は変更しない。
    /// </summary>
    private static List<string> RedactProjectPaths(IEnumerable<string> errors, TrainingProject project)
    {
        var paths = CollectProjectPaths(project);

        var redacted = new List<string>();
        foreach (var error in errors)
        {
            var message = error;
            foreach (var path in paths)
            {
                message = message.Replace(path, RedactedPlaceholder, StringComparison.Ordinal);
            }

            redacted.Add(message);
        }

        return redacted;
    }

    /// <summary>
    /// 伏せ字対象の path 実値を集める（契約 §7 / §16 / §18）。
    /// 重複は 1 件にまとめ、長い path から先に置換して部分一致による不完全な伏字を避ける。
    /// </summary>
    private static List<string> CollectProjectPaths(TrainingProject project)
    {
        var candidates = new List<string?>();

        if (project.Recording is { } recording)
        {
            candidates.Add(recording.MediaPath);
        }

        candidates.AddRange((project.Steps ?? []).Select(step => step.ScreenshotPath));

        if (project.Outputs is { } outputs)
        {
            candidates.Add(outputs.ManualMarkdown?.Path);
            candidates.Add(outputs.ManualHtml?.Path);
            candidates.Add(outputs.TrainingVideo?.Path);
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(path => path.Length)
            .ToList();
    }

    private static ManualDocumentBuildResult Failure(string error) => new() { Errors = [error] };
}
