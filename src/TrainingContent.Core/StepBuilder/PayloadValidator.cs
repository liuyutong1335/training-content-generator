using System.Text.Json;
using System.Text.RegularExpressions;
using TrainingContent.Core.Models;

namespace TrainingContent.Core;

/// <summary>
/// Event ごとの Payload 検証（契約 §10 / §11 / §18 / §22）。
/// 検証するのは「必須項目」と「存在する項目の明確な型不一致」「screenshotPath の Path Rule」のみ。
/// StepBuilder が使用しない項目を Required にしない。未知の追加 Payload フィールドは安全に無視し、
/// 値そのもの（実入力文字等）は読み出さない。
/// </summary>
public static partial class PayloadValidator
{
    /// <summary>screenshotPath に使う Project-relative の区切り（契約 §18）。</summary>
    private const char PosixSeparator = '/';

    private const string DotSegment = ".";
    private const string DotDotSegment = "..";

    [GeneratedRegex(@"^[A-Za-z]:")]
    private static partial Regex DriveLetterRegex();

    public static IReadOnlyList<string> Validate(TimelineEvent timelineEvent)
    {
        ArgumentNullException.ThrowIfNull(timelineEvent);

        var errors = new List<string>();
        var label = $"{timelineEvent.Type}, seq={timelineEvent.Seq}";
        var payload = timelineEvent.Payload;

        // payload が JSON object であることだけは全 Event Type で必須。
        if (payload.ValueKind == JsonValueKind.Undefined)
        {
            errors.Add($"payload がありません（{label}）。");
            return errors;
        }

        if (payload.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"payload は JSON object であること（{label}）: 実際は {payload.ValueKind}。");
            return errors;
        }

        switch (timelineEvent.Type)
        {
            // UI Automation 失敗時は uiElement: null としてよい（契約 §10）。x / y / button / clickCount /
            // processName / windowTitle は StepBuilder の必須項目ではない。
            case EventTypes.MouseClick:
            case EventTypes.MouseDoubleClick:
            case EventTypes.MouseRightClick:
                ValidateOptionalInteger(payload, "x", label, errors);
                ValidateOptionalInteger(payload, "y", label, errors);
                ValidateOptionalInteger(payload, "clickCount", label, errors);
                ValidateOptionalString(payload, "button", label, errors);
                ValidateOptionalString(payload, "processName", label, errors);
                ValidateOptionalString(payload, "windowTitle", label, errors);
                ValidateOptionalObject(payload, "uiElement", label, errors);
                if (TryGetObject(payload, "uiElement", out var uiElement))
                {
                    ValidateOptionalString(uiElement, "name", label, errors);
                }

                // screenshotPath は mouse payload 専用（契約 §10）。非 mouse では未知の追加フィールドとして
                // 無視する（契約 §23）ため、ここで mouse 系に限って検証する。
                ValidateScreenshotPath(payload, label, errors);
                break;

            case EventTypes.KeyboardTextEntry:
                RequireBoolean(payload, "isSensitive", label, errors);
                ValidateOptionalObject(payload, "target", label, errors);
                if (TryGetObject(payload, "target", out var target))
                {
                    ValidateOptionalString(target, "name", label, errors);
                }

                ValidateKeyCount(payload, label, errors);
                break;

            case EventTypes.KeyboardSpecialKey:
                RequireNonBlankString(payload, "key", label, errors);
                break;

            case EventTypes.KeyboardShortcut:
                RequireNonBlankString(payload, "shortcut", label, errors);
                break;
        }

