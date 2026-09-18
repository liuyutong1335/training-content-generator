namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// Redaction 対象の矩形（画像座標・ピクセル単位）。
/// 実画像ファイル上の座標であり、project-relative な論理座標ではない。
/// <para>
/// 検証前は任意の int 値を保持しうる（Width / Height が 0 以下、座標が画像外など）。
/// <see cref="ScreenshotRedactionValidator"/> が Width / Height の正値性を検証し、
/// 画像境界への clamp を行う。
/// </para>
/// </summary>
public sealed record RedactionRectangle(int X, int Y, int Width, int Height);
