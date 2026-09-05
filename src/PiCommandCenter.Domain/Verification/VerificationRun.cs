using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Verification;

/// <summary>
/// One persisted verification command execution for a work request (SPEC §20, §29 VerificationRun).
/// </summary>
public sealed class VerificationRun
{
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
        bool mandatory)
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
            mandatory);
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
