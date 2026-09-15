using TrainingContent.Core.Models;

namespace TrainingContent.Manual.Tests;

/// <summary>
/// Manual テスト専用のデータ組み立てヘルパー（汎用 TestData は作らない）。
/// ProjectValidator を通過する最小構成の TrainingProject / TrainingStep を作る。
/// </summary>
internal static class ManualTestData
{
    public const string DefaultTitle = "経費申請登録";
    public const string DefaultObjective = "経費申請を登録できるようになる";
    public const string DefaultTargetAudience = "新入社員";

    /// <summary>ProjectValidator を通過する TrainingProject。steps を省略すると Steps 0 件になる。</summary>
    public static TrainingProject Project(params TrainingStep[] steps) => new()
    {
        SchemaVersion = 1,
        Id = Guid.Parse("ac01e763-f98b-4458-bb63-68de71252121"),
        Title = DefaultTitle,
        Objective = DefaultObjective,
        TargetAudience = DefaultTargetAudience,
        Prerequisites = ["PC の基本操作", "社内ネットワークへの接続"],
        CreatedAtUtc = new DateTimeOffset(2026, 9, 14, 5, 30, 0, TimeSpan.Zero),
        UpdatedAtUtc = new DateTimeOffset(2026, 9, 14, 5, 42, 0, TimeSpan.Zero),
        Revision = 3,
        Recording = new RecordingInfo
        {
            MediaPath = "raw/recording.mp4",
            StartedAtUtc = new DateTimeOffset(2026, 9, 14, 5, 31, 0, TimeSpan.Zero),
            DurationMs = 84_210,
            HasSystemAudio = true,
            HasMicrophone = true,
        },
        Steps = [.. steps],
        Outputs = new ProjectOutputs(),
    };

    /// <summary>全項目が埋まった Step。ProjectValidator を通過する（Order は 1..N の連番で渡すこと）。</summary>
    public static TrainingStep Step(int order, string title, string? screenshotPath = null) => new()
    {
        Id = Guid.NewGuid(),
        Order = order,
        StartMs = 5_000 + (order * 1_000),
        EndMs = null,
        Action = StepActions.Click,
        Target = "新規申請",
        Title = title,
        Description = $"{title} の説明。",
        Caution = $"{title} の注意点。",
        ExpectedResult = $"{title} の期待結果。",
        ScreenshotPath = screenshotPath,
        SourceEventIds = [Guid.NewGuid()],
    };
}
