using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security;

namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// BlackBox Redaction（MVP の唯一の加工方式）。対象矩形を不透明な純黒で塗りつぶした新しい PNG を
/// <see cref="ScreenshotRedactionRequest.OutputPath"/>（実パス）へ生成する。
/// <list type="bullet">
///   <item>Source は読み取りのみ。overwrite / delete / rename しない</item>
///   <item>既存 Output は決して上書きしない（未使用 path のみ許可し、temp → <c>File.Move</c>（overwrite なし）で確定）</item>
///   <item>Rect は bitmap-local pixel 座標。出力の pixel size は入力と同一（metadata / DPI は MVP の保証外）</item>
///   <item>temp は Output と同じ directory に一意名で作り、成功・失敗いずれの経路でも残さない</item>
///   <item>Output directory は作成しない（存在しない場合は validation Error）</item>
///   <item>Project / Step / Revision には触れない（命名・保存は呼び出し側の責務）</item>
///   <item>期待される失敗は例外ではなく <see cref="ScreenshotRedactionResult.Errors"/> で返し、
///   path 実値・exception message を含めない</item>
/// </list>
/// <para>
/// 処理は background（<c>Task.Run</c>）で実行する。呼び出し側が UI スレッドで <c>await</c> しても
/// スレッドを塞がない。キャンセルは Result Error として返し、<c>OperationCanceledException</c> を
/// 外へは出さない。
/// </para>
/// </summary>
public sealed class BlackBoxScreenshotRedactor : IScreenshotRedactor
{
    /// <summary>BlackBox の色（不透明な純黒）。</summary>
    private static readonly Color BlackBoxColor = Color.FromArgb(255, 0, 0, 0);

    /// <summary>
    /// 呼び出し側（WPF の Review UI 等）が await したときに UI スレッドを塞がないよう background で実行する。
    /// token は <c>Task.Run</c> へ渡さない（pre-cancel でも Canceled Task を返さず Result Error にするため）。
    /// キャンセル判定は処理本体で行う。
    /// </summary>
    public Task<ScreenshotRedactionResult> RedactAsync(
        ScreenshotRedactionRequest request,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => Redact(request, cancellationToken));

    private static ScreenshotRedactionResult Redact(
        ScreenshotRedactionRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Failure("Redaction request が未設定です。");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled();
        }

        // Source は読み取り専用で開き、直後に閉じる（lock を残さない）。
        if (!TryReadSourceBytes(request.SourceImagePath, out var sourceBytes))
        {
            return Failure("SourceImagePath を読み込めません。");
        }

        if (!HasPngSignature(sourceBytes))
        {
            return Failure("SourceImagePath のPNGを読み込めません。");
        }

        var source = TryDecodePng(sourceBytes);
        if (source is null)
        {
            return Failure("SourceImagePath のPNGを読み込めません。");
        }

        using (source)
        {
            var validation = ScreenshotRedactionValidator.Validate(request, source.Width, source.Height);
            if (validation.HasErrors)
            {
                // 書き込みは行わない（部分出力なし）。
                return new ScreenshotRedactionResult { Errors = validation.Errors };
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            return Render(source, request.OutputPath, validation.NormalizedRegions, cancellationToken);
        }
    }

