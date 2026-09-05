using PiCommandCenter.Domain.Verification;

namespace PiCommandCenter.Application.Verification;

/// <summary>Persisted verification command run (SPEC §29 VerificationRun).</summary>
public sealed record VerificationRunDto(
    Guid Id,
    Guid RequestId,
    string ProfileId,
    string CommandId,
    VerificationRunStatus Status,
    int? ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? OutputSummary,
    string? OutputArtifactPath,
    bool Mandatory);
