namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// Redaction 入力の検証結果。Error が 1 件でもある場合 <see cref="NormalizedRegions"/> は空であり、
/// 部分的な plan を返さない（atomic）。
/// <para>
/// <see cref="Errors"/> には field 名と理由のみを含め、path 実値は含めない。
/// </para>
/// </summary>
public sealed class ScreenshotRedactionValidationResult
{
    /// <summary>画像境界へ clamp 済みの矩形（入力順を維持）。Error がある場合は空。</summary>
    public IReadOnlyList<RedactionRectangle> NormalizedRegions { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}
