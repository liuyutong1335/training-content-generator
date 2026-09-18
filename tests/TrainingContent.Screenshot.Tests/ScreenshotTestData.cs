using System.Drawing;
using System.Drawing.Imaging;

namespace TrainingContent.Screenshot.Tests;

/// <summary>
/// Screenshot テスト専用の補助。役割は
/// ①テスト用一時ディレクトリの作成 ②テスト用画像（実行時に生成する小さな PNG 等）の作成
/// ③画像 / ファイル内容の読み取り に限定する。
/// binary fixture・個人名・実ローカル path のハードコードは行わない。
/// </summary>
internal static class ScreenshotTestData
{
    /// <summary>テスト用の一時ワークスペースを作る。Dispose で自身のディレクトリを削除する。</summary>
    public static TempWorkspace CreateWorkspace() => new();

    /// <summary>実パスの pixel 色を読む。</summary>
    public static Color ReadPixel(string path, int x, int y)
    {
        using var bitmap = new Bitmap(path);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>実パスの画像サイズを読む。</summary>
    public static (int Width, int Height) ReadSize(string path)
    {
        using var bitmap = new Bitmap(path);
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>実パスの全バイトを読む（入力不変の検証用）。</summary>
    public static byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    internal sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "tcg-screenshot-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        /// <summary>ワークスペースの root（実パス）。</summary>
        public string Root { get; }

        /// <summary>ワークスペース内の実パスを作る（ファイルは作らない）。</summary>
        public string PathIn(string fileName) => Path.Combine(Root, fileName);

        /// <summary>空の PNG ファイル（0 byte）を作り、その実パスを返す。</summary>
        public string CreateDummyPng(string fileName = "source.png")
        {
            var path = PathIn(fileName);
            using (File.Create(path))
            {
            }

            return path;
        }

        /// <summary>単色の PNG を生成して実パスを返す（テスト用画像。binary fixture は使わない）。</summary>
        public string CreatePng(string fileName, int width, int height, Color fill)
        {
            var path = PathIn(fileName);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(fill);
            }

            bitmap.Save(path, ImageFormat.Png);
            return path;
        }

        /// <summary>PNG 以外の実画像（JPEG）を .png 拡張子で保存する（rename 済み画像の再現）。</summary>
        public string CreateJpegNamedPng(string fileName, int width = 4, int height = 4)
        {
            var path = PathIn(fileName);
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Blue);
            }

            bitmap.Save(path, ImageFormat.Jpeg);
            return path;
        }

        /// <summary>PNG signature のみ正しい壊れたファイルを作る。</summary>
        public string CreateCorruptPng(string fileName = "corrupt.png")
        {
            var path = PathIn(fileName);
            var bytes = new byte[]
            {
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x01, 0x02, 0x03, 0x04, 0x05,
            };
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>PNG signature を持たないテキストファイルを .png 拡張子で作る。</summary>
        public string CreateNonImagePng(string fileName = "not-an-image.png")
        {
            var path = PathIn(fileName);
            File.WriteAllText(path, "this is not an image");
            return path;
        }

        /// <summary>存在しない前提の実パスを返す（作成はしない）。</summary>
        public string NonExistentPath(string fileName) => Path.Combine(Root, "missing", fileName);

        /// <summary>workspace root 直下のファイル / ディレクトリ名（ソート済み）。</summary>
        public IReadOnlyList<string> ListEntries() =>
            [.. Directory.GetFileSystemEntries(Root).Select(Path.GetFileName)!.Order(StringComparer.Ordinal)];

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch (IOException)
            {
                // テスト後の後片付け失敗はテスト結果に影響させない。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
