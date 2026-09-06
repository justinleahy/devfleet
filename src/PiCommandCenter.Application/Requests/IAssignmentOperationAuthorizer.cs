using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>Stable, secret-free reasons an assignment-scoped operation was denied.</summary>
public static class AssignmentAuthorizationCodes
{
    public const string InvalidInput = "invalid_input";
    public const string AssignmentMissing = "assignment_missing";
    public const string NodeMismatch = "node_mismatch";
    public const string TokenMismatch = "token_mismatch";
    public const string ProjectMismatch = "project_mismatch";
    public const string StateForbidden = "state_forbidden";
    public const string SessionMismatch = "session_mismatch";
    public const string EventMismatch = "event_mismatch";
    public const string EventTypeForbidden = "event_type_forbidden";
}

/// <summary>Protocol bounds for assignment-authorized node operations.</summary>
public static class AssignmentOperationLimits
{
    /// <summary>
    /// Accommodates the supervisor format <c>{sessionId}-{sequence}-{eventType}</c> at the
    /// producer bounds: 128 session characters, a 19-digit non-negative sequence, two
    /// separators, and 64 event-type characters.
    /// </summary>
    public const int MaxEventIdLength = 256;
}

/// <summary>Raised when a node operation does not correlate to its retained assignment.</summary>
public sealed class AssignmentAuthorizationException(string code)
    : InvalidOperationException("The assignment does not authorize this operation.")
{
    /// <summary>A stable, secret-free denial reason.</summary>
    public string Code { get; } = code;
}

/// <summary>
/// Authorizes node operations against retained execution assignments without changing store state.
/// </summary>
public interface IAssignmentOperationAuthorizer
{
    /// <summary>Requires an active assignment and, when supplied, a recorded correlated session.</summary>
    Task RequireActiveAsync(
        NodeId nodeId,
        WorkRequestId requestId,
        ProjectId projectId,
        string claimToken,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes an ordered event batch. An active assignment's <c>session.registered</c>
    /// event establishes correlation for later events in the same batch; terminal history is
    /// limited to recorded sessions and explicitly allowed final events.
    /// </summary>
    Task RequireHistoricalEventsAsync(
        NodeId nodeId,
        IReadOnlyList<AssignmentEventAuthorizationRequest> events,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the supplied session ids owned by active assignments on the node.</summary>
    Task<IReadOnlyList<string>> FilterHeartbeatSessionsAsync(
        NodeId nodeId,
        IReadOnlyCollection<string> sessionIds,
        CancellationToken cancellationToken = default);
}

/// <summary>Assignment correlation carried by one event awaiting authorization.</summary>
public sealed record AssignmentEventAuthorizationRequest(
    WorkRequestId RequestId,
    ProjectId ProjectId,
    string ClaimToken,
    string? SessionId,
    string EventId,
    string EventType);
