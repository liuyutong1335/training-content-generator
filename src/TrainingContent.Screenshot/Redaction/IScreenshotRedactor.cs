namespace TrainingContent.Screenshot.Redaction;

/// <summary>
/// スクリーンショットの Redaction 窓口。D（App / Review UI）から呼ばれることを想定する。
/// <para>
/// 入力 <see cref="ScreenshotRedactionRequest"/> の path は App / Storage 境界から渡される
/// <b>ファイルシステム上の実パス</b>であり、project-relative path ではない。
/// 出力先の命名・Step（ScreenshotPath）の更新・Revision 更新・project.json 保存は責務に含まない
/// （呼び出し側が行う）。
/// </para>
/// <para>
/// 期待される失敗は例外ではなく <see cref="ScreenshotRedactionResult.Errors"/> で返す。
/// </para>
/// </summary>
public interface IScreenshotRedactor
{
    /// <summary>
    /// <see cref="ScreenshotRedactionRequest.Regions"/> を加工した新しい PNG を
    /// <see cref="ScreenshotRedactionRequest.OutputPath"/> へ生成する。
    /// </summary>
    Task<ScreenshotRedactionResult> RedactAsync(
        ScreenshotRedactionRequest request,
        CancellationToken cancellationToken = default);
}
