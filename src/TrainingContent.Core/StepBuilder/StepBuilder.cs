using TrainingContent.Core.Models;

namespace TrainingContent.Core;

/// <summary>
/// TimelineEvent[] → TrainingStep[]（契約 §28 Module Boundary）。
/// Primary Input は <see cref="IReadOnlyList{T}"/>（<see cref="TimelineEvent"/>）。
/// <list type="bullet">
///   <item>lifecycle Event は Warning なしで変換対象外（§9.2）</item>
///   <item>Unknown Event は Warning + Continue、Step には変換しない（§26）</item>
///   <item>sensitive textEntry は Warning + Continue、Step と Title を生成しない（§11.1）</item>
///   <item>Error が 1 件でもある場合は部分的な TrainingStep を返さない</item>
///   <item>seq を並べ替えて不正入力を修正しない（入力順 = Order 順）</item>
///   <item>EndMs は推測せず null（§14）／SourceEventIds に元 Event ID を保持（§15）</item>
///   <item>mouse 系 payload の screenshotPath は Project-relative のまま <see cref="TrainingStep.ScreenshotPath"/> へ引き継ぐ（§10 / §18）。
///   未設定は null。パスの自動修正はしない。非 mouse Event では screenshotPath を未知の追加フィールドとして無視し、常に null</item>
/// </list>
/// Raw Event は書き換えない。
/// </summary>
public static class StepBuilder
{
    /// <summary>events.jsonl のテキストから直接 Step を生成する。行の構文エラーは <see cref="EventsJsonlReader"/> が検出する。</summary>
    public static StepBuildResult BuildFromJsonl(string jsonl)
    {
        var read = EventsJsonlReader.Read(jsonl);
        return read.HasErrors
            ? new StepBuildResult { Errors = read.Errors }
            : Build(read.Events);
    }

    public static StepBuildResult Build(IReadOnlyList<TimelineEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var errors = ValidateStructure(events);
        if (errors.Count > 0)
        {
            return new StepBuildResult { Errors = errors };
        }

        var warnings = new List<string>();
        var steps = new List<TrainingStep>();
        var order = 1;

        foreach (var timelineEvent in events)
        {
            if (EventTypes.IsLifecycle(timelineEvent.Type))
            {
                continue;
            }

            if (!EventTypes.IsKnown(timelineEvent.Type))
            {
                warnings.Add($"未知の Event Type のため Step に変換しません: {timelineEvent.Type} (seq={timelineEvent.Seq})。");
                continue;
            }

            if (IsSensitiveTextEntry(timelineEvent))
            {
                warnings.Add($"sensitive な textEntry のため Step を生成しません（実入力・文字数は保存しない）: seq={timelineEvent.Seq}。");
                continue;
            }

            var content = StepBuilderTitles.Describe(timelineEvent);
            if (content.Warning is { } warning)
            {
                warnings.Add(warning);
            }

            steps.Add(new TrainingStep
            {
                Id = Guid.NewGuid(),
                Order = order++,
                StartMs = timelineEvent.TimestampMs,
                EndMs = null,
                Action = content.Action,
                Target = content.Target,
                Title = content.Title,
                ScreenshotPath = MouseScreenshotPath(timelineEvent),
                SourceEventIds = [timelineEvent.Id],
            });
        }

        return new StepBuildResult { Steps = steps, Warnings = warnings, Errors = errors };
    }

    /// <summary>
    /// 契約違反を静的に検出する。入力は採番し直さず、その場の順序で判定する
    /// （seq の並べ替えによる「修正」は行わない）。
    /// </summary>
    private static List<string> ValidateStructure(IReadOnlyList<TimelineEvent> events)
    {
        var errors = new List<string>();
        var seenIds = new HashSet<Guid>();
        var previousSeq = long.MinValue;

        foreach (var timelineEvent in events)
        {
            if (timelineEvent.Id == Guid.Empty)
            {
                errors.Add($"Event.Id が不正です（GUID 必須）: seq={timelineEvent.Seq}。");
            }
            else if (!seenIds.Add(timelineEvent.Id))
            {
                errors.Add($"Event.Id が重複しています: {timelineEvent.Id}。");
            }

            if (timelineEvent.Seq < 1)
            {
                errors.Add($"Event.Seq は 1 以上であること（Seq start = 1）: {timelineEvent.Seq}。");
            }
            else if (timelineEvent.Seq == previousSeq)
            {
                errors.Add($"Event.Seq が重複しています: {timelineEvent.Seq}。");
            }
            else if (timelineEvent.Seq < previousSeq)
            {
                errors.Add($"Event.Seq が単調増加していません（seq を並べ替えて不正入力を修正しない）: {previousSeq} -> {timelineEvent.Seq}。");
            }

            if (timelineEvent.Seq >= 1)
            {
                previousSeq = timelineEvent.Seq;
            }

            if (timelineEvent.TimestampMs < 0)
            {
                errors.Add($"Event.timestampMs は 0 以上であること（契約 §5.1）: {timelineEvent.TimestampMs}。");
            }

            if (string.IsNullOrWhiteSpace(timelineEvent.Type))
            {
                errors.Add($"Event.Type は null / blank 禁止です（契約 §22）: seq={timelineEvent.Seq}。");
                continue;
            }

            if (EventTypes.IsKnown(timelineEvent.Type) && !EventTypes.IsLifecycle(timelineEvent.Type))
            {
                errors.AddRange(PayloadValidator.Validate(timelineEvent));
            }
        }

        return errors;
    }

    /// <summary>
    /// mouse payload の screenshotPath のみを Step へ引き継ぐ（契約 §10）。
    /// 非 mouse Event の screenshotPath は未知の追加フィールドとして無視し、決して使用しない。
    /// 値は PayloadValidator が §18 の Path Rule で検証済みのものをそのまま使う（自動修正しない）。
    /// </summary>
    private static string? MouseScreenshotPath(TimelineEvent timelineEvent)
    {
        if (timelineEvent.Type is not (EventTypes.MouseClick or EventTypes.MouseDoubleClick or EventTypes.MouseRightClick))
        {
            return null;
        }

        return PayloadValidator.TryGetNonBlankString(timelineEvent.Payload, "screenshotPath", out var screenshotPath)
            ? screenshotPath
            : null;
    }

    private static bool IsSensitiveTextEntry(TimelineEvent timelineEvent) =>
        timelineEvent.Type == EventTypes.KeyboardTextEntry
        && PayloadValidator.TryGetBoolean(timelineEvent.Payload, "isSensitive", out var isSensitive)
        && isSensitive;
}
