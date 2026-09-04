namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Handle returned by <see cref="IAgentRuntimeAdapter.StartAsync"/>; binds the orchestrator
/// session id to the runtime's provider session id.
/// </summary>
public sealed record AgentSessionHandle
{
    public AgentSessionHandle(
        string sessionId,
        string? providerSessionId,
        string runtimeKind,
        DateTimeOffset startedAt)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Trim().Length == 0)
        {
            throw new ArgumentException("Session id must not be empty.", nameof(sessionId));
        }

        if (string.IsNullOrWhiteSpace(runtimeKind) || runtimeKind.Trim().Length == 0)
        {
            throw new ArgumentException("Runtime kind must not be empty.", nameof(runtimeKind));
        }

        SessionId = sessionId.Trim();
        ProviderSessionId = string.IsNullOrWhiteSpace(providerSessionId) ? null : providerSessionId.Trim();
        RuntimeKind = runtimeKind.Trim();
        StartedAt = startedAt;
    }

    /// <summary>Orchestrator-assigned session id.</summary>
    public string SessionId { get; }

    /// <summary>The runtime's own session identifier, when reported at start.</summary>
    public string? ProviderSessionId { get; }

    public string RuntimeKind { get; }

    public DateTimeOffset StartedAt { get; }
}
