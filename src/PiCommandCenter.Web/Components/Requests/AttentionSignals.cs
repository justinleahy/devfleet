using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;

namespace PiCommandCenter.Web.Components.Requests;

/// <summary>The attention-inbox categories of SPEC §31.6, in the order an operator triages them.</summary>
public enum AttentionKind
{
    /// <summary>An agent reported <see cref="AgentAttention.InputRequired"/>.</summary>
    HumanInput,

    /// <summary>An agent reported <see cref="AgentAttention.ApprovalRequired"/>.</summary>
    Approval,

    /// <summary>Provider-native authentication is missing (SPEC §33.4).</summary>
    ProviderAuthMissing,

    /// <summary>An agent reported <see cref="AgentAttention.ReservationConflict"/>.</summary>
    ReservationConflict,

    /// <summary>A handoff was requested and no transfer or release has answered it.</summary>
    HandoffRequested,

    /// <summary>
    /// A mandatory baseline or project-check command of the current fingerprint finished red.
    /// </summary>
    VerificationFailed,

    /// <summary>The repository changed outside every lease of this request.</summary>
    ExternalChange,

    /// <summary>A disconnected or exited session still owns a lease group.</summary>
    DisconnectedWithLease,

    /// <summary>A lease group is quarantined in recovery-required and blocks its scopes.</summary>
    RecoveryRequired,

    /// <summary>An agent reported an error or warning that is not one of the categories above.</summary>
    AgentError,
}

/// <summary>How loudly a signal is rendered; mirrors the Fluent message-bar intents in use.</summary>
public enum AttentionSeverity
{
    /// <summary>Work is blocked or a guarantee is broken.</summary>
    Error,

    /// <summary>Human judgement is needed before the fleet can move on.</summary>
    Warning,
}

/// <summary>
/// One actionable attention item, always traceable to a persisted fact: a session projection
/// field, a lease row, a verification run, or a persisted event payload.
/// </summary>
/// <param name="Evidence">
/// The persisted fact behind the signal — event type, lease id, or run id — shown verbatim so an
/// operator can find it in the timeline.
/// </param>
public sealed record AttentionSignal(
    AttentionKind Kind,
    AttentionSeverity Severity,
    string Title,
    string Detail,
    DateTimeOffset ObservedAt,
    Guid ProjectId,
    string ProjectName,
    Guid RequestId,
    string RequestTitle,
    string? SessionId,
    string Evidence);

/// <summary>
/// Derives the attention inbox of one request from its persisted projections. Every rule reads a
/// stored value; nothing is inferred from silence, and a request with no stored problem produces
/// no signal.
/// </summary>
public static class AttentionScanner
{
    /// <summary>
    /// Substrings that mark a recorded status reason as provider authentication (SPEC §33.4). No
    /// adapter emits a dedicated code today, so the recorded text is matched and then shown
    /// verbatim rather than replaced with an invented diagnosis.
    /// </summary>
    private static readonly string[] ProviderAuthMarkers =
    [
        "authenticat",
        "not logged in",
        "log in",
        "login",
        "credential",
        "api key",
    ];

    /// <summary>Scans one request's projections and appends every signal it proves.</summary>
    public static void Scan(
        List<AttentionSignal> into,
        WorkRequestDto request,
        string projectName,
        IReadOnlyList<AgentSessionDto> sessions,
        IReadOnlyList<ReservationLeaseDto> leases,
        IReadOnlyList<VerificationRunDto> verificationRuns,
        RequestInsights insights,
        DateTimeOffset now,
        IReadOnlyList<SessionEventDto>? events = null)
    {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(insights);

        ScanSessions(into, request, projectName, sessions);
        ScanLeases(into, request, projectName, sessions, leases, now);
        ScanVerification(into, request, projectName, verificationRuns, events);
        ScanRepository(into, request, projectName, insights);
    }

