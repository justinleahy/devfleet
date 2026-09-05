using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Completion;

public sealed class CompletionGateService(TimeProvider clock, ControlPlaneDbContext db) : ICompletionGateService
{
    public async Task<CompletionGateDecision> EvaluateAsync(
        ProjectId projectId,
        WorkRequestId requestId,
        string rootSessionId,
        CompletionEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        var existing = await db.RequestResults
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.RequestId == requestId.Value, cancellationToken)
            .ConfigureAwait(false);

        var request = await db.WorkRequests
            .FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RequestNotFoundException(requestId);

        if (request.ProjectId != projectId)
        {
            throw new RequestNotFoundException(requestId);
        }

        if (existing is not null && request.Status == WorkRequestStatus.Completed)
        {
            return new CompletionGateDecision(true, [], ToDto(existing));
        }

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

        if (missing.Count > 0)
        {
            return new CompletionGateDecision(false, missing, null);
        }

        if (request.Status != WorkRequestStatus.Verifying)
        {
            throw new InvalidOperationException(
                $"Completion requires status '{WorkRequestStatus.Verifying}' but request is '{request.Status}'.");
        }

        var now = clock.GetUtcNow();
        var domainResult = RequestResult.Create(
            requestId,
            evidence.SummaryMarkdown,
            CompletionJson.SerializeFiles(changed),
            CompletionJson.SerializeFindings(findings),
            CompletionJson.SerializeSummary(evidence.VerificationSummary),
            now);

        if (existing is null)
        {
            db.RequestResults.Add(new RequestResultRow
            {
                RequestId = domainResult.RequestId.Value,
                SummaryMarkdown = domainResult.SummaryMarkdown,
                ChangedFilesJson = domainResult.ChangedFilesJson,
                ReviewFindingsJson = domainResult.ReviewFindingsJson,
                VerificationSummaryJson = domainResult.VerificationSummaryJson,
                CreatedAtUtcTicks = domainResult.CreatedAt.UtcTicks,
            });
        }

        request.Complete(now);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

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

    private static RequestResultDto ToDto(RequestResultRow row) => new(
        row.RequestId,
        row.SummaryMarkdown,
        CompletionJson.DeserializeFiles(row.ChangedFilesJson),
        CompletionJson.DeserializeFindings(row.ReviewFindingsJson),
        CompletionJson.DeserializeSummary(row.VerificationSummaryJson),
        new DateTimeOffset(row.CreatedAtUtcTicks, TimeSpan.Zero));

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
