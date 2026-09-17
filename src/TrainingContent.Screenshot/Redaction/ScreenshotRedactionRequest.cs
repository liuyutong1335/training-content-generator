namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// Redaction の入力。<see cref="SourceImagePath"/> と <see cref="OutputPath"/> は
/// App / Storage 境界から渡される<b>ファイルシステム上の実パス</b>であり、
/// project-relative path ではない。
/// <para>
/// 値の自動補正は行わない（<see cref="ScreenshotRedactionValidator"/> が行うのは
/// 矩形の画像境界への clamp のみ）。
/// </para>
/// </summary>
public sealed class ScreenshotRedactionRequest
{
    /// <summary>Redaction 対象画像の実パス（絶対パス・.png）。</summary>
    public string SourceImagePath { get; init; } = "";

    /// <summary>Redaction 結果の出力先実パス（絶対パス・.png）。出力ファイルは未作成でもよい。</summary>
    public string OutputPath { get; init; } = "";

    /// <summary>対象矩形（入力順を維持する。統合・並べ替え・重複排除はしない）。</summary>
    public IReadOnlyList<RedactionRectangle> Regions { get; init; } = [];
}