    private static void ScanSessions(
        List<AttentionSignal> into,
        WorkRequestDto request,
        string projectName,
        IReadOnlyList<AgentSessionDto> sessions)
    {
        foreach (var session in sessions)
        {
            if (session.Attention == AgentAttention.None)
            {
                continue;
            }

            var reason = string.IsNullOrWhiteSpace(session.StatusReason)
                ? "The runtime recorded no reason."
                : session.StatusReason;

            if (IsProviderAuth(session))
            {
                Add(
                    into,
                    AttentionKind.ProviderAuthMissing,
                    AttentionSeverity.Error,
                    $"{session.AgentName} is blocked on provider authentication",
                    $"{reason} Complete the provider's own sign-in locally on the node running "
                        + $"{session.Runtime}; the Command Center never collects credentials.",
                    request,
                    projectName,
                    session.Id,
                    $"session {session.Id} \u00b7 {session.Attention}");
                continue;
            }

            var (kind, severity, title) = session.Attention switch
            {
                AgentAttention.InputRequired => (
                    AttentionKind.HumanInput,
                    AttentionSeverity.Warning,
                    $"{session.AgentName} is waiting for human input"),
                AgentAttention.ApprovalRequired => (
                    AttentionKind.Approval,
                    AttentionSeverity.Warning,
                    $"{session.AgentName} is waiting for approval"),
                AgentAttention.ReservationConflict => (
                    AttentionKind.ReservationConflict,
                    AttentionSeverity.Warning,
                    $"{session.AgentName} is blocked by a reservation conflict"),
                AgentAttention.Error => (
                    AttentionKind.AgentError,
                    AttentionSeverity.Error,
                    $"{session.AgentName} reported an error"),
                _ => (
                    AttentionKind.AgentError,
                    AttentionSeverity.Warning,
                    $"{session.AgentName} reported a warning"),
            };

            Add(
                into,
                kind,
                severity,
                title,
                reason,
                request,
                projectName,
                session.Id,
                $"session {session.Id} \u00b7 {session.Attention}");
        }
    }

    private static void ScanLeases(
        List<AttentionSignal> into,
        WorkRequestDto request,
        string projectName,
        IReadOnlyList<AgentSessionDto> sessions,
        IReadOnlyList<ReservationLeaseDto> leases,
        DateTimeOffset now)
    {
        foreach (var lease in leases)
        {
            var state = (ReservationLeaseState)lease.State;
            if (state == ReservationLeaseState.Released)
            {
                continue;
            }

            var owner = FindSession(sessions, lease.OwnerSessionId);
            var ownerGone = owner is not null
                && owner.Liveness is AgentLiveness.Disconnected or AgentLiveness.Exited;
            var scopes = lease.Scopes.Count == 1 ? "1 scope" : $"{lease.Scopes.Count} scopes";

            if (ownerGone)
            {
                Add(
                    into,
                    AttentionKind.DisconnectedWithLease,
                    AttentionSeverity.Error,
                    $"{owner!.AgentName} is {owner.Liveness.ToString().ToLowerInvariant()} but still holds a lease",
                    $"{scopes} stay blocked while the owning session is gone. Confirm the process stopped, "
                        + "then force-release the lease group with a reason and a repository status snapshot.",
                    request,
                    projectName,
                    lease.OwnerSessionId,
                    $"lease {lease.LeaseId} \u00b7 {lease.StateName}");
                continue;
            }

            if (state == ReservationLeaseState.RecoveryRequired)
            {
                Add(
                    into,
                    AttentionKind.RecoveryRequired,
                    AttentionSeverity.Warning,
                    "A lease group is quarantined for recovery inspection",
                    $"{scopes} cannot be granted again until the node confirms the owning process stopped "
                        + $"or a human force-releases the lease. Recorded reason: "
                        + $"{(string.IsNullOrWhiteSpace(lease.Reason) ? "none" : lease.Reason)}.",
                    request,
                    projectName,
                    lease.OwnerSessionId,
                    $"lease {lease.LeaseId} \u00b7 {lease.StateName}");
                continue;
            }

            if (state == ReservationLeaseState.Active && lease.ExpiresAt <= now)
            {
                Add(
                    into,
                    AttentionKind.RecoveryRequired,
                    AttentionSeverity.Warning,
                    "A lease deadline passed without renewal",
                    $"{scopes} are held past {lease.ExpiresAt:u}. The authority must mark the lease "
                        + "recovery-required before any covered scope is granted again.",
                    request,
                    projectName,
                    lease.OwnerSessionId,
                    $"lease {lease.LeaseId} \u00b7 expired {lease.ExpiresAt:u}");
            }
        }
    }

