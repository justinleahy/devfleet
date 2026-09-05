namespace PiCommandCenter.Application.Completion;

/// <summary>
/// Gate outcome. Rejection lists every missing criterion; acceptance includes the persisted result.
/// </summary>
public sealed record CompletionGateDecision(
    bool Accepted,
    IReadOnlyList<string> MissingRequirements,
    RequestResultDto? Result);
