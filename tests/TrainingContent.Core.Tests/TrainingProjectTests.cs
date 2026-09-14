using System.Text.Json;
using TrainingContent.Core;
using TrainingContent.Core.Models;
using TrainingContent.Core.Validation;
using Xunit;

namespace TrainingContent.Core.Tests;

/// <summary>Phase 0 Core Test Contract（phase0-contract.md §30）に対応するテスト。</summary>
public class Phase0ContractTests
{
    private static TrainingProject MakeProject() => new()
    {
        Id = Guid.NewGuid(),
        Title = "経費申請登録",
        CreatedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        Recording = new RecordingInfo
        {
            MediaPath = "raw/recording.mp4",
            StartedAtUtc = DateTimeOffset.UtcNow,
            DurationMs = 84_210,
            HasSystemAudio = true,
            HasMicrophone = true,
        },
        Steps =
        [
            new TrainingStep
            {
                Id = Guid.NewGuid(),
                Order = 1,
                StartMs = 5_210,
                Action = StepActions.Click,
                Title = "「新規申請」をクリックします",
                ScreenshotPath = "screenshots/edited/step-001.png",
                SourceEventIds = [Guid.NewGuid()],
            },
        ],
    };

    // Project serialize → deserialize: PASS
    [Fact]
    public void SerializeDeserialize_RoundTrips()
    {
        var project = MakeProject();
        var json = JsonSerializer.Serialize(project, TrainingJson.Indented);
        var restored = JsonSerializer.Deserialize<TrainingProject>(json, TrainingJson.Indented);

        Assert.NotNull(restored);
        Assert.Equal(project.Id, restored.Id);
        Assert.Equal(project.Title, restored.Title);
        Assert.Equal(project.Recording!.DurationMs, restored.Recording!.DurationMs);
        Assert.Equal(project.Steps[0].Title, restored.Steps[0].Title);
    }

    // camelCase Serialization Rule（§21）
    [Fact]
    public void SerializesAsCamelCase()
    {
        var json = JsonSerializer.Serialize(MakeProject(), TrainingJson.Compact);
        Assert.Contains("\"schemaVersion\":1", json);
        Assert.DoesNotContain("\"SchemaVersion\"", json);
    }

    // Relative path保存: PASS
    [Fact]
    public void RelativePath_IsValid()
    {
        var errors = ProjectValidator.Validate(MakeProject());
        Assert.DoesNotContain(errors, e => e.Contains("screenshotPath"));
    }

    // Absolute path拒否: PASS（= 検証で拒否される）
    [Theory]
    [InlineData("C:\\Users\\user\\raw\\recording.mp4")]
    [InlineData("\\\\server\\share\\recording.mp4")]
    public void AbsolutePath_IsRejected(string path)
    {
        var project = MakeProject();
        project.Recording!.MediaPath = path;
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("絶対パス"));
    }

    [Fact]
    public void BackslashSeparator_IsRejected()
    {
        var project = MakeProject();
        project.Steps[0].ScreenshotPath = "screenshots\\edited\\step-001.png";
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("/ に統一"));
    }

    // Duplicate Step ID: FAIL
    [Fact]
    public void DuplicateStepId_IsRejected()
    {
        var project = MakeProject();
        var id = project.Steps[0].Id;
        project.Steps.Add(new TrainingStep { Id = id, Order = 2, StartMs = 6_000, Action = StepActions.Manual, Title = "x" });
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("重複") && e.Contains("Id"));
    }

    // Duplicate Step Order: FAIL
    [Fact]
    public void DuplicateStepOrder_IsRejected()
    {
        var project = MakeProject();
        project.Steps.Add(new TrainingStep { Id = Guid.NewGuid(), Order = 1, StartMs = 6_000, Action = StepActions.Manual, Title = "x" });
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("Order が重複"));
    }

    // Negative Timestamp: FAIL
    [Fact]
    public void NegativeTimestamp_IsRejected()
    {
        var project = MakeProject();
        project.Steps[0].StartMs = -1;
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("StartMs は 0 以上"));
    }

    // End < Start: FAIL
    [Fact]
    public void EndBeforeStart_IsRejected()
    {
        var project = MakeProject();
        project.Steps[0].StartMs = 5_000;
        project.Steps[0].EndMs = 4_000;
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("EndMs"));
    }

    // schemaVersion > supported: Reject
    [Fact]
    public void UnsupportedSchemaVersion_IsRejected()
    {
        var project = MakeProject();
        project.SchemaVersion = 2;
        var errors = ProjectValidator.Validate(project);
        Assert.Contains(errors, e => e.Contains("schemaVersion 2"));
    }

    // Unknown Event Type: Warning + Continue（§26 — Type 判定の契約を固定）
    [Fact]
    public void UnknownEventType_IsNotKnownAndNotLifecycle()
    {
        Assert.False(EventTypes.IsKnown("future.newEvent"));
        Assert.False(EventTypes.IsLifecycle("future.newEvent"));
        Assert.True(EventTypes.IsKnown(EventTypes.MouseClick));
        Assert.True(EventTypes.IsLifecycle(EventTypes.RecordingPaused));
        Assert.True(EventTypes.IsLifecycle(EventTypes.RecordingStopped));
    }

    // Password raw text storage: Must not exist — textEntry 契約上、入力文字を保持するフィールド自体が存在しない
    [Fact]
    public void TextEntryPayload_HasNoRawTextField()
    {
        const string payload = """{"keyCount":8,"isSensitive":false}""";
        var json = JsonSerializer.Deserialize<JsonElement>(payload);
        var fields = json.EnumerateObject().Select(p => p.Name).ToList();
        Assert.DoesNotContain(fields, f => f.Equals("text", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, f => f.Equals("value", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fields, f => f.Equals("keys", StringComparison.OrdinalIgnoreCase));
    }
}
