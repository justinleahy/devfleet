namespace PiCommandCenter.Domain.Requests;

/// <summary>
/// Explicit numeric queue priority ordering: higher value dequeues first.
/// </summary>
public enum RequestPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Urgent = 3,
}
