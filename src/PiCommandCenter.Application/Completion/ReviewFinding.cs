namespace PiCommandCenter.Application.Completion;

/// <summary>One review finding submitted with completion evidence.</summary>
public sealed record ReviewFinding(
    string Id,
    string Summary,
    bool Blocking,
    bool Resolved,
    bool UserOverridden);
