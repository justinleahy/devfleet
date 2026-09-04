namespace PiCommandCenter.Domain.Requests;

/// <summary>
/// Lifecycle states of a work request.
/// </summary>
public enum WorkRequestStatus
{
    Queued = 0,
    Starting = 1,
    Planning = 2,
    Executing = 3,
    Reviewing = 4,
    Verifying = 5,
    Blocked = 6,
    Completed = 7,
    Failed = 8,
    Cancelled = 9,
}
