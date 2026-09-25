namespace TrainingContent.App.Services;

/// <summary>
/// 新規 Project の入力値（正規化済み）。
///
/// <para>
/// 正規化は <see cref="FromRaw"/> に集約する。Home / Contents のどちらから作成しても
/// 同じ経路を通るため、Trim や prerequisites の分割規則が View ごとに二重実装されない。
/// </para>
/// <para>
/// この型は UI 入力の運搬用であり、TrainingProject の複製ではない。保存は必ず
/// ProjectStore.CreateProjectAsync が行う。
/// </para>
/// </summary>
public sealed class NewProjectInput
{
    public required string Title { get; init; }

    public string? Objective { get; init; }

    public string? TargetAudience { get; init; }

    public IReadOnlyList<string> Prerequisites { get; init; } = [];

    /// <summary>
    /// UI の生入力から正規化した入力を作る。
    /// Title は Trim 後に blank なら <see cref="ArgumentException"/>。
    /// </summary>
    public static NewProjectInput FromRaw(
        string? title,
        string? objective,
        string? targetAudience,
        string? prerequisitesText)
    {
        var normalizedTitle = (title ?? string.Empty).Trim();
        if (normalizedTitle.Length == 0)
        {
            throw new ArgumentException("Title は必須です（null / blank 不可）。", nameof(title));
        }

        return new NewProjectInput
        {
            Title = normalizedTitle,
            Objective = NormalizeOptional(objective),
            TargetAudience = NormalizeOptional(targetAudience),
            Prerequisites = SplitPrerequisites(prerequisitesText),
        };
    }

    /// <summary>Trim し、blank なら null にする。</summary>
    private static string? NormalizeOptional(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>CRLF / LF / CR で分割し、各行を Trim、blank 行を除外、入力順を維持する。</summary>
    private static IReadOnlyList<string> SplitPrerequisites(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return
        [
            .. text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0),
        ];
    }
}
