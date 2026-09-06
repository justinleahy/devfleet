
namespace PiCommandCenter.ControlPlane.RuntimeRouting;

/// <summary>Tracks the current authenticated SignalR connection for each registered node.</summary>
public sealed class NodeConnectionDirectory
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, string> _connectionByNode = [];
    private readonly Dictionary<string, Guid> _nodeByConnection = new(StringComparer.Ordinal);

    public void Bind(Guid nodeId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
        {
            if (_nodeByConnection.TryGetValue(connectionId, out var previousNode))
            {
                if (previousNode != nodeId)
                {
                    throw new InvalidOperationException(
                        $"Connection '{connectionId}' is already registered as node '{previousNode}'.");
                }

                return;
            }
            if (_connectionByNode.TryGetValue(nodeId, out var previousConnection))
            {
                _nodeByConnection.Remove(previousConnection);
            }
            _connectionByNode[nodeId] = connectionId;
            _nodeByConnection[connectionId] = nodeId;
        }
    }

    public string? Find(Guid nodeId)
    {
        lock (_gate)
        {
            return _connectionByNode.GetValueOrDefault(nodeId);
        }
    }

    public bool IsBound(Guid nodeId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (_gate)
        {
            return _nodeByConnection.TryGetValue(connectionId, out var registeredNodeId)
                && registeredNodeId == nodeId;
        }
    }

    public void Unbind(string connectionId)
    {
        lock (_gate)
        {
            if (!_nodeByConnection.Remove(connectionId, out var nodeId))
            {
                return;
            }
            if (_connectionByNode.TryGetValue(nodeId, out var current)
                && string.Equals(current, connectionId, StringComparison.Ordinal))
            {
                _connectionByNode.Remove(nodeId);
            }
        }
    }
}
