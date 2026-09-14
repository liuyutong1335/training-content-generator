using System.Text.RegularExpressions;

namespace TrainingContent.Core.Validation;

/// <summary>
/// Project 保存前の検証（契約 §29 + Path Rule §18 + Required Text §22）。
/// エラーは違反 1 件につき 1 メッセージ。空なら保存可。
/// </summary>
public static partial class ProjectValidator
{
    public const int SupportedSchemaVersion = 1;

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DriveLetterRegex();

    public static IReadOnlyList<string> Validate(Models.TrainingProject project)
    {
        var errors = new List<string>();

        if (project.SchemaVersion > SupportedSchemaVersion)
        {
            errors.Add($"schemaVersion {project.SchemaVersion} は未対応です（対応: <= {SupportedSchemaVersion}）。Migration を行ってください。");
        }
        if (project.Id == Guid.Empty)
        {
            errors.Add("Project.Id が空です（GUID 必須）。");
        }
        if (string.IsNullOrWhiteSpace(project.Title))
        {
            errors.Add("Project.Title は null / blank 禁止です。");
        }
        if (project.Revision < 1)
        {
            errors.Add("Project.Revision は 1 以上であること。");
        }

        if (project.Recording is { } recording)
        {
            if (recording.DurationMs < 0)
            {
                errors.Add("Recording.DurationMs は 0 以上であること。");
            }
            ValidatePath(recording.MediaPath, "recording.mediaPath", errors);
        }

        var stepIds = new HashSet<Guid>();
        var orders = new HashSet<int>();
        foreach (var step in project.Steps)
        {
            if (step.Id == Guid.Empty || !stepIds.Add(step.Id))
            {
                errors.Add($"Step.Id が空または重複しています: {step.Id}");
            }
            if (!orders.Add(step.Order))
            {
                errors.Add($"Step.Order が重複しています: {step.Order}");
            }
            if (string.IsNullOrWhiteSpace(step.Title))
            {
                errors.Add($"Step.Title は null / blank 禁止です (Order={step.Order})。");
            }
            if (string.IsNullOrWhiteSpace(step.Action))
            {
                errors.Add($"Step.Action は null / blank 禁止です (Order={step.Order})。");
            }
            if (step.StartMs < 0)
            {
                errors.Add($"Step.StartMs は 0 以上であること (Order={step.Order})。");
            }
            if (project.Recording is { } rec && step.StartMs > rec.DurationMs)
            {
                errors.Add($"Step.StartMs が Recording.DurationMs を超えています (Order={step.Order})。");
            }
            if (step.EndMs is { } end && end < step.StartMs)
            {
                errors.Add($"Step.EndMs が StartMs 未満です (Order={step.Order})。");
            }
            if (step.SourceEventIds.Distinct().Count() != step.SourceEventIds.Count)
            {
                errors.Add($"Step の SourceEventIds が重複しています (Order={step.Order})。");
            }
            if (step.ScreenshotPath is not null)
            {
                ValidatePath(step.ScreenshotPath, $"step[{step.Order}].screenshotPath", errors);
            }
        }

        // Order は 1..N に正規化されていること（契約 §29）
        var sorted = project.Steps.Select(s => s.Order).OrderBy(o => o).ToList();
        if (sorted.Count > 0 && sorted.Zip(sorted.Skip(1), (a, b) => b - a).Any(d => d != 1))
        {
            errors.Add("Step.Order は 1..N の連番に正規化して保存すること。");
        }

        return errors;
    }

    /// <summary>Path Rule（§18）: 絶対パス禁止・区切りは / に統一。</summary>
    private static void ValidatePath(string path, string field, List<string> errors)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }
        if (DriveLetterRegex().IsMatch(path) || path.StartsWith('\\') || path.StartsWith("//"))
        {
            errors.Add($"{field} に絶対パスは保存できません（Project-relative にする）: {path}");
        }
        else if (path.Contains('\\'))
        {
            errors.Add($"{field} のパス区切りは / に統一すること: {path}");
        }
    }
}
