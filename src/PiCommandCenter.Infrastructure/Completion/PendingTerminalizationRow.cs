using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Infrastructure.Completion;

/// <summary>
/// Durable Complete/Fail terminalization intent accepted at BeginTerminalization.
/// Keyed by RequestId so recovery can replay the exact accepted intent after restart.
/// </summary>
public sealed class PendingTerminalizationRow
{
    public const int MaxClaimTokenLength = 128;
    public const int MaxRootSessionIdLength = 128;
    public const int MaxIntentLength = 32;
    public const int MaxCompletionEvidenceJsonLength = 16384;
    public const int MaxReasonLength = 1024;

    public WorkRequestId RequestId { get; init; }

    public ProjectId ProjectId { get; init; }

    public NodeId NodeId { get; init; }

    public string ClaimToken { get; init; } = string.Empty;

    public string? RootSessionId { get; init; }

    public string Intent { get; init; } = string.Empty;

    public string? CompletionEvidenceJson { get; init; }

    public string? Reason { get; init; }

    public long AcceptedAtUtcTicks { get; init; }

    public long Version { get; set; }
}
