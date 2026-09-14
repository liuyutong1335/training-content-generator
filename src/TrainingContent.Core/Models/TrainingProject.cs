using System.Text.Json.Serialization;

namespace TrainingContent.Core.Models;

/// <summary>
/// TrainingProject 契約（phase0-contract.md §6）。
/// 教材として確定した内容の Source of Truth。project.json として保存する。
/// </summary>
public sealed class TrainingProject
{
    public int SchemaVersion { get; set; } = 1;
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
    public string? Objective { get; set; }
    public string? TargetAudience { get; set; }
    public List<string> Prerequisites { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int Revision { get; set; } = 1;
    public RecordingInfo? Recording { get; set; }
    public List<TrainingStep> Steps { get; set; } = [];
    public ProjectOutputs Outputs { get; set; } = new();
}

/// <summary>録画セッションの参照情報（契約 §7）。DeviceName は UI/Debug 用、再識別は DeviceId 優先。</summary>
public sealed class RecordingInfo
{
    public string MediaPath { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; }
    public long DurationMs { get; set; }
    public bool HasSystemAudio { get; set; }
    public bool HasMicrophone { get; set; }
    public string? DisplayId { get; set; }
    public string? DisplayName { get; set; }
    public string? SystemAudioDeviceId { get; set; }
    public string? SystemAudioDeviceName { get; set; }
    public string? MicrophoneDeviceId { get; set; }
    public string? MicrophoneDeviceName { get; set; }
}

/// <summary>生成成果物の参照と stale 判定用情報（契約 §16）。</summary>
public sealed class ProjectOutputs
{
    public GeneratedArtifact? ManualMarkdown { get; set; }
    public GeneratedArtifact? ManualHtml { get; set; }
    public GeneratedArtifact? TrainingVideo { get; set; }
}

public sealed class GeneratedArtifact
{
    public string Path { get; set; } = "";
    public DateTimeOffset GeneratedAtUtc { get; set; }
    public int SourceRevision { get; set; }
}
