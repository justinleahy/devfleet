namespace PiCommandCenter.Infrastructure.Verification;

/// <summary>EF row for SPEC §29 VerificationRun.</summary>
public sealed class VerificationRunRow
{
    public Guid Id { get; init; }

    public Guid RequestId { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public string CommandId { get; init; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public int? ExitCode { get; set; }

    public long StartedAtUtcTicks { get; init; }

    public long? CompletedAtUtcTicks { get; set; }

    public string? OutputSummary { get; set; }

    public string? OutputArtifactPath { get; set; }

    public bool Mandatory { get; init; }

    public string Fingerprint { get; init; } = string.Empty;

    public string PolicyRevision { get; init; } = string.Empty;

    public string RunKind { get; init; } = string.Empty;

    public Guid AttemptId { get; init; }
}