    /// <summary>Source の pixel を 1:1 でコピーし、矩形を純黒で塗って Output へ確定する。</summary>
    private static ScreenshotRedactionResult Render(
        Bitmap source,
        string outputPath,
        IReadOnlyList<RedactionRectangle> regions,
        CancellationToken cancellationToken)
    {
        string? tempPath = null;

        try
        {
            using var working = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(working))
            {
                // region 外の pixel を元の色のまま保つため 1:1 でコピーする。
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(source, 0, 0);

                // SourceCopy により alpha も含めて不透明な純黒で置き換える。
                using var brush = new SolidBrush(BlackBoxColor);
                foreach (var region in regions)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return Cancelled();
                    }

                    graphics.FillRectangle(brush, region.X, region.Y, region.Width, region.Height);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
            {
                return Failure("OutputPath へPNGを生成できません。");
            }

            // temp は必ず Output と同じ directory に置く（同一 volume での rename 確定のため）。
            tempPath = Path.Combine(outputDirectory, ".redaction-" + Guid.NewGuid().ToString("N") + ".tmp");

            try
            {
                using var tempStream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                working.Save(tempStream, ImageFormat.Png);
                tempStream.Flush();
            }
            catch (Exception ex) when (IsIoOrAccessException(ex))
            {
                return Failure("OutputPath へPNGを生成できません。");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Cancelled();
            }

            try
            {
                // overwrite なし: Output が既に存在する場合は例外になり、既存ファイルには触れない
                // （validation 後に他者によって作られた場合も上書きしない）。
                File.Move(tempPath, outputPath);
                tempPath = null;
            }
            catch (Exception ex) when (IsIoOrAccessException(ex))
            {
                return Failure("OutputPath へPNGを生成できません。");
            }

            return new ScreenshotRedactionResult
            {
                Width = working.Width,
                Height = working.Height,
                AppliedRegionCount = regions.Count,
            };
        }
        catch (Exception ex) when (IsIoOrAccessException(ex))
        {
            // 描画・保存で投げられうる file / 権限系の例外のみ Result 化する。
            // OutOfMemoryException 等の致命的例外はここでは捕捉しない（decode の局所範囲のみで扱う）。
            // path 実値・exception message は出さない。
            return Failure("OutputPath へPNGを生成できません。");
        }
        finally
        {
            // 成功・失敗・キャンセルのいずれでも temp を残さない。
            DeleteIfExists(tempPath);
        }
    }

    // --- source 読み込み ---

    private static bool TryReadSourceBytes(string? sourceImagePath, out byte[] bytes)
    {
        bytes = [];

        if (string.IsNullOrWhiteSpace(sourceImagePath))
        {
            return false;
        }

        try
        {
            bytes = File.ReadAllBytes(sourceImagePath);
            return true;
        }
        catch (Exception ex) when (IsIoOrAccessException(ex))
        {
            return false;
        }
    }

    /// <summary>PNG signature（8 byte）。拡張子だけ PNG の非画像・rename 済み画像を弾く。</summary>
    private static bool HasPngSignature(byte[] bytes) =>
        bytes.Length >= 8
        && bytes[0] == 0x89
        && bytes[1] == 0x50
        && bytes[2] == 0x4E
        && bytes[3] == 0x47
        && bytes[4] == 0x0D
        && bytes[5] == 0x0A
        && bytes[6] == 0x1A
        && bytes[7] == 0x0A;

    /// <summary>
    /// メモリ上のバイト列から decode し、stream に依存しない独立した Bitmap を返す。
    /// 失敗（破損・非対応形式）は null。
    /// </summary>
    private static Bitmap? TryDecodePng(byte[] sourceBytes)
    {
        try
        {
            using var stream = new MemoryStream(sourceBytes, writable: false);
            using var decoded = new Bitmap(stream);

            // Bitmap(Stream) は stream を保持しうるため、独立した copy を作ってから stream を閉じる。
            return new Bitmap(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or IOException or NotSupportedException)
        {
            // GDI+ は不正画像で ArgumentException / OutOfMemoryException を投げる（decode 周辺に限定）。
            return null;
        }
    }

    private static void DeleteIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsIoOrAccessException(ex))
        {
            // temp cleanup の失敗は Result の成否に影響させない（path も Error に出さない）。
        }
    }

    // --- Error 生成（path 実値・exception message を含めない） ---

    private static ScreenshotRedactionResult Failure(string error) => new() { Errors = [error] };

    private static ScreenshotRedactionResult Cancelled() => Failure("処理がキャンセルされました。");

    /// <summary>file 操作で投げられうる例外（致命的例外は含めない）。</summary>
    private static bool IsIoOrAccessException(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or SecurityException
            or PathTooLongException
            or ArgumentException;
}
