using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using Path = System.IO.Path;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary><see cref="ScreenshotReplacementCoordinator.ReplaceAsync"/> の結果分類。</summary>
public enum ScreenshotReplacementStatus
{
    /// <summary>fresh な edited PNG を生成し、ScreenshotPath の更新（Revision +1）まで完了した。</summary>
    Replaced,

    /// <summary>source file が存在しない / 読めない。</summary>
    SourceMissing,

    /// <summary>許可されていない拡張子（png / jpg / jpeg / bmp 以外）。</summary>
    UnsupportedFormat,

    /// <summary>decode / PNG encode に失敗した（Project は変更しない）。</summary>
    DecodeFailed,

    /// <summary>未使用の output path を確保できなかった（Project は変更しない）。</summary>
    OutputAllocationFailed,

    /// <summary>PNG 生成後の Workspace / Storage 保存に失敗した（生成済み PNG は orphan 許容）。</summary>
    SaveFailed,
}

/// <summary><see cref="ScreenshotReplacementCoordinator.ReplaceAsync"/> の結果。</summary>
/// <param name="Message">ユーザー向け generic message（raw path / exception message を含めない）。</param>
/// <param name="EditedRelativePath">成功時の project-relative path（契約 §18 の form）。</param>
public sealed record ScreenshotReplacementOutcome(
    ScreenshotReplacementStatus Status,
    string Message,
    string? EditedRelativePath = null)
{
    public bool Succeeded => Status == ScreenshotReplacementStatus.Replaced;
}

/// <summary>
/// Review UI（B3）からの screenshot import / replacement の orchestration（D-owned）。
///
/// <para>
/// <b>provenance rule</b>: <c>screenshots/original/</c> は EventCapture が録画中に取得した raw screenshot 専用で、
/// user import / replacement を保存してはいけない。user が持ち込む教材用 screenshot はすべて
/// <c>screenshots/edited/</c> へ <c>step-{StepId:N}-{ImportId:N}.png</c>（ImportId は毎回 fresh Guid）として新規作成する。
/// </para>
/// <para>
/// <b>置換ではなく新規作成</b>: 旧 ScreenshotPath の file（original / edited いずれでも）は削除しない。
/// 成功時は ScreenshotPath を fresh path へ更新するだけで、旧 file はそのまま残す
/// （original immutability / rollback failure surface を増やさない / 既存 redaction policy と揃える）。
/// </para>
/// <para>
/// <b>decode して PNG 化する</b>: 拡張子を .png へ rename するだけの copy はしない。WPF の
/// <see cref="BitmapDecoder"/> で decode し <see cref="PngBitmapEncoder"/> で encode する
/// （crop / resize / annotation / 色補正 / 画質調整は行わない = pixel は source と同等、
/// 第三者画像 library は追加しない）。
/// </para>
/// <para>
/// <b>source path は保存しない</b>: 外部絶対 path を project.json / TrainingProject / Step / metadata の
/// どこにも書かず、UI message にも出さない（Trace も path 実値を避ける）。
/// </para>
/// <para>
/// 意図的にやらないこと: Screenshot Core の変更、独自の path containment logic（resolver を再利用）、
/// 旧 file の delete / orphan cleanup、confirmation dialog。
/// </para>
/// </summary>
public sealed class ScreenshotReplacementCoordinator
{
    /// <summary>user import / replacement の置き場（契約 §17 の固定 directory）。</summary>
    public const string EditedDirectoryRelativePath = "screenshots/edited";

    /// <summary>fresh な ImportId を試す最大回数。</summary>
    private const int MaxImportIdAttempts = 5;

    /// <summary>dialog filter と同じ許可拡張子（単一 frame 系のみ）。</summary>
    private static readonly string[] SupportedExtensions = [".png", ".jpg", ".jpeg", ".bmp"];

    private const string SourceMissingMessage = "画像を読み込めませんでした。";
    private const string UnsupportedFormatMessage = "選択した画像を使用できません。";
    private const string DecodeFailedMessage = "選択した画像を使用できません。";
    private const string OutputAllocationFailedMessage = "画像を保存できませんでした。";
    private const string SaveFailedMessage = "スクリーンショットを保存できませんでした。";
    private const string ReplacedMessage = "スクリーンショットを差し替えました。";
    private const string ImportedMessage = "スクリーンショットを追加しました。";

    private readonly ProjectStore _projectStore;
    private readonly ProjectWorkspace _workspace;

    public ScreenshotReplacementCoordinator(ProjectStore projectStore, ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(workspace);

        _projectStore = projectStore;
        _workspace = workspace;
    }

    /// <summary>
    /// 外部画像を decode して fresh な edited PNG を作り、Step の ScreenshotPath を差し替える。
    /// </summary>
    /// <param name="currentScreenshotPath">
    /// 現在の ScreenshotPath（null / blank なら「追加」）。成功 message の出し分けにのみ使う。
    /// </param>
    /// <param name="sourcePath">OpenFileDialog が返した外部絶対 path（保存も UI 表示もしない）。</param>
    public async Task<ScreenshotReplacementOutcome> ReplaceAsync(
        Guid projectId,
        Guid stepId,
        string? currentScreenshotPath,
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return new ScreenshotReplacementOutcome(ScreenshotReplacementStatus.SourceMissing, SourceMissingMessage);
        }

