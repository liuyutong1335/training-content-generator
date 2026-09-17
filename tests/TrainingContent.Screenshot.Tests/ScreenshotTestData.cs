namespace TrainingContent.Screenshot.Tests;

/// <summary>
/// Screenshot テスト専用の補助。役割は
/// ①テスト用一時ディレクトリの作成 ②空の dummy PNG ファイル（中身は検証しないため 0 byte）の作成
/// に限定する。実画像 fixture・個人名・実ローカル path のハードコードは行わない。
/// </summary>
internal static class ScreenshotTestData
{
    /// <summary>テスト用の一時ワークスペースを作る。Dispose で自身のディレクトリを削除する。</summary>
    public static TempWorkspace CreateWorkspace() => new();

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

        /// <summary>存在しない前提の実パスを返す（作成はしない）。</summary>
        public string NonExistentPath(string fileName) => Path.Combine(Root, "missing", fileName);

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
