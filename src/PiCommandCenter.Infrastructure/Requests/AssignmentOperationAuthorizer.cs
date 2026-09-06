using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>EF-backed authorization against retained assignments, sessions, and event history.</summary>
public sealed class AssignmentOperationAuthorizer(ControlPlaneDbContext db)
    : IAssignmentOperationAuthorizer
{
    private const int MaxClaimTokenLength = 128;
    private const int MaxSessionIdLength = 128;
    private const int MaxEventTypeLength = 64;
    private const int MaxHeartbeatSessionCount = 200;

    public async Task RequireActiveAsync(
        NodeId nodeId,
        WorkRequestId requestId,
        ProjectId projectId,
        string claimToken,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelationInputs(nodeId, requestId, projectId, claimToken, sessionId);

        var assignment = await GetAssignmentAsync(requestId, cancellationToken).ConfigureAwait(false);
        RequireAssignmentCorrelation(assignment, nodeId, projectId, claimToken);
        if (!IsActive(assignment.State))
        {
            throw Denied(AssignmentAuthorizationCodes.StateForbidden);
        }

        await RequireSessionCorrelationAsync(
                requestId,
                projectId,
                sessionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RequireHistoricalEventsAsync(
        NodeId nodeId,
        IReadOnlyList<AssignmentEventAuthorizationRequest> events,
        CancellationToken cancellationToken = default)
    {
        if (nodeId.Value == Guid.Empty || events is null)
        {
            throw Denied(AssignmentAuthorizationCodes.InvalidInput);
        }

        var batchRegistrations = new Dictionary<string, AssignmentCorrelation>(StringComparer.Ordinal);
        foreach (var @event in events)
        {
            await RequireHistoricalEventAsync(nodeId, @event, batchRegistrations, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RequireHistoricalEventAsync(
        NodeId nodeId,
        AssignmentEventAuthorizationRequest @event,
        Dictionary<string, AssignmentCorrelation> batchRegistrations,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ValidateCorrelationInputs(
            nodeId,
            @event.RequestId,
            @event.ProjectId,
            @event.ClaimToken,
            @event.SessionId);
        ValidateRequiredText(@event.EventId, AssignmentOperationLimits.MaxEventIdLength);
        ValidateRequiredText(@event.EventType, MaxEventTypeLength);

        var assignment = await GetAssignmentAsync(@event.RequestId, cancellationToken).ConfigureAwait(false);
        RequireAssignmentCorrelation(assignment, nodeId, @event.ProjectId, @event.ClaimToken);
        var correlation = new AssignmentCorrelation(@event.RequestId.Value, @event.ProjectId.Value);

        if (@event.SessionId is not null
            && batchRegistrations.TryGetValue(@event.SessionId, out var batchCorrelation)
            && batchCorrelation != correlation)
        {
            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }

        var knownEvent = await db.SessionEvents
            .AsNoTracking()
            .Where(candidate => candidate.EventId == @event.EventId)
            .Select(candidate => new EventSnapshot(
                candidate.NodeId,
                candidate.ProjectId,
                candidate.RequestId,
                candidate.SessionId,
                candidate.Type))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (knownEvent is not null)
        {
            if (!EventMatches(
                    knownEvent,
                    nodeId,
                    @event.RequestId,
                    @event.ProjectId,
                    @event.SessionId,
                    @event.EventType))
            {
                throw Denied(AssignmentAuthorizationCodes.EventMismatch);
            }

            return;
        }

        if (IsActive(assignment.State))
        {
            if (string.Equals(@event.EventType, "session.registered", StringComparison.Ordinal))
            {
                await RequireSessionRegistrationAsync(
                        @event.RequestId,
                        @event.ProjectId,
                        @event.SessionId,
                        batchRegistrations,
                        cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await RequireSessionCorrelationAsync(
                    @event.RequestId,
                    @event.ProjectId,
                    @event.SessionId,
                    batchRegistrations,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (assignment.State is ExecutionAssignmentState.RecoveryRequired)
        {
            await RequireRecordedSessionCorrelationAsync(
                    @event.RequestId,
                    @event.ProjectId,
                    @event.SessionId,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (!IsTerminal(assignment.State))
        {
            throw Denied(AssignmentAuthorizationCodes.StateForbidden);
        }

        if (!IsAllowedTerminalEvent(@event.EventType))
        {
            throw Denied(AssignmentAuthorizationCodes.EventTypeForbidden);
        }

        await RequireRecordedSessionCorrelationAsync(
                @event.RequestId,
                @event.ProjectId,
                @event.SessionId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> FilterHeartbeatSessionsAsync(
        NodeId nodeId,
        IReadOnlyCollection<string> sessionIds,
        CancellationToken cancellationToken = default)
    {
        if (nodeId.Value == Guid.Empty
            || sessionIds is null
            || sessionIds.Count > MaxHeartbeatSessionCount)
        {
            throw Denied(AssignmentAuthorizationCodes.InvalidInput);
        }

        var candidates = sessionIds.Take(MaxHeartbeatSessionCount + 1).ToArray();
        if (candidates.Length > MaxHeartbeatSessionCount)
        {
            throw Denied(AssignmentAuthorizationCodes.InvalidInput);
        }

        foreach (var sessionId in candidates)
        {
            ValidateRequiredText(sessionId, MaxSessionIdLength);
        }

        if (candidates.Length == 0)
        {
            return [];
        }

        // Two explicitly bounded queries: the join across the raw session Guid and the
        // value-converted WorkRequestId cannot be translated by EF, and the candidate
        // list is already capped at MaxHeartbeatSessionCount.
        var candidateSessions = await db.AgentSessions
            .AsNoTracking()
            .Where(session => candidates.Contains(session.Id))
            .Select(session => new SessionCorrelation(session.Id, session.RequestId, session.ProjectId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (candidateSessions.Count == 0)
        {
            return [];
        }

        var requestIds = candidateSessions
            .Select(session => new WorkRequestId(session.RequestId))
            .ToArray();
        var ownedAssignments = await db.ExecutionAssignments
            .AsNoTracking()
            .Where(assignment => assignment.NodeIdSnapshot == nodeId
                && requestIds.Contains(assignment.RequestId)
                && (assignment.State == ExecutionAssignmentState.Starting
                    || assignment.State == ExecutionAssignmentState.Running
                    || assignment.State == ExecutionAssignmentState.Finalizing
                    || assignment.State == ExecutionAssignmentState.Cancelling))
            .Select(assignment => new AssignmentCorrelation(
                assignment.RequestId.Value,
                assignment.ProjectId.Value))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var owned = ownedAssignments.ToHashSet();
        var ownedSessionIds = candidateSessions
            .Where(session => owned.Contains(
                new AssignmentCorrelation(session.RequestId, session.ProjectId)))
            .Select(session => session.Id)
            .ToHashSet(StringComparer.Ordinal);

        return candidates
            .Where(ownedSessionIds.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<AssignmentSnapshot> GetAssignmentAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken)
    {
        var assignment = await db.ExecutionAssignments
            .AsNoTracking()
            .Where(candidate => candidate.RequestId == requestId)
            .Select(candidate => new AssignmentSnapshot(
                candidate.ProjectId,
                candidate.NodeIdSnapshot,
                candidate.ClaimToken,
                candidate.State))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return assignment ?? throw Denied(AssignmentAuthorizationCodes.AssignmentMissing);
    }

    private static void RequireAssignmentCorrelation(
        AssignmentSnapshot assignment,
        NodeId nodeId,
        ProjectId projectId,
        string claimToken)
    {
        if (assignment.NodeId != nodeId)
        {
            throw Denied(AssignmentAuthorizationCodes.NodeMismatch);
        }

        if (!string.Equals(assignment.ClaimToken, claimToken, StringComparison.Ordinal))
        {
            throw Denied(AssignmentAuthorizationCodes.TokenMismatch);
        }

        if (assignment.ProjectId != projectId)
        {
            throw Denied(AssignmentAuthorizationCodes.ProjectMismatch);
        }
    }

    private async Task RequireSessionCorrelationAsync(
        WorkRequestId requestId,
        ProjectId projectId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId is null)
        {
            return;
        }

        await RequireRecordedSessionCorrelationAsync(requestId, projectId, sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RequireSessionCorrelationAsync(
        WorkRequestId requestId,
        ProjectId projectId,
        string? sessionId,
        IReadOnlyDictionary<string, AssignmentCorrelation> batchRegistrations,
        CancellationToken cancellationToken)
    {
        if (sessionId is null)
        {
            return;
        }

        var correlation = new AssignmentCorrelation(requestId.Value, projectId.Value);
        if (batchRegistrations.TryGetValue(sessionId, out var registeredCorrelation))
        {
            if (registeredCorrelation == correlation)
            {
                return;
            }

            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }

        await RequireRecordedSessionCorrelationAsync(requestId, projectId, sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task RequireSessionRegistrationAsync(
        WorkRequestId requestId,
        ProjectId projectId,
        string? sessionId,
        Dictionary<string, AssignmentCorrelation> batchRegistrations,
        CancellationToken cancellationToken)
    {
        if (sessionId is null)
        {
            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }

        var correlation = new AssignmentCorrelation(requestId.Value, projectId.Value);
        if (batchRegistrations.TryGetValue(sessionId, out var registeredCorrelation)
            && registeredCorrelation != correlation)
        {
            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }

        var recordedCorrelation = await db.AgentSessions
            .AsNoTracking()
            .Where(session => session.Id == sessionId)
            .Select(session => new AssignmentCorrelation(session.RequestId, session.ProjectId))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (recordedCorrelation is not null && recordedCorrelation != correlation)
        {
            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }

        batchRegistrations[sessionId] = correlation;
    }

    private async Task RequireRecordedSessionCorrelationAsync(
        WorkRequestId requestId,
        ProjectId projectId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId is null)
        {
            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }

        var matches = await db.AgentSessions
            .AsNoTracking()
            .AnyAsync(
                session => session.Id == sessionId
                    && session.RequestId == requestId.Value
                    && session.ProjectId == projectId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (!matches)
        {
            throw Denied(AssignmentAuthorizationCodes.SessionMismatch);
        }
    }

    private static void ValidateCorrelationInputs(
        NodeId nodeId,
        WorkRequestId requestId,
        ProjectId projectId,
        string claimToken,
        string? sessionId)
    {
        if (nodeId.Value == Guid.Empty
            || requestId.Value == Guid.Empty
            || projectId.Value == Guid.Empty)
        {
            throw Denied(AssignmentAuthorizationCodes.InvalidInput);
        }

        ValidateRequiredText(claimToken, MaxClaimTokenLength);
        if (sessionId is not null)
        {
            ValidateRequiredText(sessionId, MaxSessionIdLength);
        }
    }

    private static void ValidateRequiredText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            throw Denied(AssignmentAuthorizationCodes.InvalidInput);
        }
    }

    private static bool EventMatches(
        EventSnapshot @event,
        NodeId nodeId,
        WorkRequestId requestId,
        ProjectId projectId,
        string? sessionId,
        string eventType) =>
        @event.NodeId == nodeId.Value
        && @event.ProjectId == projectId.Value
        && @event.RequestId == requestId.Value
        && string.Equals(@event.SessionId, sessionId, StringComparison.Ordinal)
        && string.Equals(@event.Type, eventType, StringComparison.Ordinal);

    private static bool IsActive(ExecutionAssignmentState state) =>
        state is ExecutionAssignmentState.Starting
            or ExecutionAssignmentState.Running
            or ExecutionAssignmentState.Finalizing
            or ExecutionAssignmentState.Cancelling;

    private static bool IsTerminal(ExecutionAssignmentState state) =>
        state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;

    private static bool IsAllowedTerminalEvent(string eventType) =>
        eventType is "session.closed"
            or "session.failed"
            or "session.cancelled"
            or "session.completed"
            or "child.completed"
            or "tool.completed"
            or "turn.completed"
            or "request.completed"
            or "request.failed"
            or "request.cancelled";

    private static AssignmentAuthorizationException Denied(string code) => new(code);

    private sealed record AssignmentSnapshot(
        ProjectId ProjectId,
        NodeId NodeId,
        string ClaimToken,
        ExecutionAssignmentState State);

    private sealed record SessionCorrelation(string Id, Guid RequestId, Guid ProjectId);

    private sealed record AssignmentCorrelation(Guid RequestId, Guid ProjectId);

    private sealed record EventSnapshot(
        Guid NodeId,
        Guid ProjectId,
        Guid? RequestId,
        string? SessionId,
        string Type);
}
