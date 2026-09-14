// Ported from OpenSteps (MIT License)
// Repository: https://github.com/ebanez8/openstep
// Source file: src/OpenSteps.Core/Models/UiElementInfo.cs (ScreenBounds / UiAutomationQuality included)
// Commit: 8058980865ac07f261b97b7270776c486b942a16
// Modification: namespace changed; model split into local records for this module.
namespace TrainingContent.EventCapture.UiAutomation;

public enum UiAutomationQuality
{
    UsefulElementFound,
    GenericContainerOnly,
    UiAutomationFailed
}

public sealed record ScreenBounds(int X, int Y, int Width, int Height);

public sealed record UiElementInfo(
    string? Name,
    string? AutomationId,
    string? ControlType,
    string? ClassName,
    ScreenBounds? Bounds,
    string? ParentName,
    UiAutomationQuality Quality,
    bool UsefulElementFound,
    string RawElementDebug,
    string ParentChainDebug,
    string CandidateElementsDebug,
    bool IsEditable,
    bool IsPassword,
    bool IsKeyboardFocusable);
