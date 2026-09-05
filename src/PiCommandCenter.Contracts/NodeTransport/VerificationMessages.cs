namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// One-argument hub payload for recording a verification command run.
/// Correlation, request, project, and session identifiers are required.
/// </summary>
public sealed record VerificationRunMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    string SessionId,
    Guid Id,
    string ProfileId,
    string CommandId,
    int Status,
    string StatusName,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? OutputSummary,
    string? OutputArtifactPath,
    bool Mandatory);
