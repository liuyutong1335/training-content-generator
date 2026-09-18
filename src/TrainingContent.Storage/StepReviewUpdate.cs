namespace TrainingContent.Storage;

/// <summary>
/// Review UI（B1）が編集してよい項目だけを持つ narrow な write request。
///
/// <para>
/// <see cref="TrainingStep"/> 全体を write contract として受け取らない。
/// Id / Order / StartMs / EndMs / Action / Target / ScreenshotPath / SourceEventIds は
/// <b>この型では表現できない</b>ため、caller が B1 の編集対象外の field を書き換えられない。
/// それらは既存 canonical Step から保持する。
/// </para>
/// <para>
/// request list の順序が保存後の <see cref="TrainingStep.Order"/>（1..N）になる。
/// </para>
/// <para>
/// Description / Caution / ExpectedResult の null / empty / whitespace-only は
/// canonical な null として扱われる。
/// </para>
/// </summary>
/// <param name="StepId">既存 Step の Id。未知の Id（新規追加）は reject される。</param>
/// <param name="Title">Step のタイトル。null / blank は reject される（trim はしない）。</param>
/// <param name="Description">説明。null / blank は canonical null になる。</param>
/// <param name="Caution">注意。null / blank は canonical null になる。</param>
/// <param name="ExpectedResult">期待される結果。null / blank は canonical null になる。</param>
public sealed record StepReviewUpdate(
    Guid StepId,
    string Title,
    string? Description,
    string? Caution,
    string? ExpectedResult);
