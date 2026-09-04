using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Point-in-time view of a session's independent status dimensions as reported by the runtime.
/// </summary>
public sealed record AgentRuntimeSnapshot(
    string SessionId,
    string RuntimeKind,
    AgentLiveness Liveness,
    AgentActivity Activity,
    AgentAttention Attention,
    AgentWorkState WorkState,
    string StatusReason,
    string? CurrentOperation,
    string? ProviderSessionId,
    long? LastSequence,
    DateTimeOffset? LastHeartbeatAt);
