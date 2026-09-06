using System.ComponentModel.DataAnnotations;

namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Bounded replay of persisted verification runs.</summary>
public static class VerificationReplayLimits
{
    public const int MaxRuns = 256;
}

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
    bool Mandatory,
    [property: Required, MaxLength(256)] string Fingerprint,
    [property: Required, MaxLength(256)] string PolicyRevision,
    int RunKind,
    string RunKindName,
    Guid AttemptId);

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
    bool Mandatory,
    [property: Required, MaxLength(256)] string Fingerprint,
    [property: Required, MaxLength(256)] string PolicyRevision,
    int RunKind,
    string RunKindName,
    Guid AttemptId);

/// <summary>Authenticated list of persisted verification runs for one assignment.</summary>
public sealed record ListVerificationRunsMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string SessionId);

/// <summary>Bounded replay row without output artifacts or assignment secrets.</summary>
public sealed record VerificationRunReplayMessage(
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
    bool Mandatory,
    [property: Required, MaxLength(256)] string Fingerprint,
    [property: Required, MaxLength(256)] string PolicyRevision,
    int RunKind,
    string RunKindName,
    Guid AttemptId);

/// <summary>Bounded newest-first replay envelope.</summary>
public sealed record VerificationRunReplayListMessage(
    Guid CorrelationId,
    Guid ProjectId,
    Guid RequestId,
    string SessionId,
    int Limit,
    VerificationRunReplayMessage[] Runs);