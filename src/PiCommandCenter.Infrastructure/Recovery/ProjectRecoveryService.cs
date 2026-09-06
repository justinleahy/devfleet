using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>
/// Durable project recovery: inventory diagnosis, atomic hold/operation start,
/// restart-safe progress, recheck, and hold resume. Persistence rows stay inside this module.
/// </summary>
public sealed class ProjectRecoveryService(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier) : IProjectRecoveryService
{
    public const string StartAction = "start";
    public const string RecheckAction = "recheck";
    public static readonly TimeSpan AttemptDeadline = TimeSpan.FromSeconds(60);

    public async Task<ProjectRecoveryDiagnosis> GetDiagnosisAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        var project = await LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        var inventory = await LoadInventoryAsync(projectId, cancellationToken).ConfigureAwait(false);
        var revision = ProjectRecoveryInventory.ComputeRevision(
            project.Version,
            inventory.Assignments,
            inventory.Reservations);
        var hold = await db.Set<RecoveryHoldRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(row => row.ProjectId == projectId.Value, cancellationToken)
            .ConfigureAwait(false);
        var latest = await db.Set<RecoveryOperationRow>()
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId.Value)
            .OrderByDescending(row => row.CreatedAtUtcTicks)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        ProjectRecoveryOperation? latestOperation = null;
        if (latest is not null)
        {
            latestOperation = await MapOperationAsync(projectId, latest.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return new ProjectRecoveryDiagnosis(
            projectId,
            project.Version,
            revision,
            hold is not null,
            hold?.OperationId,
            hold?.Version,
            latestOperation,
            inventory.Assignments,
            inventory.Reservations);
    }

    public async Task<ProjectRecoveryStartResult> StartAsync(
        ProjectId projectId,
        StartProjectRecoveryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireNonBlank(command.InventoryRevision, nameof(command.InventoryRevision));
        RequireNonBlank(command.Reason, nameof(command.Reason));
        RequireNonBlank(command.Actor, nameof(command.Actor));
        RequireNonBlank(command.IdempotencyKey, nameof(command.IdempotencyKey));

        var reason = command.Reason.Trim();
        var actor = command.Actor.Trim();
        var key = command.IdempotencyKey.Trim();
        var inputHash = HashInput(command.InventoryRevision, reason, actor);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existingKey = await db.Set<RecoveryIdempotencyRow>()
                .SingleOrDefaultAsync(
                    row => row.ProjectId == projectId.Value
                        && row.Action == StartAction
                        && row.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingKey is not null)
            {
                if (!string.Equals(existingKey.InputHash, inputHash, StringComparison.Ordinal))
                {
                    throw new RecoveryIdempotencyConflictException(projectId, StartAction, key);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                if (existingKey.OperationId is null)
                {
                    return new ProjectRecoveryStartResult(true, null);
                }

                var existing = await MapOperationAsync(
                    projectId,
                    existingKey.OperationId.Value,
                    cancellationToken).ConfigureAwait(false);
                return new ProjectRecoveryStartResult(false, existing);
            }

            var unresolved = await db.Set<RecoveryOperationRow>()
                .SingleOrDefaultAsync(
                    row => row.ProjectId == projectId.Value
                        && row.Status != nameof(RecoveryOperationStatus.Recovered),
                    cancellationToken)
                .ConfigureAwait(false);
            if (unresolved is not null)
            {
                throw new RecoveryOperationConflictException(projectId, unresolved.Id);
            }

            var project = await LoadProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
            var inventory = await LoadInventoryAsync(projectId, cancellationToken).ConfigureAwait(false);
            var revision = ProjectRecoveryInventory.ComputeRevision(
                project.Version,
                inventory.Assignments,
                inventory.Reservations);
            if (!string.Equals(revision, command.InventoryRevision, StringComparison.Ordinal))
            {
                throw new RecoveryInventoryConflictException(projectId, command.InventoryRevision);
            }

            if (inventory.Assignments.Count == 0 && inventory.Reservations.Count == 0)
            {
                db.Set<RecoveryIdempotencyRow>().Add(new RecoveryIdempotencyRow
                {
                    ProjectId = projectId.Value,
                    Action = StartAction,
                    Key = key,
                    InputHash = inputHash,
                    OperationId = null,
                    CreatedAtUtcTicks = UtcTicks(clock.GetUtcNow()),
                });
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ProjectRecoveryStartResult(true, null);
            }

            var now = clock.GetUtcNow().ToUniversalTime();
            var ticks = UtcTicks(now);
            var operationId = Guid.NewGuid();
            var operation = new RecoveryOperationRow
            {
                Id = operationId,
                ProjectId = projectId.Value,
                Status = nameof(RecoveryOperationStatus.Running),
                Attempt = 1,
                InventoryRevision = revision,
                Reason = reason,
                Actor = actor,
                Stage = "Pausing new work",
                CreatedAtUtcTicks = ticks,
                UpdatedAtUtcTicks = ticks,
                LastProgressUtcTicks = ticks,
                DeadlineUtcTicks = UtcTicks(now + AttemptDeadline),
                Version = 1,
            };
            foreach (var assignment in inventory.Assignments)
            {
                operation.AssignmentTargets.Add(new RecoveryTargetRow
                {
                    Id = Guid.NewGuid(),
                    OperationId = operationId,
                    RequestId = assignment.RequestId.Value,
                    CapturedVersion = assignment.Version,
                    CapturedState = assignment.State,
                    BindingRevision = assignment.BindingRevision,
                });
            }

            foreach (var reservation in inventory.Reservations)
            {
                operation.ReservationTargets.Add(new RecoveryReservationTargetRow
                {
                    Id = Guid.NewGuid(),
                    OperationId = operationId,
                    LeaseId = reservation.LeaseId,
                    CapturedVersion = reservation.Version,
                    CapturedState = reservation.State,
                });
            }

            db.Set<RecoveryOperationRow>().Add(operation);

            var hold = await db.Set<RecoveryHoldRow>()
                .SingleOrDefaultAsync(row => row.ProjectId == projectId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (hold is null)
            {
                db.Set<RecoveryHoldRow>().Add(new RecoveryHoldRow
                {
                    ProjectId = projectId.Value,
                    OperationId = operationId,
                    EstablishedAtUtcTicks = ticks,
                    Version = 1,
                });
            }
            else
            {
                hold.OperationId = operationId;
                hold.Version++;
            }

            db.Set<RecoveryIdempotencyRow>().Add(new RecoveryIdempotencyRow
            {
                ProjectId = projectId.Value,
                Action = StartAction,
                Key = key,
                InputHash = inputHash,
                OperationId = operationId,
                CreatedAtUtcTicks = ticks,
            });
            db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
            {
                Id = Guid.NewGuid(),
                OperationId = operationId,
                ProjectId = projectId.Value,
                Kind = "started",
                Reason = reason,
                Actor = actor,
                AtUtcTicks = ticks,
            });

            await RecordCancellationIntentAsync(inventory.Assignments, now, cancellationToken)
                .ConfigureAwait(false);

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUniqueConstraint(exception) || exception is DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return await ResolveStartRaceAsync(
                    projectId,
                    StartAction,
                    key,
                    inputHash,
                    command.InventoryRevision,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        notifier.Publish(ProjectionChange.Project(projectId.Value));
        var started = await db.Set<RecoveryIdempotencyRow>()
            .AsNoTracking()
            .SingleAsync(
                row => row.ProjectId == projectId.Value && row.Action == StartAction && row.Key == key,
                cancellationToken)
            .ConfigureAwait(false);
        var mapped = await MapOperationAsync(projectId, started.OperationId!.Value, cancellationToken)
            .ConfigureAwait(false);
        foreach (var target in mapped.AssignmentTargets)
        {
            notifier.Publish(ProjectionChange.Request(projectId.Value, target.RequestId.Value));
        }

        return new ProjectRecoveryStartResult(false, mapped);
    }

    public Task<ProjectRecoveryOperation> GetOperationAsync(
        ProjectId projectId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        MapOperationAsync(projectId, operationId, cancellationToken);

    public async Task<ProjectRecoveryOperation> RecheckAsync(
        ProjectId projectId,
        Guid operationId,
        long expectedOperationVersion,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        RequireNonBlank(idempotencyKey, nameof(idempotencyKey));
        var key = idempotencyKey.Trim();
        var inputHash = HashRecheck(operationId, expectedOperationVersion);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existingKey = await db.Set<RecoveryIdempotencyRow>()
                .SingleOrDefaultAsync(
                    row => row.ProjectId == projectId.Value
                        && row.Action == RecheckAction
                        && row.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingKey is not null)
            {
                if (!string.Equals(existingKey.InputHash, inputHash, StringComparison.Ordinal)
                    || existingKey.OperationId != operationId)
                {
                    throw new RecoveryIdempotencyConflictException(projectId, RecheckAction, key);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return await MapOperationAsync(projectId, operationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            var operation = await db.Set<RecoveryOperationRow>()
                .SingleOrDefaultAsync(
                    row => row.Id == operationId && row.ProjectId == projectId.Value,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RecoveryOperationNotFoundException(projectId, operationId);

            if (operation.Version != expectedOperationVersion)
            {
                throw new RecoveryRevisionConflictException(projectId, operationId, expectedOperationVersion);
            }

            var status = ParseStatus(operation.Status);
            if (status == RecoveryOperationStatus.Recovered)
            {
                throw new RecoveryNotReadyException(
                    $"Recovery operation '{operationId}' is recovered and cannot start another attempt.");
            }

            if (status is RecoveryOperationStatus.Pending or RecoveryOperationStatus.Running)
            {
                PersistRecheckIdempotency(projectId, operationId, key, inputHash);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return await MapOperationAsync(projectId, operationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (status != RecoveryOperationStatus.NeedsIntervention)
            {
                throw new RecoveryNotReadyException(
                    $"Recovery operation '{operationId}' cannot be rechecked from status '{operation.Status}'.");
            }

            var now = clock.GetUtcNow().ToUniversalTime();
            var ticks = UtcTicks(now);
            operation.Attempt++;
            operation.Status = nameof(RecoveryOperationStatus.Running);
            operation.UpdatedAtUtcTicks = ticks;
            operation.LastProgressUtcTicks = ticks;
            operation.DeadlineUtcTicks = UtcTicks(now + AttemptDeadline);
            operation.Version++;
            db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
            {
                Id = Guid.NewGuid(),
                OperationId = operationId,
                ProjectId = projectId.Value,
                Kind = "rechecked",
                Reason = operation.Reason,
                Actor = operation.Actor,
                AtUtcTicks = ticks,
            });
            PersistRecheckIdempotency(projectId, operationId, key, inputHash);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsUniqueConstraint(exception))
        {
            db.ChangeTracker.Clear();
            var raced = await db.Set<RecoveryIdempotencyRow>()
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.ProjectId == projectId.Value
                        && row.Action == RecheckAction
                        && row.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (raced is not null
                && string.Equals(raced.InputHash, inputHash, StringComparison.Ordinal)
                && raced.OperationId == operationId)
            {
                return await MapOperationAsync(projectId, operationId, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (raced is not null)
            {
                throw new RecoveryIdempotencyConflictException(projectId, RecheckAction, key);
            }

            throw new RecoveryRevisionConflictException(projectId, operationId, expectedOperationVersion);
        }

        notifier.Publish(ProjectionChange.Project(projectId.Value));
        return await MapOperationAsync(projectId, operationId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(
        ProjectId projectId,
        Guid operationId,
        long expectedHoldVersion,
        string actor,
        CancellationToken cancellationToken = default)
    {
        RequireNonBlank(actor, nameof(actor));
        var resumeActor = actor.Trim();

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var operation = await db.Set<RecoveryOperationRow>()
            .Include(row => row.AssignmentTargets)
            .Include(row => row.ReservationTargets)
            .SingleOrDefaultAsync(
                row => row.Id == operationId && row.ProjectId == projectId.Value,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RecoveryOperationNotFoundException(projectId, operationId);

        if (ParseStatus(operation.Status) != RecoveryOperationStatus.Recovered)
        {
            throw new RecoveryNotReadyException(
                $"Recovery operation '{operationId}' must be recovered before the hold can be cleared.");
        }

        var hold = await db.Set<RecoveryHoldRow>()
            .SingleOrDefaultAsync(row => row.ProjectId == projectId.Value, cancellationToken)
            .ConfigureAwait(false);
        if (hold is null || hold.Version != expectedHoldVersion)
        {
            throw new RecoveryRevisionConflictException(projectId, operationId, expectedHoldVersion);
        }

        var requestIds = operation.AssignmentTargets.Select(t => new WorkRequestId(t.RequestId)).ToList();
        var assignments = await db.ExecutionAssignments
            .Where(a => requestIds.Contains(a.RequestId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var target in operation.AssignmentTargets)
        {
            var live = assignments.SingleOrDefault(a => a.RequestId.Value == target.RequestId);
            if (live is null || !IsTerminal(live.State))
            {
                throw new RecoveryNotReadyException(
                    $"Assignment '{target.RequestId}' is still unresolved; the recovery hold cannot be cleared.");
            }
        }

        var leaseIds = operation.ReservationTargets.Select(t => t.LeaseId).ToList();
        var leases = await db.Set<ReservationLeaseRow>()
            .Where(row => leaseIds.Contains(row.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var target in operation.ReservationTargets)
        {
            var live = leases.SingleOrDefault(row => row.Id == target.LeaseId);
            if (live is null || live.State != nameof(ReservationLeaseState.Released))
            {
                throw new RecoveryNotReadyException(
                    $"Reservation '{target.LeaseId}' is still unresolved; the recovery hold cannot be cleared.");
            }
        }

        db.Set<RecoveryHoldRow>().Remove(hold);
        var now = clock.GetUtcNow().ToUniversalTime();
        db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
        {
            Id = Guid.NewGuid(),
            OperationId = operationId,
            ProjectId = projectId.Value,
            Kind = "resumed",
            Reason = operation.Reason,
            Actor = resumeActor,
            AtUtcTicks = UtcTicks(now),
        });
        operation.UpdatedAtUtcTicks = UtcTicks(now);
        operation.Version++;

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DbUpdateConcurrencyException || IsUniqueConstraint(exception))
        {
            db.ChangeTracker.Clear();
            throw new RecoveryRevisionConflictException(projectId, operationId, expectedHoldVersion);
        }

        notifier.Publish(ProjectionChange.Project(projectId.Value));
    }

    private async Task RecordCancellationIntentAsync(
        IReadOnlyList<ProjectRecoveryAssignmentSnapshot> assignments,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var ids = assignments.Select(a => a.RequestId).ToList();
        var requests = await db.WorkRequests
            .Where(request => ids.Contains(request.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var liveAssignments = await db.ExecutionAssignments
            .Where(assignment => ids.Contains(assignment.RequestId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var snapshot in assignments)
        {
            var request = requests.Single(candidate => candidate.Id == snapshot.RequestId);
            var assignment = liveAssignments.Single(candidate => candidate.RequestId == snapshot.RequestId);
            if (IsTerminal(assignment.State))
            {
                continue;
            }

            if (request.Status is not WorkRequestStatus.Cancelling
                and not WorkRequestStatus.Completed
                and not WorkRequestStatus.Failed
                and not WorkRequestStatus.Cancelled)
            {
                request.BeginCancelling(now);
            }

            if (assignment.State != ExecutionAssignmentState.Cancelling)
            {
                assignment.BeginCancelling(now);
            }
        }
    }

    private async Task<ProjectRecoveryStartResult> ResolveStartRaceAsync(
        ProjectId projectId,
        string action,
        string key,
        string inputHash,
        string expectedRevision,
        CancellationToken cancellationToken)
    {
        var existingKey = await db.Set<RecoveryIdempotencyRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.ProjectId == projectId.Value && row.Action == action && row.Key == key,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingKey is not null)
        {
            if (!string.Equals(existingKey.InputHash, inputHash, StringComparison.Ordinal))
            {
                throw new RecoveryIdempotencyConflictException(projectId, action, key);
            }

            if (existingKey.OperationId is null)
            {
                return new ProjectRecoveryStartResult(true, null);
            }

            var mapped = await MapOperationAsync(projectId, existingKey.OperationId.Value, cancellationToken)
                .ConfigureAwait(false);
            return new ProjectRecoveryStartResult(false, mapped);
        }

        var unresolved = await db.Set<RecoveryOperationRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.ProjectId == projectId.Value
                    && row.Status != nameof(RecoveryOperationStatus.Recovered),
                cancellationToken)
            .ConfigureAwait(false);
        if (unresolved is not null)
        {
            throw new RecoveryOperationConflictException(projectId, unresolved.Id);
        }

        throw new RecoveryInventoryConflictException(projectId, expectedRevision);
    }

    private async Task<ProjectRecoveryOperation> MapOperationAsync(
        ProjectId projectId,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var row = await db.Set<RecoveryOperationRow>()
            .AsNoTracking()
            .Include(candidate => candidate.AssignmentTargets)
            .Include(candidate => candidate.ReservationTargets)
            .SingleOrDefaultAsync(
                candidate => candidate.Id == operationId && candidate.ProjectId == projectId.Value,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RecoveryOperationNotFoundException(projectId, operationId);

        return new ProjectRecoveryOperation(
            row.Id,
            projectId,
            ParseStatus(row.Status),
            row.Attempt,
            row.Version,
            row.InventoryRevision,
            row.Reason,
            row.Actor,
            row.Stage,
            row.BlockerCodesJson,
            row.EvidenceJson,
            FromTicks(row.CreatedAtUtcTicks),
            FromTicks(row.UpdatedAtUtcTicks),
            row.CompletedAtUtcTicks is null ? null : FromTicks(row.CompletedAtUtcTicks.Value),
            row.DeadlineUtcTicks is null ? null : FromTicks(row.DeadlineUtcTicks.Value),
            row.AssignmentTargets
                .OrderBy(t => t.RequestId)
                .Select(t => new ProjectRecoveryAssignmentTarget(
                    new WorkRequestId(t.RequestId),
                    t.CapturedVersion,
                    t.CapturedState,
                    t.BindingRevision,
                    t.Outcome,
                    t.EvidenceJson))
                .ToList(),
            row.ReservationTargets
                .OrderBy(t => t.LeaseId)
                .Select(t => new ProjectRecoveryReservationTarget(
                    t.LeaseId,
                    t.CapturedVersion,
                    t.CapturedState,
                    t.Outcome,
                    t.EvidenceJson))
                .ToList());
    }

    private async Task<Project> LoadProjectAsync(ProjectId projectId, CancellationToken cancellationToken) =>
        await db.Projects
            .SingleOrDefaultAsync(project => project.Id == projectId, cancellationToken)
            .ConfigureAwait(false)
        ?? throw new InvalidOperationException($"Project '{projectId.Value}' was not found.");

    private async Task<Inventory> LoadInventoryAsync(ProjectId projectId, CancellationToken cancellationToken)
    {
        var assignmentRows = await db.ExecutionAssignments
            .AsNoTracking()
            .Where(assignment => assignment.ProjectId == projectId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var nodeIds = assignmentRows
            .Select(assignment => assignment.NodeIdSnapshot)
            .Distinct()
            .ToList();
        var nodes = nodeIds.Count == 0
            ? []
            : await db.FleetNodes
                .AsNoTracking()
                .Where(node => nodeIds.Contains(node.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        var nodesById = nodes.ToDictionary(node => node.Id);
        var assignments = assignmentRows
            .Where(assignment => !IsTerminal(assignment.State))
            .Select(assignment =>
            {
                nodesById.TryGetValue(assignment.NodeIdSnapshot, out var node);
                return new ProjectRecoveryAssignmentSnapshot(
                    assignment.RequestId,
                    assignment.Version,
                    assignment.State.ToString(),
                    assignment.BindingValidationRevisionSnapshot,
                    assignment.NodeIdSnapshot.Value,
                    node?.DisplayName,
                    assignment.CanonicalRepositoryPathSnapshot,
                    assignment.AssignedAt,
                    assignment.LastRenewedAt,
                    assignment.LastReconciledAt,
                    assignment.LeaseExpiresAt,
                    node?.LastHeartbeatAt,
                    node is null ? null : node.Status.ToString());
            })
            .ToList();
        var reservationRows = await db.Set<ReservationLeaseRow>()
            .AsNoTracking()
            .Where(row => row.ProjectId == projectId.Value
                && row.State != nameof(ReservationLeaseState.Released))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var reservations = reservationRows
            .Select(row => new ProjectRecoveryReservationSnapshot(
                row.Id,
                row.Version,
                row.State,
                row.RequestId,
                row.OwnerSessionId,
                row.Reason,
                FromTicks(row.ExpiresAtUtcTicks)))
            .ToList();
        return new Inventory(assignments, reservations);
    }

    private static void RequireNonBlank(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} must be non-blank.", name);
        }
    }

    private static string HashInput(string revision, string reason, string actor)
    {
        var payload = revision + "\n" + reason + "\n" + actor + "\n";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }


    private static string HashRecheck(Guid operationId, long expectedOperationVersion)
    {
        var payload = operationId.ToString("D") + "\n" + expectedOperationVersion.ToString() + "\n";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void PersistRecheckIdempotency(
        ProjectId projectId,
        Guid operationId,
        string key,
        string inputHash)
    {
        db.Set<RecoveryIdempotencyRow>().Add(new RecoveryIdempotencyRow
        {
            ProjectId = projectId.Value,
            Action = RecheckAction,
            Key = key,
            InputHash = inputHash,
            OperationId = operationId,
            CreatedAtUtcTicks = UtcTicks(clock.GetUtcNow()),
        });
    }
    private static bool IsTerminal(ExecutionAssignmentState state) =>
        state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;

    private static RecoveryOperationStatus ParseStatus(string status) =>
        Enum.Parse<RecoveryOperationStatus>(status);

    private static long UtcTicks(DateTimeOffset at) => at.ToUniversalTime().UtcTicks;

    private static DateTimeOffset FromTicks(long ticks) => new(ticks, TimeSpan.Zero);

    private static bool IsUniqueConstraint(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is SqliteException sqlite && sqlite.SqliteErrorCode is 19)
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Inventory(
        IReadOnlyList<ProjectRecoveryAssignmentSnapshot> Assignments,
        IReadOnlyList<ProjectRecoveryReservationSnapshot> Reservations);
}