        // 拡張子は許可 list のみ（GIF / TIFF / WebP / HEIC 等は scope 外）。判定は大文字小文字を問わない。
        var extension = Path.GetExtension(sourcePath);
        if (!SupportedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            Trace.TraceWarning("ScreenshotReplacementCoordinator: 未対応の拡張子です（拡張子のみ記録）。");
            return new ScreenshotReplacementOutcome(
                ScreenshotReplacementStatus.UnsupportedFormat, UnsupportedFormatMessage);
        }

        if (!File.Exists(sourcePath))
        {
            return new ScreenshotReplacementOutcome(ScreenshotReplacementStatus.SourceMissing, SourceMissingMessage);
        }

        var projectDirectory = _projectStore.GetProjectDirectory(projectId);

        // fresh な output path を確保する（既存 file は絶対に overwrite しない）。
        var output = EnsureFreshOutputPath(projectDirectory, stepId);
        if (output is null)
        {
            Trace.TraceError("ScreenshotReplacementCoordinator: 未使用の output path を確保できませんでした。");
            return new ScreenshotReplacementOutcome(
                ScreenshotReplacementStatus.OutputAllocationFailed, OutputAllocationFailedMessage);
        }

        // decode / encode は background で行い、その後の Workspace 更新は caller の context へ戻ってから行う。
        try
        {
            await Task.Run(
                    () => ConvertToPng(sourcePath, output.Value.AbsolutePath),
                    cancellationToken)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(output.Value.AbsolutePath);
            throw;
        }
        catch (Exception ex)
        {
            // 途中まで作られた fresh PNG は canonical からまだ参照されていないので best-effort で片付ける。
            // cleanup の失敗で元の例外を上書きしない。
            TryDeleteFile(output.Value.AbsolutePath);

            Trace.TraceError("ScreenshotReplacementCoordinator: 画像を PNG へ変換できませんでした — {0}", ex);
            return new ScreenshotReplacementOutcome(ScreenshotReplacementStatus.DecodeFailed, DecodeFailedMessage);
        }

        if (!File.Exists(output.Value.AbsolutePath))
        {
            Trace.TraceError("ScreenshotReplacementCoordinator: 変換成功のはずが output が存在しません。");
            return new ScreenshotReplacementOutcome(ScreenshotReplacementStatus.DecodeFailed, DecodeFailedMessage);
        }

        // ScreenshotPath の更新は Workspace（= Storage 境界）経由のみ。Revision / UpdatedAtUtc は Storage の責務。
        // Workspace は成功後に CurrentProject を publish する（Views が観測する）ので、ここも context を離れない。
        try
        {
            await _workspace.UpdateStepScreenshotAsync(projectId, stepId, output.Value.RelativePath, cancellationToken);
        }
        catch (Exception ex)
        {
            // 生成済み PNG は orphan として許容する（rollback delete の failure surface を増やさない）。
            Trace.TraceError("ScreenshotReplacementCoordinator: ScreenshotPath の保存に失敗しました — {0}", ex);
            return new ScreenshotReplacementOutcome(ScreenshotReplacementStatus.SaveFailed, SaveFailedMessage);
        }

        var message = string.IsNullOrWhiteSpace(currentScreenshotPath) ? ImportedMessage : ReplacedMessage;
        Trace.TraceInformation("ScreenshotReplacementCoordinator: screenshot を差し替えました。");

        return new ScreenshotReplacementOutcome(
            ScreenshotReplacementStatus.Replaced, message, output.Value.RelativePath);
    }

    /// <summary>
    /// <c>screenshots/edited/step-{StepId:N}-{ImportId:N}.png</c> を fresh に確保する。
    /// 既存 file に当たった場合は別 ImportId で再試行し、確保できなければ <c>null</c>。
    /// </summary>
    private (string RelativePath, string AbsolutePath)? EnsureFreshOutputPath(string projectDirectory, Guid stepId)
    {
        for (var attempt = 0; attempt < MaxImportIdAttempts; attempt++)
        {
            var relativePath = $"{EditedDirectoryRelativePath}/step-{stepId:N}-{Guid.NewGuid():N}.png";

            // project 内 path の解決は既存 resolver を使う（独自 containment logic を作らない）。
            var resolved = ProjectScreenshotPathResolver.Resolve(projectDirectory, relativePath);
            if (!resolved.Succeeded || resolved.AbsolutePath is null)
            {
                Trace.TraceError(
                    "ScreenshotReplacementCoordinator: output path を解決できません — {0}", resolved.ErrorMessage);
                return null;
            }

            if (!File.Exists(resolved.AbsolutePath))
            {
                return (relativePath, resolved.AbsolutePath);
            }
        }

        return null;
    }

    /// <summary>
    /// source を decode して PNG として書き出す（crop / resize / annotation / 画質調整はしない）。
    /// 出力は <see cref="FileMode.CreateNew"/> で開き、既存 file を上書きしない。
    /// </summary>
    private static void ConvertToPng(string sourcePath, string outputPath)
    {
        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(
            input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

        // 許可拡張子は単一 frame 系のみ（GIF / TIFF 等の複数 frame は scope 外）。
        var frame = decoder.Frames[0];

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));

        using var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        encoder.Save(output);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("ScreenshotReplacementCoordinator: 一時 file を削除できません — {0}", ex);
        }
    }
}
