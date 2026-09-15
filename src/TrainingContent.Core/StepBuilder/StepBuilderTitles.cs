using TrainingContent.Core.Models;

namespace TrainingContent.Core;

/// <summary>1 件の TimelineEvent から導出した Step の内容（Warning は処理を継続した事実）。</summary>
public readonly record struct StepContent(string Action, string? Target, string Title, string? Warning);

/// <summary>
/// TrainingStep の Action / Target / Title を生成する（契約 §10 / §11.2 / §11.3 / §13 / §22）。
/// Target は mouse の payload.uiElement.name または textEntry の payload.target.name のみ。
/// windowTitle を Target の代用にしない。
/// </summary>
public static class StepBuilderTitles
{
    /// <summary>keyboard.specialKey の MVP 対象（契約 §11.2）。</summary>
    public static readonly IReadOnlySet<string> MvpSpecialKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "Enter", "Tab", "Escape", "Backspace", "Delete", "Left", "Right", "Up", "Down",
    };

    /// <summary>keyboard.shortcut の MVP 対象（契約 §11.3）。</summary>
    public static readonly IReadOnlySet<string> MvpShortcuts = new HashSet<string>(StringComparer.Ordinal)
    {
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+S", "Ctrl+Z",
    };

    /// <summary>
    /// 既知かつ non-lifecycle な Event に対して呼び出す。sensitive textEntry は呼び出し側で除外する
    /// （Title を生成しないため）。
    /// </summary>
    public static StepContent Describe(TimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);

        var payload = timelineEvent.Payload;
        var target = ResolveTarget(timelineEvent);
        var quoted = target is null ? null : $"「{target}」";

        switch (timelineEvent.Type)
        {
            case EventTypes.MouseClick:
                return new StepContent(StepActions.Click, target, Compose(quoted, "をクリックします", "クリックします"), null);

            case EventTypes.MouseDoubleClick:
                return new StepContent(StepActions.DoubleClick, target, Compose(quoted, "をダブルクリックします", "ダブルクリックします"), null);

            case EventTypes.MouseRightClick:
                return new StepContent(StepActions.RightClick, target, Compose(quoted, "を右クリックします", "右クリックします"), null);

            case EventTypes.KeyboardTextEntry:
                return new StepContent(StepActions.TextEntry, target, Compose(quoted, "に入力します", "テキストを入力します"), null);

            case EventTypes.KeyboardSpecialKey:
                var key = PayloadValidator.TryGetNonBlankString(payload, "key", out var keyValue) ? keyValue : "";
                return new StepContent(
                    StepActions.SpecialKey,
                    null,
                    $"{key} キーを押します",
                    MvpSpecialKeys.Contains(key) ? null : $"MVP リスト外の特殊キーです（Step は生成します）: {key} (seq={timelineEvent.Seq})。");

            case EventTypes.KeyboardShortcut:
                var shortcut = PayloadValidator.TryGetNonBlankString(payload, "shortcut", out var shortcutValue) ? shortcutValue : "";
                return new StepContent(
                    StepActions.Shortcut,
                    null,
                    $"{shortcut} を実行します",
                    MvpShortcuts.Contains(shortcut) ? null : $"MVP リスト外のショートカットです（Step は生成します）: {shortcut} (seq={timelineEvent.Seq})。");

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(timelineEvent),
                    timelineEvent.Type,
                    "Describe は既知かつ non-lifecycle な Event Type にのみ使用できます。");
        }
    }

    /// <summary>Target 規則（契約 §10 / §11.1）。取得できない場合は null。</summary>
    private static string? ResolveTarget(TimelineEvent timelineEvent)
    {
        return timelineEvent.Type switch
        {
            EventTypes.MouseClick or EventTypes.MouseDoubleClick or EventTypes.MouseRightClick =>
                PayloadValidator.TryGetObject(timelineEvent.Payload, "uiElement", out var uiElement)
                && PayloadValidator.TryGetNonBlankString(uiElement, "name", out var elementName)
                    ? elementName
                    : null,

            EventTypes.KeyboardTextEntry =>
                PayloadValidator.TryGetObject(timelineEvent.Payload, "target", out var target)
                && PayloadValidator.TryGetNonBlankString(target, "name", out var targetName)
                    ? targetName
                    : null,

            _ => null,
        };
    }

    private static string Compose(string? quotedTarget, string withTarget, string withoutTarget) =>
        quotedTarget is null ? withoutTarget : quotedTarget + withTarget;
}
