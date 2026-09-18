namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// Redaction の結果。Error が 1 件でもある場合 <see cref="Width"/> / <see cref="Height"/> は null であり、
/// 出力は生成されていない（atomic）。
/// <para>
/// <b>path は保持しない</b>: 呼び出し側は <see cref="ScreenshotRedactionRequest"/> で path を把握しており、
/// 結果をログ等へ出しても path 実値が漏れないようにするため。
/// <see cref="Errors"/> にも path 実値・exception message を含めない。
/// </para>
/// </summary>
public sealed class ScreenshotRedactionResult
{
    /// <summary>出力画像の幅（pixel）。失敗時は null。</summary>
    public int? Width { get; init; }

    /// <summary>出力画像の高さ（pixel）。失敗時は null。</summary>
    public int? Height { get; init; }

    /// <summary>適用した矩形の件数。</summary>
    public int AppliedRegionCount { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = [];

    public bool HasErrors => Errors.Count > 0;

    public bool Succeeded => !HasErrors;
}
