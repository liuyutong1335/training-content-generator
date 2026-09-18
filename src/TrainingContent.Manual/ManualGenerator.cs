using TrainingContent.Core.Models;
using TrainingContent.Manual.Html;
using TrainingContent.Manual.Markdown;

namespace TrainingContent.Manual;

/// <summary>
/// TrainingProject → ManualDocumentBuilder → Markdown / HTML Writer → <see cref="ManualGenerationResult"/>
/// （docs/development-plan.md §19、phase0-contract.md §16 / §17 / §28）。
/// <list type="bullet">
///   <item>入力は TrainingProject のみ。Raw Event / events.jsonl / payload を参照しない</item>
///   <item>Builder または Writer に Error がある場合は Markdown / Html をとも null にする（atomic）</item>
///   <item>Markdown と HTML で重複した Error は重複排除し、出現順を維持する</item>
///   <item>TrainingProject / Steps / Outputs / Revision を変更しない</item>
///   <item>ファイル I/O・ディレクトリ作成・ProjectOutputs 更新・GeneratedArtifact 作成・時刻取得は行わない
///   （担当D = Storage / ProjectStore の責務）</item>
///   <item>例外を通常の検証結果として使わない（結果オブジェクトの Errors で返す）</item>
/// </list>
/// </summary>
public static class ManualGenerator
{
    /// <summary>Markdown の出力先（契約 §17 の Project Directory）。</summary>
    public const string MarkdownPath = "manual/manual.md";

    /// <summary>HTML の出力先（契約 §17 の Project Directory）。</summary>
    public const string HtmlPath = "manual/manual.html";

    public static ManualGenerationResult Generate(TrainingProject? project)
    {
        var documentBuild = ManualDocumentBuilder.Build(project);
        if (documentBuild.HasErrors || documentBuild.Document is null)
        {
            return Failure(documentBuild.Errors);
        }

        var markdown = MarkdownManualWriter.Write(documentBuild.Document);
        var html = HtmlManualWriter.Write(documentBuild.Document);

        // 片方だけの成果物を返さない（全体を atomic に失敗させる）。
        if (markdown.HasErrors || html.HasErrors)
        {
            return Failure([.. markdown.Errors, .. html.Errors]);
        }

        return new ManualGenerationResult
        {
            Markdown = new ManualGeneratedFile { Path = MarkdownPath, Content = markdown.Content },
            Html = new ManualGeneratedFile { Path = HtmlPath, Content = html.Content },
        };
    }

    private static ManualGenerationResult Failure(IEnumerable<string> errors) =>
        new() { Errors = DistinctInOrder(errors) };

    /// <summary>重複を排除しつつ出現順を維持する（Markdown / HTML で同一の Error が出るため）。</summary>
    private static List<string> DistinctInOrder(IEnumerable<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var distinct = new List<string>();

        foreach (var error in errors)
        {
            if (seen.Add(error))
            {
                distinct.Add(error);
            }
        }

        return distinct;
    }
}
