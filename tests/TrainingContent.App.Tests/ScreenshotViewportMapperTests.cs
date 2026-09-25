using TrainingContent.App.Services;
using Xunit;

namespace TrainingContent.App.Tests;

/// <summary>
/// C: <see cref="ScreenshotViewportMapper"/> の座標変換（R3〜R8）。
///
/// <para>
/// preview は <c>Stretch=Uniform</c> で表示するため、control 座標 → 実 bitmap pixel の変換には
/// scale と letterbox offset が必要になる。ここでは WPF を使わず純粋な数値で検証する。
/// </para>
/// </summary>
public class ScreenshotViewportMapperTests
{
    private const int BitmapWidth = 1920;
    private const int BitmapHeight = 1080;

    // =====================================================================
    // R3〜R5 — 表示矩形（letterbox）
    // =====================================================================

    [Fact]
    public void R3_同じ縦横比の_viewport_では_letterbox_が出ない()
    {
        var displayed = ScreenshotViewportMapper.DisplayedImageRect(BitmapWidth, BitmapHeight, 960, 540);

        Assert.Equal(0, displayed.X, 3);
        Assert.Equal(0, displayed.Y, 3);
        Assert.Equal(960, displayed.Width, 3);
        Assert.Equal(540, displayed.Height, 3);
    }

    [Fact]
    public void R4_縦長_viewport_では上下に_letterbox_が出る()
    {
        // scale = min(1000/1920, 700/1080) = 0.5208333 → 表示 1000 x 562.5、上下 68.75 ずつ。
        var displayed = ScreenshotViewportMapper.DisplayedImageRect(BitmapWidth, BitmapHeight, 1000, 700);

        Assert.Equal(0, displayed.X, 3);
        Assert.Equal(68.75, displayed.Y, 3);
        Assert.Equal(1000, displayed.Width, 3);
        Assert.Equal(562.5, displayed.Height, 3);
    }

    [Fact]
    public void R5_横長_viewport_では左右に_letterbox_が出る()
    {
        // scale = min(1000/1920, 400/1080) = 0.3703704 → 表示 711.111 x 400、左右 144.444 ずつ。
        var displayed = ScreenshotViewportMapper.DisplayedImageRect(BitmapWidth, BitmapHeight, 1000, 400);

        Assert.Equal(144.444, displayed.X, 3);
        Assert.Equal(0, displayed.Y, 3);
        Assert.Equal(711.111, displayed.Width, 3);
        Assert.Equal(400, displayed.Height, 3);
    }

    // =====================================================================
    // R6〜R8 — 選択矩形の bitmap 変換
    // =====================================================================

    [Fact]
    public void R6_letterbox_部分にはみ出した選択は_交差部分が_bitmap_全体になる()
    {
        // R4 と同じ geometry。viewport 全体を選ぶと画像全体（0,0,1920,1080）に収まる。
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            BitmapWidth, BitmapHeight, 1000, 700, new ScreenshotRect(0, 0, 1000, 700));

        Assert.NotNull(region);
        Assert.Equal(0, region!.X);
        Assert.Equal(0, region.Y);
        Assert.Equal(1920, region.Width);
        Assert.Equal(1080, region.Height);
    }

    [Fact]
    public void R7_letterbox_上だけの選択は_invalid_になる()
    {
        // 上側 letterbox（y < 68.75）だけを選ぶ → 画像表示領域と交差しない。
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            BitmapWidth, BitmapHeight, 1000, 700, new ScreenshotRect(0, 0, 500, 50));

        Assert.Null(region);
    }

    [Fact]
    public void R7b_bitmap_の外側だけの選択は_invalid_になる()
    {
        // viewport == bitmap（scale 1・letterbox なし）で、画像右外だけを選ぶ。
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            BitmapWidth, BitmapHeight, BitmapWidth, BitmapHeight, new ScreenshotRect(2000, 2000, 100, 100));

        Assert.Null(region);
    }

    [Fact]
    public void R8_left_top_は_floor_right_bottom_は_ceil_で_bitmap_境界内に_clamp_される()
    {
        // scale 1・letterbox なし。選択 (10.6, 20.4)-(100.2, 200.9) → (10,20) から (101,201)。
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            BitmapWidth, BitmapHeight, BitmapWidth, BitmapHeight,
            new ScreenshotRect(10.6, 20.4, 100.2 - 10.6, 200.9 - 20.4));

        Assert.NotNull(region);
        Assert.Equal(10, region!.X);
        Assert.Equal(20, region.Y);
        Assert.Equal(91, region.Width);   // 101 - 10
        Assert.Equal(181, region.Height); // 201 - 20
    }

    [Fact]
    public void R8b_bitmap_境界を越える選択は_bitmap_内へ_clamp_される()
    {
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            BitmapWidth, BitmapHeight, BitmapWidth, BitmapHeight,
            new ScreenshotRect(-50, -50, 150, 150)); // 左上が画像外

        Assert.NotNull(region);
        Assert.Equal(0, region!.X);
        Assert.Equal(0, region.Y);
        Assert.Equal(100, region.Width);
        Assert.Equal(100, region.Height);
    }

    [Fact]
    public void R8c_逆方向の_drag_でも正の矩形になる()
    {
        // 右下から左上へ drag（幅・高さが負の選択）。
        var region = ScreenshotViewportMapper.ToBitmapRectangle(
            BitmapWidth, BitmapHeight, BitmapWidth, BitmapHeight,
            new ScreenshotRect(300, 400, -200, -300));

        Assert.NotNull(region);
        Assert.Equal(100, region!.X);
        Assert.Equal(100, region.Y);
        Assert.Equal(200, region.Width);
        Assert.Equal(300, region.Height);
    }

    [Fact]
    public void R8d_degenerate_な入力では_null_を返す()
    {
        Assert.Null(ScreenshotViewportMapper.ToBitmapRectangle(0, 0, 100, 100, new ScreenshotRect(0, 0, 50, 50)));
        Assert.Null(ScreenshotViewportMapper.ToBitmapRectangle(1920, 1080, 0, 0, new ScreenshotRect(0, 0, 50, 50)));
        Assert.Null(ScreenshotViewportMapper.ToBitmapRectangle(1920, 1080, 1000, 700, new ScreenshotRect(0, 0, 0, 0)));
    }
}
