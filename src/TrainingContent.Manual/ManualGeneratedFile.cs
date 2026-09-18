namespace TrainingContent.Manual;

/// <summary>
/// 生成された成果物 1 件（Project-relative なパスと内容）。
/// 本クラスは内容を保持するだけで、保存・ディレクトリ作成は行わない
/// （書き込みは担当D = Storage / ProjectStore の責務）。
/// </summary>
public sealed class ManualGeneratedFile
{
    /// <summary>Project-relative なパス（例: manual/manual.md）。</summary>
    public string Path { get; init; } = "";

    public string Content { get; init; } = "";
}
