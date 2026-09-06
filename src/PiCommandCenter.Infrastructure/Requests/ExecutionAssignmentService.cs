using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// Owns atomic assignment admission, lease renewal, and node inventory reconciliation.
/// Lease expiry never releases ownership.
/// </summary>
public sealed class ExecutionAssignmentService(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IRequestEligibilityEvaluator eligibilityEvaluator,
    IProjectionNotifier notifier) : IExecutionAssignmentService
{
    public async Task<ExecutionAssignmentDto?> ClaimNextAsync(
        NodeId nodeId,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ValidateNodeAndLease(nodeId, lease);

        try
        {
            await using var transaction = await db.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var candidates = await db.WorkRequests
                .Where(request => request.Status == WorkRequestStatus.Queued)
                .OrderByDescending(request => request.Priority)
                .ThenBy(request => request.CreatedAt)
                .ThenBy(request => request.Id)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var request in candidates)
            {
                var decision = await eligibilityEvaluator
                    .EvaluateAsync(request.Id, nodeId, cancellationToken)
                    .ConfigureAwait(false);
                var binding = decision.EligibleBinding;
                if (!decision.Status.IsEligible
                    || decision.Assignment is not null
                    || binding is null
                    || binding.NodeId != nodeId.Value
                    || binding.ProjectId != request.ProjectId.Value
                    || string.IsNullOrWhiteSpace(binding.CanonicalRepositoryPath))
                {
                    continue;
                }

                var project = await db.Projects
                    .SingleAsync(candidate => candidate.Id == request.ProjectId, cancellationToken)
                    .ConfigureAwait(false);
                var now = clock.GetUtcNow().ToUniversalTime();
                var assignment = ExecutionAssignment.Create(
                    request.Id,
                    request.ProjectId,
                    new WorkspaceBindingId(binding.Id),
                    nodeId,
                    binding.CanonicalRepositoryPath,
                    project.DefaultBranch,
                    binding.ValidationRevision,
                    CreateClaimToken(),
                    now,
                    lease);
                request.Start(now);
                db.ExecutionAssignments.Add(assignment);

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                PublishAssignmentChange(assignment);
                return ToDto(assignment, request, project);
            }

            return null;
        }
        catch (Exception exception) when (IsClaimRace(exception))
        {
            db.ChangeTracker.Clear();
            return null;
        }
    }

    public async Task<IReadOnlyList<AssignmentReconciliationResultDto>> ReconcileAsync(
        NodeId nodeId,
        IReadOnlyCollection<ExecutionAssignmentInventoryDto> inventory,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ValidateNodeAndLease(nodeId, lease);

        var inventoryByRequest = inventory
            .GroupBy(item => item.RequestId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var inventoryIds = inventoryByRequest.Keys.ToArray();
        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var assignments = await db.ExecutionAssignments
            .Where(assignment => assignment.NodeIdSnapshot == nodeId
                || inventoryIds.Contains(assignment.RequestId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var requestIds = assignments.Select(assignment => assignment.RequestId).ToArray();
        var requests = await db.WorkRequests
            .Where(request => requestIds.Contains(request.Id))
            .ToDictionaryAsync(request => request.Id, cancellationToken)
            .ConfigureAwait(false);
        var projectIds = assignments.Select(assignment => assignment.ProjectId).Distinct().ToArray();
        var projects = await db.Projects
            .Where(project => projectIds.Contains(project.Id))
            .ToDictionaryAsync(project => project.Id, cancellationToken)
            .ConfigureAwait(false);
        var now = clock.GetUtcNow().ToUniversalTime();
        var results = new List<AssignmentReconciliationResultDto>(
            Math.Max(assignments.Count, inventoryByRequest.Count));
        var changed = new List<ExecutionAssignment>();

        foreach (var assignment in assignments)
        {
            inventoryByRequest.Remove(assignment.RequestId, out var reported);
            if (assignment.NodeIdSnapshot != nodeId)
            {
                results.Add(new AssignmentReconciliationResultDto(
                    assignment.RequestId,
                    AssignmentReconciliationDisposition.RecoveryRequired,
                    Assignment: null));
                continue;
            }

            var authoritative = ToDto(
                assignment,
                requests[assignment.RequestId],
                projects[assignment.ProjectId]);
            if (IsTerminal(assignment.State))
            {
                if (reported is not null)
                {
                    results.Add(new AssignmentReconciliationResultDto(
                        assignment.RequestId,
                        AssignmentReconciliationDisposition.Terminal,
                        authoritative));
                }

                continue;
            }

            var item = reported is { Length: 1 } ? reported[0] : null;
            if (assignment.State == ExecutionAssignmentState.Cancelling)
            {
                results.Add(item is not null && IsSameAssignment(assignment, item)
                    ? new AssignmentReconciliationResultDto(
                        assignment.RequestId,
                        AssignmentReconciliationDisposition.Cancel,
                        authoritative)
                    : RecoveryResult(assignment, requests, projects));
                continue;
            }

            if (item is null || !IsExactEvidence(assignment, item))
            {
                MarkRecoveryRequired(assignment, now, changed);
                results.Add(RecoveryResult(assignment, requests, projects));
                continue;
            }

            if (assignment.State == ExecutionAssignmentState.RecoveryRequired
                || item.SupervisorState != AssignmentSupervisorState.Running
                || !item.RepositoryKnown
                || item.PendingEventCount < 0)
            {
                MarkRecoveryRequired(assignment, now, changed);
                results.Add(RecoveryResult(assignment, requests, projects));
                continue;
            }

            if (assignment.IsLeaseExpired(now))
            {
                var restoredState = assignment.State == ExecutionAssignmentState.Starting
                    ? ExecutionAssignmentState.Running
                    : assignment.State;
                assignment.MarkRecoveryRequired(now);
                assignment.Reconcile(nodeId, item.ClaimToken, restoredState, lease, now);
            }
            else
            {
                if (assignment.State == ExecutionAssignmentState.Starting)
                {
                    assignment.MarkRunning(now);
                }

                assignment.Renew(nodeId, item.ClaimToken, lease, now);
            }

            changed.Add(assignment);
            results.Add(new AssignmentReconciliationResultDto(
                assignment.RequestId,
                AssignmentReconciliationDisposition.Resume,
                ToDto(assignment, requests[assignment.RequestId], projects[assignment.ProjectId])));
        }

        foreach (var unknownId in inventoryByRequest.Keys)
        {
            results.Add(new AssignmentReconciliationResultDto(
                unknownId,
                AssignmentReconciliationDisposition.RecoveryRequired,
                Assignment: null));
        }

        if (changed.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var assignment in changed)
        {
            PublishAssignmentChange(assignment);
        }

        return results;
    }

    public async Task<DateTimeOffset> RenewAsync(
        WorkRequestId requestId,
        NodeId nodeId,
        string claimToken,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var assignment = await db.ExecutionAssignments
            .SingleOrDefaultAsync(candidate => candidate.RequestId == requestId, cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null)
        {
            throw new ClaimRenewalRejectedException(
                $"No execution assignment exists for request '{requestId}'.");
        }

        DateTimeOffset leaseExpiresAt;
        try
        {
            leaseExpiresAt = assignment.Renew(nodeId, claimToken, lease, clock.GetUtcNow());
        }
        catch (InvalidOperationException exception)
        {
            throw new ClaimRenewalRejectedException(exception.Message, exception);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        PublishAssignmentChange(assignment);
        return leaseExpiresAt;
    }

    private static AssignmentReconciliationResultDto RecoveryResult(
        ExecutionAssignment assignment,
        IReadOnlyDictionary<WorkRequestId, WorkRequest> requests,
        IReadOnlyDictionary<ProjectId, Project> projects) => new(
            assignment.RequestId,
            AssignmentReconciliationDisposition.RecoveryRequired,
            ToDto(assignment, requests[assignment.RequestId], projects[assignment.ProjectId]));

    private static void MarkRecoveryRequired(
        ExecutionAssignment assignment,
        DateTimeOffset now,
        ICollection<ExecutionAssignment> changed)
    {
        if (assignment.State == ExecutionAssignmentState.RecoveryRequired)
        {
            return;
        }

        assignment.MarkRecoveryRequired(now);
        changed.Add(assignment);
    }

    private static bool IsExactEvidence(
        ExecutionAssignment assignment,
        ExecutionAssignmentInventoryDto item) =>
        IsSameAssignment(assignment, item)
        && item.State == assignment.State;

    private static bool IsSameAssignment(
        ExecutionAssignment assignment,
        ExecutionAssignmentInventoryDto item) =>
        item.RequestId == assignment.RequestId
        && item.ProjectId == assignment.ProjectId
        && item.WorkspaceBindingId == assignment.WorkspaceBindingId
        && item.NodeIdSnapshot == assignment.NodeIdSnapshot
        && string.Equals(
            item.CanonicalRepositoryPathSnapshot,
            assignment.CanonicalRepositoryPathSnapshot,
            StringComparison.Ordinal)
        && string.Equals(
            item.DefaultBranchSnapshot,
            assignment.DefaultBranchSnapshot,
            StringComparison.Ordinal)
        && item.BindingValidationRevisionSnapshot == assignment.BindingValidationRevisionSnapshot
        && string.Equals(item.ClaimToken, assignment.ClaimToken, StringComparison.Ordinal)
        && item.AssignedAt == assignment.AssignedAt;

    private static ExecutionAssignmentDto ToDto(
        ExecutionAssignment assignment,
        WorkRequest request,
        Project project) => new(
            assignment.RequestId,
            assignment.ProjectId,
            assignment.WorkspaceBindingId,
            assignment.NodeIdSnapshot,
            assignment.CanonicalRepositoryPathSnapshot,
            assignment.DefaultBranchSnapshot,
            assignment.BindingValidationRevisionSnapshot,
            assignment.State,
            assignment.ClaimToken,
            assignment.AssignedAt,
            assignment.LeaseExpiresAt,
            request.Title,
            request.Prompt,
            request.Kind,
            request.RiskLevel,
            project.CreateRequestBranch,
            project.CreateRequestCommit);

    private static string CreateClaimToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private static void ValidateNodeAndLease(NodeId nodeId, TimeSpan lease)
    {
        if (nodeId.Value == Guid.Empty)
        {
            throw new ArgumentException("Node id must not be empty.", nameof(nodeId));
        }

        if (lease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lease), "Lease duration must be positive.");
        }
    }

    private static bool IsClaimRace(Exception exception) =>
        exception is DbUpdateConcurrencyException
        || FindSqliteException(exception) is
        {
            SqliteErrorCode: 5 or 6,
        };

    private static SqliteException? FindSqliteException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite;
            }
        }

        return null;
    }

    private static bool IsTerminal(ExecutionAssignmentState state) =>
        state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;

    private void PublishAssignmentChange(ExecutionAssignment assignment)
    {
        notifier.Publish(ProjectionChange.Project(assignment.ProjectId.Value));
        notifier.Publish(ProjectionChange.Request(
            assignment.ProjectId.Value,
            assignment.RequestId.Value));
    }
}
