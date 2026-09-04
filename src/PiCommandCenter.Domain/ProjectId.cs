namespace PiCommandCenter.Domain;

/// <summary>
/// Strongly-typed identifier for a Project aggregate.
/// </summary>
public readonly record struct ProjectId
{
    public ProjectId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Project id must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static ProjectId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
