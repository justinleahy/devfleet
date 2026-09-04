using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Runtime-neutral adapter contract, exactly SPEC §28. Implementations convert their native
/// activity into <see cref="NormalizedAgentEvent"/> envelopes; capabilities, not version checks,
/// determine available controls.
/// </summary>
public interface IAgentRuntimeAdapter
{
    string RuntimeKind { get; }

    AgentRuntimeCapabilities Capabilities { get; }

    Task<AgentSessionHandle> StartAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<NormalizedAgentEvent> WatchAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task SendAsync(
        string sessionId,
        AgentInput input,
        CancellationToken cancellationToken);

    Task CancelAsync(
        string sessionId,
        CancellationToken cancellationToken);

    Task<AgentRuntimeSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken);
}
