using System.Text;
using TrainingContent.Video.Timeline;

namespace TrainingContent.Video.Subtitle;

/// <summary>
/// 表示区間 → ASS (Advanced SubStation Alpha) 字幕ファイルの生成（開発計画書 §12 Subtitle 相当）。
/// ffmpeg の ass フィルタで焼き込む。pure logic なのでユニットテスト可能（監査 SP 系の教訓）。
/// </summary>
public static class AssSubtitleWriter
{
    /// <summary>Dialogue テキスト内で ASS の制御文字になる箇所をエスケープし、改行を \N にする。</summary>
    public static string EscapeText(string? text) =>
        (text ?? "")
            .Replace("{", "｛")
            .Replace("}", "｝")
            .Replace("\r\n", "\\N")
            .Replace("\n", "\\N");

    /// <summary>ms → ASS 時刻（h:mm:ss.mmm）。</summary>
    public static string FormatTime(long ms) =>
        $"{ms / 3600_000}:{ms / 60_000 % 60:D2}:{ms / 1000 % 60:D2}.{ms % 1000:D3}";

    /// <summary>
    /// Step 表示区間列 → ASS 全文。画面下部（\an2）に Step 番号 + Title、
    /// 続く行に Description / ExpectedResult（StepDetail スタイル）または Caution（強調スタイル）を出す。
    /// </summary>
    public static string Write(IReadOnlyList<StepDisplayInterval> intervals, int playResX = 1280, int playResY = 720)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {playResX}");
        sb.AppendLine($"PlayResY: {playResY}");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        // Format 行は Dialogue のフィールド数と一致させる（一致しないと余剰フィールドがテキストとして描画される）
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine($"Style: StepTitle,Yu Gothic UI,{(int)(playResY * 0.055)},&H00FFFFFF,&H00000000,&H80000000,1,2,0,2,{(int)(playResX * 0.047)},{(int)(playResX * 0.047)},{(int)(playResY * 0.083)},0");
        sb.AppendLine($"Style: StepDetail,Yu Gothic UI,{(int)(playResY * 0.036)},&H00FFFFFF,&H00000000,&H80000000,0,1,0,2,{(int)(playResX * 0.047)},{(int)(playResX * 0.047)},{(int)(playResY * 0.028)},0");
        sb.AppendLine($"Style: StepCaution,Yu Gothic UI,{(int)(playResY * 0.036)},&H0000D7FF,&H00000000,&H80000000,1,1,0,2,{(int)(playResX * 0.047)},{(int)(playResX * 0.047)},{(int)(playResY * 0.028)},0");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");

        foreach (var interval in intervals)
        {
            var step = interval.Step;
            var title = $"{step.Order}. {EscapeText(step.Title)}";
            var detailLines = new[] { step.Description, step.ExpectedResult }
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(EscapeText);
            var detail = string.Join("\\N", detailLines);

            var text = string.IsNullOrWhiteSpace(step.Caution)
                ? $"{title}{{\\rStepDetail}}\\N{detail}"
                : $"{title}{{\\rStepCaution}}\\N{EscapeText(step.Caution)}{{\\rStepDetail}}\\N{detail}";

            sb.AppendLine($"Dialogue: 0,{FormatTime(interval.StartMs)},{FormatTime(interval.EndMs)},StepTitle,,0,0,0,,{text}");
        }

        return sb.ToString();
    }

    /// <summary>Title Screen / Ending 用の中央寄せ 1 枚カード（契約 §20）。</summary>
    public static string WriteCard(string text, double seconds, int playResX = 1280, int playResY = 720)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Script Info]");
        sb.AppendLine("ScriptType: v4.00+");
        sb.AppendLine($"PlayResX: {playResX}");
        sb.AppendLine($"PlayResY: {playResY}");
        sb.AppendLine();
        sb.AppendLine("[V4+ Styles]");
        sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, OutlineColour, BackColour, Bold, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
        sb.AppendLine($"Style: Card,Yu Gothic UI,{(int)(playResY * 0.075)},&H00FFFFFF,&H00000000,&H80000000,1,2,0,5,0,0,0,0");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
        // \fad で 0.3 秒のフェードイン/アウト（カードの切り替わりを滑らかにする）
        sb.AppendLine($"Dialogue: 0,0:00:00.000,{FormatTime((long)(seconds * 1000))},Card,,0,0,0,,{{\\fad(300,300)}}{EscapeText(text)}");
        return sb.ToString();
    }
}
