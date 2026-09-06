using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Completion;

/// <summary>
/// EF-backed terminalization authority. Begin closes admission and moves the assignment into
/// Finalizing/Cancelling (capacity stays occupied); Confirm requires an exact all-zero/true
/// quiescence proof and then atomically persists the result (Complete only), the work request
/// terminal status, and the assignment terminal status in a single commit. Any mismatch or
/// uncertainty leaves the assignment nonterminal — ownership is never released here.
/// </summary>
public sealed class AssignmentTerminalizationService(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier,
    ILogger<AssignmentTerminalizationService>? logger = null) : IAssignmentTerminalizationService
{
    private readonly ILogger _logger = logger ?? NullLogger<AssignmentTerminalizationService>.Instance;

    public async Task<CompletionGateDecision> BeginAsync(
        NodeId nodeId,
        ProjectId projectId,
        WorkRequestId requestId,
        string claimToken,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ValidateCorrelation(nodeId, projectId, requestId, claimToken, rootSessionId, intent);

        var assignment = await LoadOwnedAssignmentAsync(nodeId, projectId, requestId, claimToken, cancellationToken)
            .ConfigureAwait(false);
        var request = await LoadRequestAsync(projectId, requestId, cancellationToken).ConfigureAwait(false);

        var terminal = await TryTerminalDecisionAsync(assignment, request, intent, cancellationToken)
            .ConfigureAwait(false);
        if (terminal is not null)
        {
            return terminal;
        }

        var missing = await ValidateIntentAsync(requestId, intent, evidence, reason, cancellationToken)
            .ConfigureAwait(false);
        if (missing.Count > 0)
        {
            return new CompletionGateDecision(false, missing, null);
        }

        if (intent == TerminalizationIntent.Complete && request.Status != WorkRequestStatus.Verifying)
        {
            throw new InvalidOperationException(
                $"Completion requires status '{WorkRequestStatus.Verifying}' but request is '{request.Status}'.");
        }

        var now = clock.GetUtcNow();
        if (intent == TerminalizationIntent.Cancel)
        {
            if (assignment.State != ExecutionAssignmentState.Cancelling)
            {
                assignment.BeginCancelling(now);
            }
            if (request.Status != WorkRequestStatus.Cancelling)
            {
                request.BeginCancelling(now);
            }
        }
        else
        {
            if (assignment.State == ExecutionAssignmentState.Cancelling
                || assignment.State == ExecutionAssignmentState.RecoveryRequired)
            {
                throw new InvalidOperationException(
                    $"Assignment in state '{assignment.State}' cannot begin finalizing.");
            }

            if (assignment.State == ExecutionAssignmentState.Starting)
            {
                // Dispatch wiring has not marked the assignment running yet; the
                // transactional coordinator owns the transition so Finalizing is reachable.
                assignment.MarkRunning(now);
            }

            if (assignment.State == ExecutionAssignmentState.Running)
            {
                assignment.BeginFinalizing(now);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        notifier.Publish(ProjectionChange.Request(projectId.Value, requestId.Value));

        return new CompletionGateDecision(true, [], null);
    }

    public async Task<CompletionGateDecision> ConfirmAsync(
        NodeId nodeId,
        ProjectId projectId,
        WorkRequestId requestId,
        string claimToken,
        string rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        AssignmentQuiescenceProof proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ValidateCorrelation(nodeId, projectId, requestId, claimToken, rootSessionId, intent);

        var assignment = await LoadOwnedAssignmentAsync(nodeId, projectId, requestId, claimToken, cancellationToken)
            .ConfigureAwait(false);
        var request = await LoadRequestAsync(projectId, requestId, cancellationToken).ConfigureAwait(false);

        var terminal = await TryTerminalDecisionAsync(assignment, request, intent, cancellationToken)
            .ConfigureAwait(false);
        if (terminal is not null)
        {
            return terminal;
        }

        var missing = ValidateProof(proof);
        if (missing.Count == 0)
        {
            missing = await ValidateIntentAsync(requestId, intent, evidence, reason, cancellationToken)
                .ConfigureAwait(false);
        }

        if (missing.Count > 0)
        {
            return new CompletionGateDecision(false, missing, null);
        }

        var expected = intent == TerminalizationIntent.Cancel
            ? ExecutionAssignmentState.Cancelling
            : ExecutionAssignmentState.Finalizing;
        if (assignment.State != expected)
        {
            throw new InvalidOperationException(
                $"'{nameof(ConfirmAsync)}' for '{intent}' requires state '{expected}' but assignment is '{assignment.State}'.");
        }

        var now = clock.GetUtcNow();
        switch (intent)
        {
            case TerminalizationIntent.Complete:
                if (request.Status != WorkRequestStatus.Verifying)
                {
                    throw new InvalidOperationException(
                        $"Completion requires status '{WorkRequestStatus.Verifying}' but request is '{request.Status}'.");
                }

                PersistResult(requestId, evidence!, now);
                request.Complete(now);
                assignment.Complete(now);
                break;
            case TerminalizationIntent.Fail:
                request.Fail(now);
                assignment.Fail(now);
                break;
            case TerminalizationIntent.Cancel:
                request.ConfirmCancellation(now);
                assignment.Cancel(now);
                break;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        notifier.Publish(ProjectionChange.Request(projectId.Value, requestId.Value));

        if (intent != TerminalizationIntent.Complete)
        {
            return new CompletionGateDecision(true, [], null);
        }

        var persisted = await db.RequestResults
            .AsNoTracking()
            .SingleAsync(r => r.RequestId == requestId.Value, cancellationToken)
            .ConfigureAwait(false);
        return new CompletionGateDecision(true, [], ToDto(persisted));
    }

    public async Task<RequestResultDto?> GetResultAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var row = await db.RequestResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequestId == requestId.Value, cancellationToken)
            .ConfigureAwait(false);

        return row is null ? null : ToDto(row);
    }

    private static void ValidateCorrelation(
        NodeId nodeId,
        ProjectId projectId,
        WorkRequestId requestId,
        string claimToken,
        string rootSessionId,
        TerminalizationIntent intent)
    {
        if (nodeId.Value == Guid.Empty
            || projectId.Value == Guid.Empty
            || requestId.Value == Guid.Empty
            || string.IsNullOrWhiteSpace(claimToken)
            || string.IsNullOrWhiteSpace(rootSessionId))
        {
            throw new AssignmentAuthorizationException(AssignmentAuthorizationCodes.InvalidInput);
        }

        if (!Enum.IsDefined(intent))
        {
            throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown terminalization intent.");
        }
    }

    private async Task<ExecutionAssignment> LoadOwnedAssignmentAsync(
        NodeId nodeId,
        ProjectId projectId,
        WorkRequestId requestId,
        string claimToken,
        CancellationToken cancellationToken)
    {
        var assignment = await db.ExecutionAssignments
            .SingleOrDefaultAsync(a => a.RequestId == requestId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new AssignmentAuthorizationException(AssignmentAuthorizationCodes.AssignmentMissing);

        if (assignment.NodeIdSnapshot != nodeId)
        {
            throw new AssignmentAuthorizationException(AssignmentAuthorizationCodes.NodeMismatch);
        }

        if (!string.Equals(assignment.ClaimToken, claimToken, StringComparison.Ordinal))
        {
            throw new AssignmentAuthorizationException(AssignmentAuthorizationCodes.TokenMismatch);
        }

        if (assignment.ProjectId != projectId)
        {
            throw new AssignmentAuthorizationException(AssignmentAuthorizationCodes.ProjectMismatch);
        }

        return assignment;
    }

    private async Task<WorkRequest> LoadRequestAsync(
        ProjectId projectId,
        WorkRequestId requestId,
        CancellationToken cancellationToken)
    {
        var request = await db.WorkRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RequestNotFoundException(requestId);

        if (request.ProjectId != projectId)
        {
            throw new RequestNotFoundException(requestId);
        }

        return request;
    }

    /// <summary>
    /// Exact retries after a committed terminalization return the persisted outcome without
    /// reopening. Any terminal state that does not match the requested intent is an error.
    /// </summary>
    private async Task<CompletionGateDecision?> TryTerminalDecisionAsync(
        ExecutionAssignment assignment,
        WorkRequest request,
        TerminalizationIntent intent,
        CancellationToken cancellationToken)
    {
        var matches = assignment.State switch
        {
            ExecutionAssignmentState.Completed => intent == TerminalizationIntent.Complete
                && request.Status == WorkRequestStatus.Completed,
            ExecutionAssignmentState.Failed => intent == TerminalizationIntent.Fail
                && request.Status == WorkRequestStatus.Failed,
            ExecutionAssignmentState.Cancelled => intent == TerminalizationIntent.Cancel
                && request.Status == WorkRequestStatus.Cancelled,
            _ => false,
        };

        if (!matches)
        {
            return assignment.State is ExecutionAssignmentState.Completed
                or ExecutionAssignmentState.Failed
                or ExecutionAssignmentState.Cancelled
                ? throw new InvalidOperationException(
                    $"Assignment in terminal state '{assignment.State}' cannot be reopened for '{intent}'.")
                : null;
        }

        if (intent != TerminalizationIntent.Complete)
        {
            return new CompletionGateDecision(true, [], null);
        }

        var row = await db.RequestResults
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.RequestId == request.Id.Value, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Assignment is complete but the persisted request result is missing.");

        return new CompletionGateDecision(true, [], ToDto(row));
    }

    /// <summary>
    /// Validates the intent-specific payload. Complete runs the objective completion
    /// preflight; Fail/Cancel require a reason.
    /// </summary>
    private async Task<List<string>> ValidateIntentAsync(
        WorkRequestId requestId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (intent == TerminalizationIntent.Complete)
        {
            return evidence is null
                ? [CompletionRequirements.CompletionEvidence]
                : await EvaluateCompletionCriteriaAsync(requestId, evidence, cancellationToken)
                    .ConfigureAwait(false);
        }

        return string.IsNullOrWhiteSpace(reason)
            ? [CompletionRequirements.TerminalizationReason]
            : [];
    }

    private static List<string> ValidateProof(AssignmentQuiescenceProof proof)
    {
        var missing = new List<string>();
        if (!proof.AdmissionClosed)
        {
            missing.Add(CompletionRequirements.QuiescenceAdmission);
        }

        if (proof.ActiveChildren != 0)
        {
            missing.Add(CompletionRequirements.QuiescenceChildren);
        }

        if (proof.ActiveOperations != 0)
        {
            missing.Add(CompletionRequirements.QuiescenceOperations);
        }

        if (proof.ActiveProcesses != 0)
        {
            missing.Add(CompletionRequirements.QuiescenceProcesses);
        }

        if (proof.PendingEvents != 0)
        {
            missing.Add(CompletionRequirements.QuiescenceEvents);
        }

        if (proof.ActiveReservations != 0)
        {
            missing.Add(CompletionRequirements.QuiescenceReservations);
        }

        if (!proof.RepositoryInspected)
        {
            missing.Add(CompletionRequirements.QuiescenceRepository);
        }

        return missing;
    }

    /// <summary>Objective completion criteria over sessions, events, reservations, and verification.</summary>
    private async Task<List<string>> EvaluateCompletionCriteriaAsync(
        WorkRequestId requestId,
        CompletionEvidence evidence,
        CancellationToken cancellationToken)
    {
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(evidence.SummaryMarkdown))
        {
            missing.Add(CompletionRequirements.ResultSummary);
        }

        if (evidence.ChangedFiles is null)
        {
            missing.Add(CompletionRequirements.DiffCaptured);
        }

        var findings = evidence.ReviewFindings ?? [];
        if (findings.Any(f => f.Blocking && !f.Resolved && !f.UserOverridden))
        {
            missing.Add(CompletionRequirements.UnresolvedBlockingFinding);
        }

        var events = await db.SessionEvents
            .AsNoTracking()
            .Where(e => e.RequestId == requestId.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!events.Any(IsPlanEvent))
        {
            missing.Add(CompletionRequirements.PlanEvent);
        }

        var sessions = await db.AgentSessions
            .AsNoTracking()
            .Where(s => s.RequestId == requestId.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var implementers = sessions
            .Where(s => s.ParentSessionId is not null
                && RoleIs(s.Role, "implementer")
                && string.Equals(s.WorkState, nameof(AgentWorkState.Completed), StringComparison.Ordinal))
            .ToList();

        if (implementers.Count == 0)
        {
            missing.Add(CompletionRequirements.ImplementationChild);
        }

        var reviewers = sessions
            .Where(s => s.ParentSessionId is not null
                && RoleIs(s.Role, "reviewer")
                && string.Equals(s.WorkState, nameof(AgentWorkState.Completed), StringComparison.Ordinal)
                && implementers.All(i => !string.Equals(i.Id, s.Id, StringComparison.Ordinal)))
            .ToList();

        if (reviewers.Count == 0)
        {
            missing.Add(CompletionRequirements.IndependentReviewer);
        }

        if (sessions.Any(s => string.Equals(s.Activity, nameof(AgentActivity.RunningTool), StringComparison.Ordinal)))
        {
            missing.Add(CompletionRequirements.ActiveMutation);
        }

        var leases = await db.ReservationLeases
            .AsNoTracking()
            .Include(l => l.Scopes)
            .Where(l => l.RequestId == requestId.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (leases.Any(l => string.Equals(l.State, nameof(ReservationLeaseState.Active), StringComparison.Ordinal)))
        {
            missing.Add(CompletionRequirements.ActiveReservation);
        }

        var changed = evidence.ChangedFiles ?? [];
        if (evidence.ChangedFiles is not null && !OwnershipKnown(changed, leases))
        {
            missing.Add(CompletionRequirements.OwnershipKnown);
        }

        var runs = await db.VerificationRuns
            .AsNoTracking()
            .Where(r => r.RequestId == requestId.Value)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var mandatory = runs.Where(r => r.Mandatory).ToList();
        if (mandatory.Count == 0
            || mandatory.Any(r => !string.Equals(r.Status, nameof(Domain.Verification.VerificationRunStatus.Passed), StringComparison.Ordinal)))
        {
            missing.Add(CompletionRequirements.MandatoryVerification);
        }

        return missing;
    }

    private void PersistResult(WorkRequestId requestId, CompletionEvidence evidence, DateTimeOffset now)
    {
        if (db.RequestResults.Local.Any(r => r.RequestId == requestId.Value))
        {
            return;
        }

        var findings = evidence.ReviewFindings ?? [];
        var changed = evidence.ChangedFiles ?? [];
        var domainResult = RequestResult.Create(
            requestId,
            evidence.SummaryMarkdown,
            CompletionJson.SerializeFiles(changed),
            CompletionJson.SerializeFindings(findings),
            CompletionJson.SerializeSummary(evidence.VerificationSummary),
            now);

        var overridden = findings.Count(f => f.UserOverridden);
        if (overridden > 0)
        {
            _logger.LogInformation(
                "AUDIT completion-override request={RequestId} findings={Count}",
                requestId.Value,
                overridden);
        }

        db.RequestResults.Add(new RequestResultRow
        {
            RequestId = domainResult.RequestId.Value,
            SummaryMarkdown = domainResult.SummaryMarkdown,
            ChangedFilesJson = domainResult.ChangedFilesJson,
            ReviewFindingsJson = domainResult.ReviewFindingsJson,
            VerificationSummaryJson = domainResult.VerificationSummaryJson,
            RequestBranch = BlankToNull(evidence.RequestBranch),
            CheckpointCommitId = BlankToNull(evidence.CheckpointCommitId),
            CreatedAtUtcTicks = domainResult.CreatedAt.UtcTicks,
        });
    }

    private static RequestResultDto ToDto(RequestResultRow row) => new(
        row.RequestId,
        row.SummaryMarkdown,
        CompletionJson.DeserializeFiles(row.ChangedFilesJson),
        CompletionJson.DeserializeFindings(row.ReviewFindingsJson),
        CompletionJson.DeserializeSummary(row.VerificationSummaryJson),
        new DateTimeOffset(row.CreatedAtUtcTicks, TimeSpan.Zero),
        row.RequestBranch,
        row.CheckpointCommitId);

    private static string? BlankToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsPlanEvent(SessionEvent e)
    {
        if (e.Type.StartsWith("plan.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(e.Type, "request.phase_changed", StringComparison.OrdinalIgnoreCase)
            && e.PayloadJson.Contains("plan", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool RoleIs(string role, string expected) =>
        string.Equals(role, expected, StringComparison.OrdinalIgnoreCase);

    private static bool OwnershipKnown(IReadOnlyList<string> changedFiles, List<ReservationLeaseRow> leases)
    {
        if (changedFiles.Count == 0)
        {
            return true;
        }

        ReservationScope[] scopes;
        try
        {
            scopes = leases
                .SelectMany(l => l.Scopes)
                .Where(s => s.Kind != (int)ReservationScopeKind.Resource)
                .Select(s => ReservationScope.Create((ReservationScopeKind)s.Kind, s.Path))
                .ToArray();
        }
        catch (InvalidReservationScopeException)
        {
            return false;
        }

        foreach (var path in changedFiles)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            ReservationScope file;
            try
            {
                file = ReservationScope.Create(ReservationScopeKind.File, path);
            }
            catch (InvalidReservationScopeException)
            {
                return false;
            }

            if (!scopes.Any(s => s.Covers(file)))
            {
                return false;
            }
        }

        return true;
    }
}
