namespace PiCommandCenter.Infrastructure.Persistence;

/// <summary>
/// Persisted row of the current <c>AgentSession</c> projection (SPEC §29). Written only by the
/// transactional reducer that applies normalized events after their <see cref="SessionEvent"/>
/// insert; the aggregate invariants (independent status dimensions, monotone
/// <see cref="LastSequence"/>, optimistic <see cref="Version"/>) live in the domain aggregate.
/// </summary>
public sealed class AgentSessionRow
{
    public string Id { get; init; } = string.Empty;

    public Guid ProjectId { get; init; }

    public Guid RequestId { get; init; }

    public string? ParentSessionId { get; init; }

    public string AgentName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Runtime { get; init; } = string.Empty;

    public string RuntimeProfile { get; init; } = string.Empty;

    public string? ProviderSessionId { get; set; }

    public string Liveness { get; set; } = string.Empty;

    public string Activity { get; set; } = string.Empty;

    public string Attention { get; set; } = string.Empty;

    public string WorkState { get; set; } = string.Empty;

    public string StatusReason { get; set; } = string.Empty;

    public string? CurrentOperation { get; set; }

    public int? ProcessId { get; set; }

    public long StartedAtUtcTicks { get; init; }

    public long? LastHeartbeatAtUtcTicks { get; set; }

    public long? EndedAtUtcTicks { get; set; }

    public long LastSequence { get; set; }

    public long Version { get; set; }
}
