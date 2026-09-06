using System.ComponentModel.DataAnnotations;

namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// One-argument hub command for recording a verification run.
/// Correlation, request, project, assignment token, and session identifiers are required.
/// </summary>
public sealed record VerificationRunMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
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

/// <summary>Recorded verification run returned without the assignment claim token.</summary>
public sealed record VerificationRunResultMessage(
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
