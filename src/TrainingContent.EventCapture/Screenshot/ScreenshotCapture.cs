// 参考実装: OpenSteps src/OpenSteps.Capture/ScreenshotService.cs (MIT License)
// Commit: 8058980865ac07f261b97b7270776c486b942a16
// 最小構成: 仮想デスクトップ全体を撮影し、クリック位置にハイライトを描画して PNG 保存する。
// 保存パスは契約 §18 (Path Rule) に従いプロジェクト相対・区切り文字は '/'。
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace TrainingContent.EventCapture.Screenshot;

public static class ScreenshotCapture
{
    private static int _counter;

    /// <summary>
    /// デスクトップ全体を撮影する。戻り値はプロジェクト相対パス
    /// （screenshots/original/event-XXXXXX.png）。失敗時は null。
    /// </summary>
    public static string? CaptureEventScreenshot(string projectDir, int clickX, int clickY)
    {
        try
        {
            var originalDir = Path.Combine(projectDir, "screenshots", "original");
            Directory.CreateDirectory(originalDir);

            var bounds = SystemInformation.VirtualScreen;
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

                const int radius = 22;
                using var pen = new Pen(Color.FromArgb(230, 220, 30, 30), 5);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.DrawEllipse(pen, clickX - bounds.Left - radius, clickY - bounds.Top - radius, radius * 2, radius * 2);
            }

            var fileName = $"event-{Interlocked.Increment(ref _counter):D6}.png";
            bitmap.Save(Path.Combine(originalDir, fileName), ImageFormat.Png);
            return $"screenshots/original/{fileName}";
        }
        catch
        {
            // 撮影失敗は Event 破棄の理由にしない（契約 §10 の UIA 失敗扱いに準ずる）。
            return null;
        }
    }
}
