namespace PiCommandCenter.Domain;

/// <summary>
/// Strongly-typed identifier for a node that hosts projects.
/// </summary>
public readonly record struct NodeId
{
    public NodeId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Node id must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static NodeId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
