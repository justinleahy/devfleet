using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Application.Sessions;

/// <summary>
/// Read model of an agent session projection (SPEC §21, §29): the session's identity plus its
/// four independent status dimensions and the reason/operation behind them.
/// </summary>
public sealed record AgentSessionDto(
    string Id,
    Guid ProjectId,
    Guid RequestId,
    string? ParentSessionId,
    string AgentName,
    string Role,
    string Runtime,
    string Model,
    string? ProviderSessionId,
    AgentLiveness Liveness,
    AgentActivity Activity,
    AgentAttention Attention,
    AgentWorkState WorkState,
    string StatusReason,
    string? CurrentOperation,
    int? ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset? LastHeartbeatAt,
    DateTimeOffset? EndedAt);