        return errors;
    }

    /// <summary>Payload（object）から非 blank 文字列を取り出す。存在しない・型違い・blank は false。</summary>
    internal static bool TryGetNonBlankString(JsonElement element, string name, out string value)
    {
        value = "";
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = property.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    /// <summary>Payload（object）の boolean を取り出す。存在しない・型違いは false。</summary>
    internal static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    /// <summary>Payload（object）の object 値を取り出す。存在しない・null・型違いは false。</summary>
    internal static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        value = property;
        return true;
    }

    /// <summary>
    /// screenshotPath（契約 §10 / §18）: mouse 系 payload 専用。存在する場合のみ検証し、自動修正しない。
    /// 非 mouse Event では呼び出さない（未知の追加フィールドとして無視する）。
    /// </summary>
    private static void ValidateScreenshotPath(JsonElement payload, string label, List<string> errors)
    {
        if (!payload.TryGetProperty("screenshotPath", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors.Add($"payload の 'screenshotPath' は文字列であること（{label}）: 実際は {property.ValueKind}。");
            return;
        }

        var path = property.GetString()!;
        if (string.IsNullOrWhiteSpace(path))
        {
            errors.Add($"payload の 'screenshotPath' は空文字 / whitespace にできない（{label}）。");
        }
        else if (IsAbsolutePath(path))
        {
            errors.Add($"payload の 'screenshotPath' に絶対パスは保存できない（Project-relative にする）: {path}（{label}）。");
        }
        else if (path.Contains('\\'))
        {
            errors.Add($"payload の 'screenshotPath' のパス区切りは {PosixSeparator} に統一すること: {path}（{label}）。");
        }
        else if (HasTraversalSegment(path))
        {
            errors.Add($"payload の 'screenshotPath' に '{DotSegment}' / '{DotDotSegment}' のパスセグメントは使用できない（Project 外を参照しない）: {path}（{label}）。");
        }
    }

    /// <summary>Path Rule（契約 §18）: drive letter / UNC / 先頭区切りを絶対パスとして扱う。</summary>
    private static bool IsAbsolutePath(string path) =>
        DriveLetterRegex().IsMatch(path)
        || path.StartsWith('\\')
        || path.StartsWith('/');

    /// <summary>
    /// '.' / '..' を「セグメント全体」として含む場合のみ拒否する。
    /// <c>v1.2/a.png</c> のようにセグメント内に . を含むだけの名前は許可する。正規化・自動修正はしない。
    /// </summary>
    private static bool HasTraversalSegment(string path) =>
        path.Split(PosixSeparator).Any(segment => segment is DotSegment or DotDotSegment);

    private static void RequireBoolean(JsonElement payload, string name, string label, List<string> errors)
    {
        if (!payload.TryGetProperty(name, out var property))
        {
            errors.Add($"payload に必須フィールド '{name}' がありません（{label}）。");
        }
        else if (property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add($"payload の '{name}' は true / false であること（{label}）: 実際は {property.ValueKind}。");
        }
    }

    private static void RequireNonBlankString(JsonElement payload, string name, string label, List<string> errors)
    {
        if (!payload.TryGetProperty(name, out var property))
        {
            errors.Add($"payload に必須フィールド '{name}' がありません（{label}）。");
        }
        else if (property.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.GetString()))
        {
            errors.Add($"payload の '{name}' は非 blank の文字列であること（{label}）。");
        }
    }

    /// <summary>keyCount は StepBuilder 生成に必須ではない。存在する場合のみ null / 0 以上の整数であることを検証する。</summary>
    private static void ValidateKeyCount(JsonElement payload, string label, List<string> errors)
    {
        if (!payload.TryGetProperty("keyCount", out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out var keyCount) || keyCount < 0)
        {
            errors.Add($"payload の 'keyCount' は 0 以上の整数または null であること（{label}）: 実際は {property.ValueKind}。");
            return;
        }

        // sensitive な textEntry は文字数も保存しない（契約 §11.1）。
        if (TryGetBoolean(payload, "isSensitive", out var isSensitive) && isSensitive)
        {
            errors.Add($"sensitive な textEntry に keyCount が含まれています（文字数も保存禁止）: {label}。");
        }
    }

    private static void ValidateOptionalInteger(JsonElement payload, string name, string label, List<string> errors)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetInt64(out _))
        {
            errors.Add($"payload の '{name}' は整数（number）であること（{label}）: 実際は {property.ValueKind}。");
        }
    }

    private static void ValidateOptionalString(JsonElement payload, string name, string label, List<string> errors)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            errors.Add($"payload の '{name}' は文字列であること（{label}）: 実際は {property.ValueKind}。");
        }
    }

    private static void ValidateOptionalObject(JsonElement payload, string name, string label, List<string> errors)
    {
        if (!payload.TryGetProperty(name, out var property) || property.ValueKind is JsonValueKind.Null or JsonValueKind.Object)
        {
            return;
        }

        errors.Add($"payload の '{name}' は JSON object または null であること（{label}）: 実際は {property.ValueKind}。");
    }
}
