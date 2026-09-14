using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OperationCaptureSpike;
using OperationCaptureSpike.Hooks;
using OperationCaptureSpike.UiAutomation;

// =====================================================================
// Spike B — Operation Capture (担当 B)
// 目的: 1 回の操作から Timestamp / Action / Process / Window / UI Element /
//       ControlType / Coordinates / Screenshot を取得し、
//       Phase 0 契約 v1.0 準拠の TimelineEvent (events.jsonl) を出力する。
// 使い方: リポジトリルートで
//   dotnet run --project spike/operation-capture
// =====================================================================

Console.OutputEncoding = System.Text.Encoding.UTF8;
Console.WriteLine("=== Spike B: Operation Capture ===");
Console.WriteLine();
Console.WriteLine("  [Enter] 録画開始 (その後、好きなアプリを操作してください)");
Console.WriteLine("  録画中 : P = 一時停止 / R = 再開 / S = 停止して終了");
Console.WriteLine();
Console.WriteLine("  注意: このコンソール自体の操作は記録対象外にしています。");
Console.Write("> ");

while (Console.ReadKey(true).Key != ConsoleKey.Enter)
{
    Console.Write("> ");
}

var repoRoot = FindRepoRoot();
var projectDir = Path.Combine(repoRoot, "projects", Guid.NewGuid().ToString());
Directory.CreateDirectory(projectDir);
var writer = new EventWriter(Path.Combine(projectDir, "events.jsonl"));
var clock = new MasterClock();
var uiAutomation = new UiAutomationService();

// 自プロセス（このコンソール）の操作は記録対象外にする。
var ownProcessId = (uint)Environment.ProcessId;

string? lastTextWindowKey = null;

// keyboard.textEntry の連続入力バッファ（バースト単位で 1 Event にまとめる）。
var textBuffer = new List<TextKey>();
var textTarget = default((UiElementInfo? Element, string? ProcessName, string? WindowTitle));

// ---- ワーカースレッド（Hook コールバックをブロックしないため） ----
var queue = new BlockingCollection<RawItem>();
var worker = new Thread(ProcessQueue) { IsBackground = true };
worker.Start();

void Enqueue(RawItem item)
{
    if (!queue.IsAddingCompleted)
    {
        queue.Add(item);
    }
}

void HandleMouseEvent(object? _, ClickCapturedEventArgs e)
{
    Enqueue(new RawItem(clock.NowMs(), RawKind.Mouse, X: e.X, Y: e.Y, ClickType: e.ActionType));
}

void HandleKeyEvent(object? _, KeyboardInputEventArgs e)
{
    Enqueue(new RawItem(clock.NowMs(), RawKind.Key, KeyKind: e.Kind, KeyName: e.KeyName, ShortcutName: e.ShortcutName));
}

void FlushTextBuffer()
{
    if (textBuffer.Count == 0)
    {
        return;
    }

    var isSensitive = textBuffer.Any(k => k.IsPassword);
    var payload = new TextEntryPayload(
        isSensitive ? null : textBuffer.Count,
        textBuffer[0].ProcessName,
        textBuffer[0].WindowTitle,
        textTarget.Element is { } el && !isSensitive
            ? new TargetPayload(el.Name, el.AutomationId, el.ControlType)
            : null,
        isSensitive);
    writer.Append("keyboard.textEntry", textBuffer[0].TimestampMs, payload);
    textBuffer.Clear();
    textTarget = default;
    lastTextWindowKey = null;
}

bool IsOwnProcess(WindowInfo window) => window.ProcessId == ownProcessId;

void ProcessQueue()
{
    foreach (var item in queue.GetConsumingEnumerable())
    {
        try
        {
            ProcessItem(item);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] event processing failed: {ex.Message}");
        }
    }
}

