// UseWPF=true の project では implicit usings が WPF 用になり System.IO が含まれない。
// また System.Windows.Shapes.Path との衝突を避けるため Path を明示的に alias する。
using System.IO;
using System.Text;
using Path = System.IO.Path;

namespace TrainingContent.App.Services;

/// <summary>
/// current recording session が events.jsonl へ append した範囲だけを読み出す helper。
///
/// <para>
/// <b>背景</b>: <c>EventTimelineWriter</c> は re-record 時に既存 events.jsonl へ <b>append</b> し、
/// seq も既存最大値から継続する（過去 session の行は残る）。したがってファイル全体を
/// <c>StepBuilder</c> へ渡すと、過去 session の Event まで Step 化されてしまう。
/// </para>
/// <para>
/// <b>境界の取り方</b>: session 開始（<c>OperationCaptureSession.Start()</c>）の <b>直前</b>に
/// ファイルの byte length を snapshot し、停止後にその offset から EOF までを読む。
/// offset は文字 index ではなく <see cref="FileStream"/> の byte position。
/// writer が既存末尾へ newline を補う場合があるため、先頭の CR/LF は除去してよい。
/// </para>
/// </summary>
public static class RecordingSessionEventsReader
{
    /// <summary>offset 未取得を表す sentinel。</summary>
    public const long NoOffset = -1;

    /// <summary>
    /// session 開始直前の events.jsonl の byte length を返す（= 今回 session の開始位置）。
    ///
    /// <para>
    /// ファイルが存在しない場合は 0（今回 session が先頭から書く）。読めない場合は例外を投げる
    /// （呼出側が EventCapture fault として扱い、integrated recording にしない）。
    /// </para>
    /// </summary>
    public static long SnapshotStartOffset(string eventsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventsPath);

        return File.Exists(eventsPath) ? new FileInfo(eventsPath).Length : 0;
    }

    /// <summary>
    /// <paramref name="startOffset"/> から EOF までを UTF-8 で読み出す。
    /// 先頭に混ざり得る CR/LF（writer の末尾改行正規化ぶん）は除去する。
    /// </summary>
    /// <returns>失敗時は <see cref="RecordingSessionEventsReadResult.Succeeded"/> が false。</returns>
    public static async Task<RecordingSessionEventsReadResult> ReadSessionAsync(
        string eventsPath,
        long startOffset,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventsPath);

        if (startOffset < 0)
        {
            return RecordingSessionEventsReadResult.Failure(
                "current session の events 範囲（byte offset）が取得できていません。");
        }

        if (!File.Exists(eventsPath))
        {
            return RecordingSessionEventsReadResult.Failure("events.jsonl が存在しません。");
        }

        try
        {
            await using var stream = new FileStream(
                eventsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            // session 開始時より短い = 別経路が truncate した等。読む範囲を決められないため失敗にする。
            if (stream.Length < startOffset)
            {
                return RecordingSessionEventsReadResult.Failure(
                    "events.jsonl が session 開始時より短くなっています。");
            }

            stream.Seek(startOffset, SeekOrigin.Begin);

            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                detectEncodingFromByteOrderMarks: false);

            var text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            return RecordingSessionEventsReadResult.Success(TrimLeadingNewlines(text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return RecordingSessionEventsReadResult.Failure(
                $"current session の events を読み出せませんでした（{ex.GetType().Name}）。");
        }
    }

    /// <summary>先頭の CR / LF を落とす（offset 直後に writer の改行正規化ぶんが入り得るため）。</summary>
    private static string TrimLeadingNewlines(string text)
    {
        var start = 0;
        while (start < text.Length && (text[start] == '\r' || text[start] == '\n'))
        {
            start++;
        }

        return start == 0 ? text : text[start..];
    }
}

/// <summary><see cref="RecordingSessionEventsReader.ReadSessionAsync"/> の結果。</summary>
public sealed record RecordingSessionEventsReadResult(bool Succeeded, string Jsonl, string? ErrorMessage)
{
    public static RecordingSessionEventsReadResult Success(string jsonl) => new(true, jsonl, null);

    public static RecordingSessionEventsReadResult Failure(string message) => new(false, string.Empty, message);
}
