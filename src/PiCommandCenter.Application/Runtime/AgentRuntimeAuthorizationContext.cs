namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Host-owned reservation grant attached to a start request. Never taken from model input.
/// </summary>
public sealed record AgentRuntimeAuthorizationContext(Guid LeaseId, long FencingToken);
