using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Nodes;

/// <summary>Thrown when an operation references a node that is not registered.</summary>
public sealed class NodeNotFoundException : Exception
{
    public NodeNotFoundException(NodeId id)
        : base($"Node '{id}' is not registered.")
    {
        Id = id;
    }

    public NodeId Id { get; }
}