    /// <summary>
    /// Verification attention is exactly what the completion gate blocks on: a mandatory
    /// <see cref="VerificationRunKind.Baseline"/> or <see cref="VerificationRunKind.ProjectCheck"/>
    /// command of the current fingerprint that failed or timed out. Intermediate runs are ignored
    /// because they never affect phase, attention, or completion; optional commands are ignored
    /// because a red optional command only warns; and a red row from a superseded fingerprint is
    /// retained history that blocks nothing.
    /// </summary>
    private static void ScanVerification(
        List<AttentionSignal> into,
        WorkRequestDto request,
        string projectName,
        IReadOnlyList<VerificationRunDto> runs,
        IReadOnlyList<SessionEventDto>? events)
    {
        var view = RequestVerificationViewReader.Read(runs, Array.Empty<AgentSessionDto>(), events);
        ScanVerificationRows(into, request, projectName, view.Baseline);
        ScanVerificationRows(into, request, projectName, view.ProjectChecks);
    }

    private static void ScanVerificationRows(
        List<AttentionSignal> into,
        WorkRequestDto request,
        string projectName,
        IReadOnlyList<VerificationRow> rows)
    {
        foreach (var row in rows)
        {
            if (!row.IsBlocking)
            {
                continue;
            }

            var run = row.Run;
            var outcome = run.Status == VerificationRunStatus.TimedOut ? "timed out" : "failed";
            var exit = run.ExitCode is { } code ? $" with exit code {code}" : string.Empty;
            var summary = string.IsNullOrWhiteSpace(run.OutputSummary)
                ? "No output summary was captured."
                : run.OutputSummary;
            var kind = run.RunKind == VerificationRunKind.Baseline ? "baseline" : "project check";

            Add(
                into,
                AttentionKind.VerificationFailed,
                AttentionSeverity.Error,
                $"Verification {run.CommandId} {outcome}",
                $"Mandatory {kind} command {run.CommandId} of profile {run.ProfileId} "
                    + $"{outcome}{exit}. Completion stays blocked until it passes for the current "
                    + $"repository fingerprint. {summary}",
                request,
                projectName,
                sessionId: null,
                $"verification run {run.Id}",
                run.CompletedAt ?? run.StartedAt);
        }
    }

    private static void ScanRepository(
        List<AttentionSignal> into,
        WorkRequestDto request,
        string projectName,
        RequestInsights insights)
    {
        if (insights.LatestExternalChange is { } external)
        {
            var paths = external.Paths.Count == 0
                ? "The event recorded no paths."
                : $"Paths: {string.Join(", ", external.Paths)}.";

            Add(
                into,
                AttentionKind.ExternalChange,
                AttentionSeverity.Error,
                "The repository changed outside every lease",
                $"{paths} {external.Detail ?? "Completion stays blocked until the change is attributed or reverted by a human."} "
                    + "The Command Center never resets, stashes, or cleans the repository.",
                request,
                projectName,
                sessionId: null,
                $"repository.external_change_detected \u00b7 seq {external.Sequence}",
                external.OccurredAt);
        }

        foreach (var handoff in insights.OpenHandoffRequests)
        {
            Add(
                into,
                AttentionKind.HandoffRequested,
                AttentionSeverity.Warning,
                "A reservation handoff is unanswered",
                $"{handoff.Reason ?? "No reason was recorded."} No transfer or release for lease "
                    + $"{handoff.LeaseId ?? "(unrecorded)"} is persisted yet.",
                request,
                projectName,
                handoff.SessionId,
                $"reservation.handoff_requested \u00b7 seq {handoff.Sequence}",
                handoff.OccurredAt);
        }
    }

    private static bool IsProviderAuth(AgentSessionDto session)
    {
        if (session.Attention is not (AgentAttention.Error or AgentAttention.InputRequired
            or AgentAttention.ApprovalRequired or AgentAttention.Warning))
        {
            return false;
        }

        return Matches(session.StatusReason) || Matches(session.CurrentOperation);
    }

    private static bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var marker in ProviderAuthMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static AgentSessionDto? FindSession(IReadOnlyList<AgentSessionDto> sessions, string sessionId)
    {
        foreach (var session in sessions)
        {
            if (string.Equals(session.Id, sessionId, StringComparison.Ordinal))
            {
                return session;
            }
        }

        return null;
    }

    private static void Add(
        List<AttentionSignal> into,
        AttentionKind kind,
        AttentionSeverity severity,
        string title,
        string detail,
        WorkRequestDto request,
        string projectName,
        string? sessionId,
        string evidence,
        DateTimeOffset? observedAt = null) =>
        into.Add(new AttentionSignal(
            kind,
            severity,
            title,
            detail,
            observedAt ?? request.UpdatedAt,
            request.ProjectId,
            projectName,
            request.Id,
            request.Title,
            sessionId,
            evidence));
}
