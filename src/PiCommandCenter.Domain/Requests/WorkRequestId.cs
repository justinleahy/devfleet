namespace PiCommandCenter.Domain.Requests;

/// <summary>
/// Strongly-typed identifier for a WorkRequest aggregate.
/// </summary>
public readonly record struct WorkRequestId
{
    public WorkRequestId(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("Work request id must not be empty.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static WorkRequestId New() => new(Guid.NewGuid());

    public override string ToString() => Value.ToString();
}
