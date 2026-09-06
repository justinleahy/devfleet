using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Verification;

/// <summary>
/// One persisted verification command execution for a work request (SPEC §20, §29 VerificationRun).
/// Every run identifies the exact final or intermediate fingerprint, policy revision, kind, and attempt.
/// </summary>
public sealed class VerificationRun
{
    public const int MaxFingerprintLength = 256;
    public const int MaxPolicyRevisionLength = 128;

    private VerificationRun(
        Guid id,
        WorkRequestId requestId,
        string profileId,
        string commandId,
        VerificationRunStatus status,
        int? exitCode,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? outputSummary,
        string? outputArtifactPath,
        bool mandatory,
        string fingerprint,
        string policyRevision,
        VerificationRunKind runKind,
        Guid attemptId)
    {
        Id = id;
        RequestId = requestId;
        ProfileId = profileId;
        CommandId = commandId;
        Status = status;
        ExitCode = exitCode;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        OutputSummary = outputSummary;
        OutputArtifactPath = outputArtifactPath;
        Mandatory = mandatory;
        Fingerprint = fingerprint;
        PolicyRevision = policyRevision;
        RunKind = runKind;
        AttemptId = attemptId;
    }

    public Guid Id { get; }

    public WorkRequestId RequestId { get; }

    public string ProfileId { get; }

    public string CommandId { get; }

    public VerificationRunStatus Status { get; }

    public int? ExitCode { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? CompletedAt { get; }

    public string? OutputSummary { get; }

    public string? OutputArtifactPath { get; }

    public bool Mandatory { get; }

    public string Fingerprint { get; }

    public string PolicyRevision { get; }

    public VerificationRunKind RunKind { get; }

    public Guid AttemptId { get; }

    public bool IsGreen => Status == VerificationRunStatus.Passed;

    public static VerificationRun Record(
        WorkRequestId requestId,
        string profileId,
        string commandId,
        VerificationRunStatus status,
        int? exitCode,
        DateTimeOffset startedAt,
        DateTimeOffset? completedAt,
        string? outputSummary,
        string? outputArtifactPath,
        bool mandatory,
        string fingerprint,
        string policyRevision,
        VerificationRunKind runKind,
        Guid attemptId,
        Guid? id = null)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("Profile id must not be empty.", nameof(profileId));
        }

        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id must not be empty.", nameof(commandId));
        }

        if (completedAt is { } done && done < startedAt)
        {
            throw new ArgumentException("CompletedAt cannot precede StartedAt.", nameof(completedAt));
        }

        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            throw new ArgumentException("Fingerprint must not be empty.", nameof(fingerprint));
        }

        var cleanFingerprint = fingerprint.Trim();
        if (cleanFingerprint.Length > MaxFingerprintLength)
        {
            throw new ArgumentException(
                $"Fingerprint must not exceed {MaxFingerprintLength} characters.",
                nameof(fingerprint));
        }

        if (string.IsNullOrWhiteSpace(policyRevision))
        {
            throw new ArgumentException("Policy revision must not be empty.", nameof(policyRevision));
        }

        var cleanPolicyRevision = policyRevision.Trim();
        if (cleanPolicyRevision.Length > MaxPolicyRevisionLength)
        {
            throw new ArgumentException(
                $"Policy revision must not exceed {MaxPolicyRevisionLength} characters.",
                nameof(policyRevision));
        }

        if (!Enum.IsDefined(runKind))
        {
            throw new ArgumentOutOfRangeException(nameof(runKind), runKind, "Unknown verification run kind.");
        }

        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt id must not be empty.", nameof(attemptId));
        }

        return new VerificationRun(
            id is { } guid && guid != Guid.Empty ? guid : Guid.NewGuid(),
            requestId,
            profileId.Trim(),
            commandId.Trim(),
            status,
            exitCode,
            startedAt,
            completedAt,
            Truncate(outputSummary, 16_384),
            Truncate(outputArtifactPath, 1024),
            mandatory,
            cleanFingerprint,
            cleanPolicyRevision,
            runKind,
            attemptId);
    }

    private static string? Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return value.Length <= max ? value : value[..max];
    }
}
