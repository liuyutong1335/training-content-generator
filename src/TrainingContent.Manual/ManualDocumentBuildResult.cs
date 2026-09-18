namespace TrainingContent.Manual;

/// <summary>
/// ManualDocument 構築の結果。例外は投げず Errors で返す。
/// Error が 1 件でもある場合 <see cref="Document"/> は null（部分的な Document を返さない）。
/// </summary>
public sealed class ManualDocumentBuildResult
{
    public ManualDocument? Document { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}
