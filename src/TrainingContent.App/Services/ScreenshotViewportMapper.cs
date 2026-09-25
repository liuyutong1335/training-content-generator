using TrainingContent.Screenshot.Redaction;

namespace TrainingContent.App.Services;

/// <summary>矩形（preview control の座標・DIP 単位）。</summary>
public readonly record struct ScreenshotRect(double X, double Y, double Width, double Height);

/// <summary>
/// Redaction 選択の座標変換（純関数・WPF 非依存）。
///
/// <para>
/// Review の preview は <c>Stretch=Uniform</c>（縦横比維持）で表示するため、control 座標は
/// 画像の pixel 座標と一致しない（letterbox と scale のぶんずれる）。
/// <see cref="RedactionRectangle"/> は<b>実 bitmap の pixel 座標</b>を要求するので、
/// ここで control 座標 → 実際に画像が描かれている矩形 → bitmap-local pixel へ変換する。
/// </para>
/// <para>
/// 整数化は left / top = <c>floor</c>、right / bottom = <c>ceil</c> に統一し、その後 bitmap 境界へ clamp する
/// （選択範囲を内側へ縮めない安全側の丸め）。letterbox 部分へのはみ出しは画像表示領域との
/// intersection を取り、交差が空なら <c>null</c>（redaction 不可）を返す。
/// </para>
/// </summary>
public static class ScreenshotViewportMapper
{
    /// <summary>
    /// <c>Stretch=Uniform</c> で bitmap を viewport に収めたときに、実際に画像が描かれる矩形（DIP）。
    /// 入力が不正な場合は幅 0 / 高さ 0 の矩形を返す。
    /// </summary>
    public static ScreenshotRect DisplayedImageRect(
        int bitmapWidth,
        int bitmapHeight,
        double viewportWidth,
        double viewportHeight)
    {
        if (bitmapWidth <= 0 || bitmapHeight <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
        {
            return new ScreenshotRect(0, 0, 0, 0);
        }

        var scale = Math.Min(viewportWidth / bitmapWidth, viewportHeight / bitmapHeight);
        if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
        {
            return new ScreenshotRect(0, 0, 0, 0);
        }

        var width = bitmapWidth * scale;
        var height = bitmapHeight * scale;

        return new ScreenshotRect((viewportWidth - width) / 2, (viewportHeight - height) / 2, width, height);
    }

    /// <summary>
    /// control 座標の選択矩形を bitmap-local pixel の <see cref="RedactionRectangle"/> へ変換する。
    /// </summary>
    /// <returns>有効な矩形が得られなければ <c>null</c>（redaction disabled）。</returns>
    public static RedactionRectangle? ToBitmapRectangle(
        int bitmapWidth,
        int bitmapHeight,
        double viewportWidth,
        double viewportHeight,
        ScreenshotRect selection)
    {
        if (bitmapWidth <= 0 || bitmapHeight <= 0)
        {
            return null;
        }

        var displayed = DisplayedImageRect(bitmapWidth, bitmapHeight, viewportWidth, viewportHeight);
        if (displayed.Width <= 0 || displayed.Height <= 0)
        {
            return null;
        }

        // drag は右下・左上どちらにも向くため、幅 / 高さを正規化してから交差を取る。
        var left = Math.Min(selection.X, selection.X + selection.Width);
        var top = Math.Min(selection.Y, selection.Y + selection.Height);
        var right = Math.Max(selection.X, selection.X + selection.Width);
        var bottom = Math.Max(selection.Y, selection.Y + selection.Height);

        var intersectedLeft = Math.Max(left, displayed.X);
        var intersectedTop = Math.Max(top, displayed.Y);
        var intersectedRight = Math.Min(right, displayed.X + displayed.Width);
        var intersectedBottom = Math.Min(bottom, displayed.Y + displayed.Height);

        if (intersectedRight <= intersectedLeft || intersectedBottom <= intersectedTop)
        {
            return null; // 画像表示領域と交差しない（letterbox 上だけの選択など）
        }

        var scale = displayed.Width / bitmapWidth;

        var bitmapLeft = (intersectedLeft - displayed.X) / scale;
        var bitmapTop = (intersectedTop - displayed.Y) / scale;
        var bitmapRight = (intersectedRight - displayed.X) / scale;
        var bitmapBottom = (intersectedBottom - displayed.Y) / scale;

        // 選択範囲を内側へ縮めない丸め（left/top = floor、right/bottom = ceil）。
        var x = (int)Math.Floor(bitmapLeft);
        var y = (int)Math.Floor(bitmapTop);
        var rightEdge = (int)Math.Ceiling(bitmapRight);
        var bottomEdge = (int)Math.Ceiling(bitmapBottom);

        x = Math.Clamp(x, 0, bitmapWidth);
        y = Math.Clamp(y, 0, bitmapHeight);
        rightEdge = Math.Clamp(rightEdge, 0, bitmapWidth);
        bottomEdge = Math.Clamp(bottomEdge, 0, bitmapHeight);

        var width = rightEdge - x;
        var height = bottomEdge - y;

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        return new RedactionRectangle(x, y, width, height);
    }
}
