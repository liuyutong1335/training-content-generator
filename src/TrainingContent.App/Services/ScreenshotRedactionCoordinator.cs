// UseWPF=true の project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.Diagnostics;
using System.IO;
using Path = System.IO.Path;
using TrainingContent.Core.Models;
using TrainingContent.Screenshot.Redaction;
using TrainingContent.Storage;

namespace TrainingContent.App.Services;

/// <summary><see cref="ScreenshotRedactionCoordinator.RedactAsync"/> の結果分類。</summary>
public enum ScreenshotRedactionStatus
{
    /// <summary>edited PNG を生成し、ScreenshotPath の更新（Revision +1）まで完了した。</summary>
    Redacted,

    /// <summary>Step に ScreenshotPath が無い（redaction 不可）。</summary>
    NoScreenshot,

    /// <summary>ScreenshotPath が不正、または実ファイルを解決できない（redactor は呼ばない）。</summary>
    InvalidSource,

    /// <summary>redactor が失敗した（Project は変更しない）。</summary>
    RedactionFailed,

    /// <summary>PNG 生成後の Workspace / Storage 保存に失敗した（Project は旧状態。生成済み PNG は orphan 許容）。</summary>
    SaveFailed,
}

/// <summary><see cref="ScreenshotRedactionCoordinator.RedactAsync"/> の結果。</summary>
/// <param name="Message">ユーザー向け generic message（raw path / exception message を含めない）。</param>
/// <param name="EditedRelativePath">成功時の project-relative path（契約 §18 の form）。</param>
public sealed record ScreenshotRedactionOutcome(
    ScreenshotRedactionStatus Status,
    string Message,
    string? EditedRelativePath = null)
{
    public bool Succeeded => Status == ScreenshotRedactionStatus.Redacted;
}

/// <summary>
/// Review UI（C）からの BlackBox redaction の orchestration（D-owned）。
///
/// <para>
/// <b>流れ</b>: ScreenshotPath（project-relative）→ safe resolver で絶対パス → 実 bitmap pixel の矩形で
/// <see cref="IScreenshotRedactor.RedactAsync"/> → <b>fresh な edited PNG</b> を生成 →
/// <see cref="ProjectWorkspace.UpdateStepScreenshotAsync"/> で ScreenshotPath を更新（Revision +1 は Storage の責務）。
/// </para>
/// <para>
/// <b>Screenshot Core には踏み込まない</b>: Project / ScreenshotPath / Revision / output naming は決めない
/// （<see cref="IScreenshotRedactor"/> の責務外であることが契約）。
/// </para>
/// <para>
/// <b>original immutability</b>: <c>screenshots/original/*</c> は決して上書きしない。source は「現在参照されている
/// ScreenshotPath」（original でも edited でもよい）とし、出力は必ず
/// <c>screenshots/edited/step-{StepId:N}-{EditId:N}.png</c>（EditId は毎回 fresh Guid）へ新規作成する。
/// 出力が既存だった場合は別 EditId を試し、確保できなければ失敗にする（overwrite しない）。
/// </para>
/// <para>
/// <b>失敗時</b>: redactor 失敗では Project を触らない。PNG 生成後の保存失敗では <b>orphan な edited PNG を許容</b>し、
/// project.json と CurrentProject は旧状態のまま（rollback delete の追加 failure surface を作らない）。
/// </para>
/// <para>
/// 意図的にやらないこと: Revision / UpdatedAtUtc の直接更新、ProjectStore への直接保存、Pending / staging の操作、
/// View 依存、独自の画像処理（BlackBox 以外の手法・crop・resize・annotation）。
/// </para>
/// </summary>
public sealed class ScreenshotRedactionCoordinator
{
    /// <summary>edited PNG の置き場（契約 §17 の固定 directory。ProjectStore が作成する）。</summary>
    public const string EditedDirectoryRelativePath = "screenshots/edited";

    private const int MaxEditIdAttempts = 5;

    private const string NoScreenshotMessage = "この手順にはスクリーンショットがありません。";
    private const string InvalidSourceMessage = "スクリーンショットを読み込めないため、黒塗りできません。";
    private const string RedactionFailedMessage = "黒塗りに失敗しました。時間をおいて再度お試しください。";
    private const string SaveFailedMessage = "黒塗り画像を保存できませんでした。時間をおいて再度お試しください。";
    private const string RedactedMessage = "範囲を黒塗りしました。";

    private readonly IScreenshotRedactor _redactor;
    private readonly ProjectStore _projectStore;
    private readonly ProjectWorkspace _workspace;

    public ScreenshotRedactionCoordinator(
        IScreenshotRedactor redactor,
        ProjectStore projectStore,
        ProjectWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(workspace);

        _redactor = redactor;
        _projectStore = projectStore;
        _workspace = workspace;
    }

    /// <summary>
    /// preview 用（読み取り専用）: project-relative な ScreenshotPath を Project directory 配下の絶対パスへ解決する。
    /// </summary>
    public ScreenshotPathResolution ResolveScreenshotPath(Guid projectId, string? projectRelativePath) =>
        ProjectScreenshotPathResolver.Resolve(
            _projectStore.GetProjectDirectory(projectId), projectRelativePath);