void ProcessItem(RawItem item)
{
    // Pause 中のユーザー操作は破棄する（Canonical Timeline の外側のため。詳細は README）。
    if (clock.IsPaused)
    {
        return;
    }

    if (item.Kind == RawKind.Mouse)
    {
        FlushTextBuffer();

        var window = WindowInfoService.FromPoint(item.X, item.Y);
        if (IsOwnProcess(window))
        {
            return;
        }

        var uiInfo = uiAutomation.GetElementAt(item.X, item.Y);
        var screenshotPath = ScreenshotCapture.CaptureEventScreenshot(projectDir, item.X, item.Y);
        var eventType = item.ClickType switch
        {
            SpikeClickType.Click => "mouse.click",
            SpikeClickType.DoubleClick => "mouse.doubleClick",
            SpikeClickType.RightClick => "mouse.rightClick",
            _ => "mouse.click"
        };
        writer.Append(eventType, item.TimestampMs, new MousePayload(
            item.X,
            item.Y,
            item.ClickType == SpikeClickType.RightClick ? "right" : "left",
            item.ClickType == SpikeClickType.DoubleClick ? 2 : 1,
            window.ProcessName,
            window.WindowTitle,
            new UiElementPayload(
                uiInfo.Name,
                uiInfo.AutomationId,
                uiInfo.ControlType,
                uiInfo.ClassName,
                uiInfo.IsEditable,
                uiInfo.IsPassword,
                uiInfo.Bounds is { } b ? new BoundsPayload(b.X, b.Y, b.Width, b.Height) : null),
            screenshotPath));
        return;
    }

    if (item.Kind == RawKind.Key)
    {
        switch (item.KeyKind)
        {
            case KeyboardInputKind.Text:
            {
                var window = WindowInfoService.FromForegroundWindow();
                if (IsOwnProcess(window))
                {
                    return;
                }

                // バースト先頭でフォーカス要素を取得（Password 判定と target 用）。
                if (textBuffer.Count == 0 || lastTextWindowKey != WindowKey(window))
                {
                    var focused = uiAutomation.GetFocusedElement();
                    textTarget = (focused, window.ProcessName, window.WindowTitle);
                    lastTextWindowKey = WindowKey(window);
                }

                textBuffer.Add(new TextKey(
                    item.TimestampMs,
                    textTarget.Element?.IsPassword == true,
                    window.ProcessName,
                    window.WindowTitle));

                // 入力が途切れたらバーストを閉じる（別ウィンドウ/長い空白）。
                if (textBuffer.Count > 1
                    && item.TimestampMs - textBuffer[^2].TimestampMs > 2000)
                {
                    FlushTextBuffer();
                }

                break;
            }

            case KeyboardInputKind.SpecialKey:
                FlushTextBuffer();
                writer.Append("keyboard.specialKey", item.TimestampMs, new SpecialKeyPayload(item.KeyName ?? "Unknown"));
                break;

            case KeyboardInputKind.Shortcut:
                FlushTextBuffer();
                writer.Append("keyboard.shortcut", item.TimestampMs, new ShortcutPayload(item.ShortcutName ?? "Unknown"));
                break;
        }
    }
}

static string WindowKey(WindowInfo w) => $"{w.ProcessId}:{w.WindowTitle}";

// ---- 録画開始 ----
using var mouseHook = new GlobalMouseHook();
using var keyboardHook = new GlobalKeyboardHook();
mouseHook.ClickCaptured += HandleMouseEvent;
keyboardHook.KeyboardInputCaptured += HandleKeyEvent;

mouseHook.Start();
keyboardHook.Start();
clock.Start();
writer.Append("recording.started", clock.NowMs(), new { });
Console.WriteLine();
Console.WriteLine("録画を開始しました。デスクトップを操作してください。");

// ---- コンソール制御ルール（P / R / S） ----
while (true)
{
    var key = Console.ReadKey(true).Key;
    if (key == ConsoleKey.P)
    {
        FlushTextBuffer();
        clock.Pause();
        writer.Append("recording.paused", clock.NowMs(), new { });
        Console.WriteLine("-- paused --");
    }
    else if (key == ConsoleKey.R)
    {
        clock.Resume();
        writer.Append("recording.resumed", clock.NowMs(), new { });
        Console.WriteLine("-- resumed --");
    }
    else if (key == ConsoleKey.S)
    {
        FlushTextBuffer();
        var durationMs = clock.NowMs();
        mouseHook.Stop();
        keyboardHook.Stop();
        writer.Append("recording.stopped", durationMs, new { });
        queue.CompleteAdding();
        worker.Join(3000);
        Console.WriteLine("-- stopped --");
        break;
    }
}

