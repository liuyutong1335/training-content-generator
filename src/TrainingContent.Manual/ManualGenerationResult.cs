namespace TrainingContent.Manual;

/// <summary>
/// Manual 生成の結果。Error が 1 件でもある場合 <see cref="Markdown"/> / <see cref="Html"/> は
/// ともに null であり、片方だけの成果物を返さない（atomic）。
/// <para>
/// 保存・ファイル書き込み・ProjectOutputs 更新・GeneratedArtifact 作成・GeneratedAtUtc 設定は
/// 本結果に含めない（担当D = Storage / ProjectStore の責務）。
/// </para>
/// </summary>
public sealed class ManualGenerationResult
{
    public ManualGeneratedFile? Markdown { get; init; }

    public ManualGeneratedFile? Html { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;
}
