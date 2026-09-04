namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Command to enqueue a new work request. The owning project is supplied separately to
/// <see cref="IRequestQueue.EnqueueAsync"/>.
/// </summary>
public sealed record QueueWorkRequestCommand(
    Domain.Requests.WorkRequestKind Kind,
    Domain.Requests.RequestPriority Priority,
    Domain.Requests.RiskLevel RiskLevel,
    string Title,
    string Prompt);