// ---- 結果サマリ + 契約 §29 準拠のセルフチェック ----
var jsonlPath = Path.Combine(projectDir, "events.jsonl");
Console.WriteLine();
Console.WriteLine($"events.jsonl : {jsonlPath}");
Console.WriteLine($"Event count  : {writer.Count}");
Console.WriteLine($"Screenshots  : {Path.Combine(projectDir, "screenshots", "original")}");
Console.WriteLine();

var problems = ValidateEvents(jsonlPath);
if (problems.Count == 0)
{
    Console.WriteLine("セルフチェック: PASS (seq / timestampMs / type / path rule)");
}
else
{
    Console.WriteLine($"セルフチェック: {problems.Count} 件の違反");
    foreach (var problem in problems)
    {
        Console.WriteLine($"  - {problem}");
    }
}

Console.WriteLine();
Console.WriteLine("Gate B の確認項目: Timestamp / Action / Process / Window / Element / ControlType / Coordinates / Screenshot");
Console.WriteLine("mouse.click の行に上記 8 項目が揃っているか確認してください。");

static List<string> ValidateEvents(string path)
{
    var problems = new List<string>();
    var knownTypes = new HashSet<string>
    {
        "mouse.click", "mouse.doubleClick", "mouse.rightClick",
        "keyboard.textEntry", "keyboard.specialKey", "keyboard.shortcut",
        "recording.started", "recording.paused", "recording.resumed", "recording.stopped"
    };
    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
    options.Converters.Add(new JsonStringEnumConverter());

    long expectedSeq = 1;
    foreach (var line in File.ReadLines(path))
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var seq = root.GetProperty("seq").GetInt64();
        if (seq != expectedSeq++)
        {
            problems.Add($"seq が単調増加していません: {seq} (expected {expectedSeq - 1})");
        }

        var timestampMs = root.GetProperty("timestampMs").GetInt64();
        if (timestampMs < 0)
        {
            problems.Add($"timestampMs が負です: seq={seq}");
        }

        var type = root.GetProperty("type").GetString() ?? "";
        if (!knownTypes.Contains(type))
        {
            problems.Add($"未知の Event Type: {type} (契約 §26 は警告のみ、本チェックでは違反扱い)");
        }

        // Path Rule (§18): JSON 内に絶対パス・'\\' を保存しない。
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "payload" && property.Value.ValueKind == JsonValueKind.Object)
            {
                CheckPaths(property.Value, seq, problems);
            }
        }
    }

    return problems;
}

static void CheckPaths(JsonElement payload, long seq, List<string> problems)
{
    foreach (var property in payload.EnumerateObject())
    {
        if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString() is { } text
            && (text.Contains('\\') || (text.Length > 1 && text[1] == ':')))
        {
            problems.Add($"絶対パスまたは '\\' を検出: seq={seq}, {property.Name}={text}");
        }
        else if (property.Value.ValueKind == JsonValueKind.Object)
        {
            CheckPaths(property.Value, seq, problems);
        }
    }
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "TrainingContentGenerator.sln")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    throw new InvalidOperationException("リポジトリルートが見つかりません。リポジトリ内で実行してください。");
}

// ---- 内部レコード ----

internal static class RawKind
{
    public const string Mouse = "mouse";
    public const string Key = "key";
}

internal sealed record RawItem(
    long TimestampMs,
    string Kind,
    int X = 0,
    int Y = 0,
    SpikeClickType ClickType = SpikeClickType.Click,
    KeyboardInputKind KeyKind = KeyboardInputKind.Text,
    string? KeyName = null,
    string? ShortcutName = null);

internal sealed record TextKey(
    long TimestampMs,
    bool IsPassword,
    string? ProcessName,
    string? WindowTitle);

internal sealed record SpecialKeyPayload(string Key);

internal sealed record ShortcutPayload(string Shortcut);