    /// <summary>
    /// 選択矩形を BlackBox redaction して新しい edited PNG を生成し、Step の ScreenshotPath を更新する。
    /// </summary>
    /// <param name="region"><b>実 bitmap pixel</b> の矩形（<see cref="ScreenshotViewportMapper"/> で変換済みのもの）。</param>
    public async Task<ScreenshotRedactionOutcome> RedactAsync(
        Guid projectId,
        Guid stepId,
        string? currentScreenshotPath,
        RedactionRectangle region,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(currentScreenshotPath))
        {
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.NoScreenshot, NoScreenshotMessage);
        }

        if (region.Width <= 0 || region.Height <= 0)
        {
            Trace.TraceError("ScreenshotRedactionCoordinator: 選択矩形が不正です（{0}x{1}）。", region.Width, region.Height);
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.InvalidSource, InvalidSourceMessage);
        }

        var projectDirectory = _projectStore.GetProjectDirectory(projectId);

        // 1. source を安全に解決する（不正 path では redactor を呼ばない）。
        var source = ProjectScreenshotPathResolver.Resolve(projectDirectory, currentScreenshotPath);
        if (!source.Succeeded || source.AbsolutePath is null)
        {
            Trace.TraceError("ScreenshotRedactionCoordinator: source を解決できません — {0}", source.ErrorMessage);
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.InvalidSource, InvalidSourceMessage);
        }

        if (!File.Exists(source.AbsolutePath))
        {
            Trace.TraceError("ScreenshotRedactionCoordinator: source のファイルが存在しません。");
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.InvalidSource, InvalidSourceMessage);
        }

        // 2. fresh な output path を確保する（既存 file は絶対に overwrite しない）。
        var output = EnsureFreshOutputPath(projectDirectory, stepId);
        if (output is null)
        {
            Trace.TraceError("ScreenshotRedactionCoordinator: 未使用の output path を確保できませんでした。");
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.RedactionFailed, RedactionFailedMessage);
        }

        // 3. BlackBox redaction（Screenshot Core は path 命名も Project 更新も行わない）。
        // ここで ConfigureAwait(false) を使わない: この後の Workspace 更新は CurrentProject を publish し、
        // それを View が観測する（= UI thread でなければならない）ため、caller の context から離れない。
        // 画像処理自体は redactor 内の Task.Run で background 実行されるので UI は止まらない。
        ScreenshotRedactionResult result;
        try
        {
            result = await _redactor.RedactAsync(
                new ScreenshotRedactionRequest
                {
                    SourceImagePath = source.AbsolutePath,
                    OutputPath = output.Value.AbsolutePath,
                    Regions = [region],
                },
                cancellationToken);
        }
        catch (Exception ex)
        {
            Trace.TraceError("ScreenshotRedactionCoordinator: redactor が例外を投げました — {0}", ex);
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.RedactionFailed, RedactionFailedMessage);
        }

        if (result.HasErrors)
        {
            // raw な Errors は UI へ出さない（Trace のみ）。Project は一切変更されていない。
            Trace.TraceError(
                "ScreenshotRedactionCoordinator: redaction が失敗しました — {0}",
                string.Join(" / ", result.Errors));
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.RedactionFailed, RedactionFailedMessage);
        }

        if (!File.Exists(output.Value.AbsolutePath))
        {
            Trace.TraceError("ScreenshotRedactionCoordinator: redaction 成功のはずが output が存在しません。");
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.RedactionFailed, RedactionFailedMessage);
        }

        // 4. ScreenshotPath の更新は Workspace（= Storage 境界）経由のみ。Revision / UpdatedAtUtc は Storage の責務。
        // Workspace は成功後に CurrentProject を publish する（Views が観測する）ので、ここも context を離れない。
        try
        {
            await _workspace.UpdateStepScreenshotAsync(
                projectId, stepId, output.Value.RelativePath, cancellationToken);
        }
        catch (Exception ex)
        {
            // 生成済み PNG は orphan として許容する（rollback delete の failure surface を増やさない）。
            // project.json / CurrentProject は旧状態のまま。
            Trace.TraceError("ScreenshotRedactionCoordinator: ScreenshotPath の保存に失敗しました — {0}", ex);
            return new ScreenshotRedactionOutcome(ScreenshotRedactionStatus.SaveFailed, SaveFailedMessage);
        }

        Trace.TraceInformation("ScreenshotRedactionCoordinator: 範囲を黒塗りし ScreenshotPath を更新しました。");
        return new ScreenshotRedactionOutcome(
            ScreenshotRedactionStatus.Redacted, RedactedMessage, output.Value.RelativePath);
    }

    /// <summary>
    /// <c>screenshots/edited/step-{StepId:N}-{EditId:N}.png</c> を fresh に確保する。
    /// 既存 file に当たった場合は別 EditId で再試行し、確保できなければ <c>null</c>。
    /// </summary>
    private (string RelativePath, string AbsolutePath)? EnsureFreshOutputPath(string projectDirectory, Guid stepId)
    {
        for (var attempt = 0; attempt < MaxEditIdAttempts; attempt++)
        {
            var relativePath =
                $"{EditedDirectoryRelativePath}/step-{stepId:N}-{Guid.NewGuid():N}.png";

            var resolved = ProjectScreenshotPathResolver.Resolve(projectDirectory, relativePath);
            if (!resolved.Succeeded || resolved.AbsolutePath is null)
            {
                Trace.TraceError("ScreenshotRedactionCoordinator: output path を解決できません — {0}", resolved.ErrorMessage);
                return null;
            }

            if (!File.Exists(resolved.AbsolutePath))
            {
                return (relativePath, resolved.AbsolutePath);
            }
        }

        return null;
    }
}
