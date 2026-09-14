namespace TrainingContent.Core.Models;

/// <summary>
/// TrainingStep 契約（phase0-contract.md §12）。
/// TimelineEvent から生成された教材用手順データ。Manual / Video の共通入力。
/// Raw Event とは別物（§2.3）。Review UI で編集可能。
/// </summary>
public sealed class TrainingStep
{
    public Guid Id { get; set; }
    public int Order { get; set; }
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Caution { get; set; }
    public string? ExpectedResult { get; set; }
    public string? ScreenshotPath { get; set; }
    public List<Guid> SourceEventIds { get; set; } = [];
}

/// <summary>TrainingStep.Action の MVP 定義（契約 §13）。</summary>
public static class StepActions
{
    public const string Click = "click";
    public const string DoubleClick = "doubleClick";
    public const string RightClick = "rightClick";
    public const string TextEntry = "textEntry";
    public const string SpecialKey = "specialKey";
    public const string Shortcut = "shortcut";

    /// <summary>Review UI からユーザーが追加した Step。sourceEventIds = []。</summary>
    public const string Manual = "manual";
}
