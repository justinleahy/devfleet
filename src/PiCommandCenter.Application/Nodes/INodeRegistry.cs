using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Registry surface for fleet nodes: registration, heartbeats, and reads for UI surfaces.
/// </summary>
public interface INodeRegistry
{
    /// <summary>Lists all known nodes ordered by display name.</summary>
    Task<IReadOnlyList<NodeDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a node by id. Returns null when no node with the given id is registered.
    /// </summary>
    Task<NodeDto?> GetAsync(NodeId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers or re-registers a node. An existing node keeps its identity and is refreshed.
    /// </summary>
    /// <exception cref="ArgumentException">The command violates a node invariant.</exception>
    Task<NodeDto> RegisterAsync(RegisterNodeCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a heartbeat. Throws <see cref="NodeNotFoundException"/> for an unregistered node.
    /// </summary>
    /// <exception cref="ArgumentException">Active session ids are not valid.</exception>
    Task<NodeDto> HeartbeatAsync(NodeHeartbeatCommand command, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the node offline after a missed heartbeat window. No-op when already offline or unknown.
    /// </summary>
    Task MarkStaleOfflineAsync(NodeId id, DateTimeOffset at, CancellationToken cancellationToken = default);
}
