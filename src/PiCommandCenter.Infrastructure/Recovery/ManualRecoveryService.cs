using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>
/// Atomic administrator manual-recovery transition. Persistence rows stay in this module.
/// Writer uncertainty and missing repository status cannot be waived. The hold is kept.
/// </summary>
public sealed class ManualRecoveryService(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier) : IManualProjectRecoveryService
{
    public const string ConfirmManualAction = "confirm-manual";
    public const string OperatorAttestationProvenance = "operator-attestation";
    public const int MaxEvidenceJsonLength = 16384;
    public const int MaxTextLength = 1024;
    public static readonly TimeSpan MaxRepositoryEvidenceAge = TimeSpan.FromMinutes(15);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<ProjectRecoveryOperation> ConfirmManualAsync(
        ProjectId projectId,
        ConfirmManualProjectRecoveryCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireNonBlank(command.ExactProjectName, nameof(command.ExactProjectName));
        RequireNonBlank(command.Reason, nameof(command.Reason));
        RequireNonBlank(command.Actor, nameof(command.Actor));
        RequireNonBlank(command.IdempotencyKey, nameof(command.IdempotencyKey));
        RequireNonBlank(command.ProcessStopEvidence, nameof(command.ProcessStopEvidence));
        RequireNonBlank(command.RepositoryStatusSnapshot, nameof(command.RepositoryStatusSnapshot));
        RequireNonBlank(command.RepositoryStatusSource, nameof(command.RepositoryStatusSource));
        RequireNonBlank(
            command.ReservationAndEventGapAccounting,
            nameof(command.ReservationAndEventGapAccounting));

        var reason = RequireBound(command.Reason.Trim(), nameof(command.Reason));
        var actor = RequireBound(command.Actor.Trim(), nameof(command.Actor), 128);
        var key = RequireBound(command.IdempotencyKey.Trim(), nameof(command.IdempotencyKey), 128);
        var projectName = command.ExactProjectName.Trim();
        var processStop = RequireBound(command.ProcessStopEvidence.Trim(), nameof(command.ProcessStopEvidence));
        var repoSnapshot = RequireBound(command.RepositoryStatusSnapshot.Trim(), nameof(command.RepositoryStatusSnapshot));
        var repoSource = RequireBound(command.RepositoryStatusSource.Trim(), nameof(command.RepositoryStatusSource));
        var gapAccounting = RequireBound(
            command.ReservationAndEventGapAccounting.Trim(),
            nameof(command.ReservationAndEventGapAccounting));
        var inputHash = HashInput(command);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var existingKey = await db.Set<RecoveryIdempotencyRow>()
                .SingleOrDefaultAsync(
                    row => row.ProjectId == projectId.Value
                        && row.Action == ConfirmManualAction
                        && row.Key == key,
                    cancellationToken)
                .ConfigureAwait(false);
            if (existingKey is not null)
            {
                if (!string.Equals(existingKey.InputHash, inputHash, StringComparison.Ordinal))
                {
                    throw new RecoveryIdempotencyConflictException(projectId, ConfirmManualAction, key);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return await MapOperationAsync(
                        projectId,
                        existingKey.OperationId
                            ?? throw new RecoveryOperationNotFoundException(projectId, command.OperationId),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            ValidateConfirmations(command);

            var now = clock.GetUtcNow().ToUniversalTime();
            ValidateRepositoryCollectionTime(command.RepositoryCollectedAt, now);

            var project = await db.Projects
                .SingleOrDefaultAsync(row => row.Id == projectId, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Project '{projectId.Value}' was not found.");
            if (!string.Equals(project.DisplayName, projectName, StringComparison.Ordinal))
            {
                throw new RecoveryNotReadyException(
                    $"Typed project name does not match project '{projectId.Value}'.");
            }

            var operation = await db.Set<RecoveryOperationRow>()
                .Include(row => row.AssignmentTargets)
                .Include(row => row.ReservationTargets)
                .SingleOrDefaultAsync(
                    row => row.Id == command.OperationId && row.ProjectId == projectId.Value,
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new RecoveryOperationNotFoundException(projectId, command.OperationId);

            if (operation.Version != command.ExpectedOperationVersion)
            {
                throw new RecoveryRevisionConflictException(
                    projectId,
                    command.OperationId,
                    command.ExpectedOperationVersion);
            }

            if (!string.Equals(
                    operation.Status,
                    nameof(RecoveryOperationStatus.NeedsIntervention),
                    StringComparison.Ordinal)
                || operation.Attempt != command.ExpectedAttempt)
            {
                throw new RecoveryNotReadyException(
                    $"Recovery operation '{command.OperationId}' is not the current NeedsIntervention attempt.");
            }

            var requestIds = operation.AssignmentTargets.Select(t => new WorkRequestId(t.RequestId)).ToList();
            var assignments = await db.ExecutionAssignments
                .Where(a => requestIds.Contains(a.RequestId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var requests = await db.WorkRequests
                .Where(r => requestIds.Contains(r.Id))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            foreach (var target in operation.AssignmentTargets)
            {
                if (IsResolved(target.Outcome))
                {
                    continue;
                }

                var live = assignments.SingleOrDefault(a => a.RequestId.Value == target.RequestId)
                    ?? throw new RecoveryNotReadyException(
                        $"Captured assignment '{target.RequestId}' is no longer present.");
                EnsureRecoveryOwnedAssignment(target, live);
            }

            var leaseIds = operation.ReservationTargets.Select(t => t.LeaseId).ToList();
            var leaseRows = leaseIds.Count == 0
                ? []
                : await db.Set<ReservationLeaseRow>()
                    .Include(row => row.Scopes)
                    .Where(row => leaseIds.Contains(row.Id))
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

            var evidence = BoundJson(SerializeEvidence(new
            {
                Provenance = OperatorAttestationProvenance,
                ProcessStopEvidence = processStop,
                RepositoryStatusSnapshot = repoSnapshot,
                RepositoryStatusSource = repoSource,
                RepositoryCollectedAt = command.RepositoryCollectedAt.ToUniversalTime(),
                ReservationAndEventGapAccounting = gapAccounting,
                WriterAccessPrevented = true,
                ConfirmOriginalExecutionCannotResume = true,
                AcknowledgeEvidenceGaps = true,
                Actor = actor,
                Attempt = operation.Attempt,
            }));

            foreach (var target in operation.AssignmentTargets)
            {
                if (IsResolved(target.Outcome))
                {
                    continue;
                }

                var assignment = assignments.Single(a => a.RequestId.Value == target.RequestId);
                var request = requests.Single(r => r.Id.Value == target.RequestId);
                CancelNonterminal(assignment, request, now);
                target.Outcome = nameof(ExecutionAssignmentState.Cancelled);
                target.EvidenceJson = evidence;
            }

            var pending = await db.PendingTerminalizations
                .Where(row => requestIds.Contains(row.RequestId))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (pending.Count > 0)
            {
                db.PendingTerminalizations.RemoveRange(pending);
            }

            ProjectFencingTokenRow? fence = null;
            foreach (var reservationTarget in operation.ReservationTargets)
            {
                if (IsResolved(reservationTarget.Outcome))
                {
                    continue;
                }

                var row = leaseRows.SingleOrDefault(l => l.Id == reservationTarget.LeaseId);
                if (row is null)
                {
                    throw new RecoveryNotReadyException(
                        $"Captured reservation '{reservationTarget.LeaseId}' is no longer present.");
                }

                if (string.Equals(row.State, nameof(ReservationLeaseState.Released), StringComparison.Ordinal))
                {
                    reservationTarget.Outcome = "Resolved";
                    reservationTarget.EvidenceJson = evidence;
                    continue;
                }

                if (row.Version != reservationTarget.CapturedVersion)
                {
                    throw new RecoveryNotReadyException(
                        $"Reservation '{reservationTarget.LeaseId}' revision is stale for manual recovery.");
                }

                if (!string.Equals(row.State, reservationTarget.CapturedState, StringComparison.Ordinal))
                {
                    throw new RecoveryNotReadyException(
                        $"Reservation '{reservationTarget.LeaseId}' state does not match the captured target.");
                }

                fence ??= await LoadOrCreateFenceAsync(projectId.Value, cancellationToken).ConfigureAwait(false);
                fence.LastFencingToken++;
                var lease = ToAggregate(row);
                lease.ForceRelease(reason, repoSnapshot, fence.LastFencingToken, now);
                ApplyToRow(lease, row);
                reservationTarget.Outcome = "Resolved";
                reservationTarget.EvidenceJson = evidence;
                db.ReservationAuditFacts.Add(new ReservationAuditFactRow
                {
                    Id = Guid.NewGuid(),
                    LeaseId = lease.Id,
                    ProjectId = projectId.Value,
                    Kind = "ForceReleased",
                    Reason = reason,
                    RepositoryStatusSnapshot = repoSnapshot,
                    Actor = actor,
                    AtUtcTicks = UtcTicks(now),
                });
            }

            var ticks = UtcTicks(now);
            operation.Status = nameof(RecoveryOperationStatus.Recovered);
            operation.Stage = "Resolving execution ownership";
            operation.EvidenceJson = evidence;
            operation.BlockerCodesJson = null;
            operation.UpdatedAtUtcTicks = ticks;
            operation.LastProgressUtcTicks = ticks;
            operation.CompletedAtUtcTicks = ticks;
            operation.Version++;

            db.Set<RecoveryIdempotencyRow>().Add(new RecoveryIdempotencyRow
            {
                ProjectId = projectId.Value,
                Action = ConfirmManualAction,
                Key = key,
                InputHash = inputHash,
                OperationId = operation.Id,
                CreatedAtUtcTicks = ticks,
            });
            db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
            {
                Id = Guid.NewGuid(),
                OperationId = operation.Id,
                ProjectId = projectId.Value,
                Kind = OperatorAttestationProvenance,
                Reason = reason,
                Actor = actor,
                PayloadJson = evidence,
                AtUtcTicks = ticks,
            });

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUniqueConstraint(exception) || exception is DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return await ResolveRaceAsync(projectId, key, inputHash, command.OperationId, cancellationToken)
                .ConfigureAwait(false);
        }

        notifier.Publish(ProjectionChange.Project(projectId.Value));
        return await MapOperationAsync(projectId, command.OperationId, cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureRecoveryOwnedAssignment(
        RecoveryTargetRow target,
        ExecutionAssignment live)
    {
        if (live.BindingValidationRevisionSnapshot != target.BindingRevision)
        {
            throw new RecoveryNotReadyException(
                $"Assignment '{target.RequestId}' binding revision does not match the captured target.");
        }

        if (IsTerminal(live.State))
        {
            throw new RecoveryNotReadyException(
                $"Assignment '{target.RequestId}' is already terminal and cannot be recovered.");
        }

        if (!Enum.TryParse<ExecutionAssignmentState>(target.CapturedState, out var captured))
        {
            throw new RecoveryNotReadyException(
                $"Assignment '{target.RequestId}' captured state is invalid.");
        }

        if (IsTerminal(captured))
        {
            throw new RecoveryNotReadyException(
                $"Assignment '{target.RequestId}' captured a terminal state without a resolved outcome.");
        }

        var expectedVersion = captured == ExecutionAssignmentState.Cancelling
            ? target.CapturedVersion
            : target.CapturedVersion + 1;
        if (live.State != ExecutionAssignmentState.Cancelling || live.Version != expectedVersion)
        {
            throw new RecoveryNotReadyException(
                $"Assignment '{target.RequestId}' revision is stale for manual recovery.");
        }
    }

    private static void ValidateConfirmations(ConfirmManualProjectRecoveryCommand command)
    {
        if (!command.ConfirmOriginalExecutionCannotResume
            || !command.WriterAccessPrevented
            || !command.AcknowledgeEvidenceGaps)
        {
            throw new RecoveryNotReadyException(
                "Manual recovery requires writer-access prevention and explicit confirmations; uncertainty cannot be waived.");
        }
    }

    private static void ValidateRepositoryCollectionTime(DateTimeOffset collectedAt, DateTimeOffset now)
    {
        var at = collectedAt.ToUniversalTime();
        if (at > now)
        {
            throw new RecoveryNotReadyException("Repository status collection time must not be in the future.");
        }

        if (now - at > MaxRepositoryEvidenceAge)
        {
            throw new RecoveryNotReadyException("Repository status snapshot is stale.");
        }
    }

    private static void CancelNonterminal(
        ExecutionAssignment assignment,
        WorkRequest request,
        DateTimeOffset now)
    {
        if (IsTerminal(assignment.State))
        {
            return;
        }

        if (assignment.State != ExecutionAssignmentState.Cancelling)
        {
            assignment.BeginCancelling(now);
        }

        assignment.Cancel(now);

        if (request.Status is not WorkRequestStatus.Completed
            and not WorkRequestStatus.Failed
            and not WorkRequestStatus.Cancelled)
        {
            if (request.Status != WorkRequestStatus.Cancelling)
            {
                request.BeginCancelling(now);
            }

            request.ConfirmCancellation(now);
        }
    }

    private async Task<ProjectFencingTokenRow> LoadOrCreateFenceAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var fence = await db.ProjectFencingTokens
            .SingleOrDefaultAsync(row => row.ProjectId == projectId, cancellationToken)
            .ConfigureAwait(false);
        if (fence is not null)
        {
            return fence;
        }

        fence = new ProjectFencingTokenRow
        {
            ProjectId = projectId,
            LastFencingToken = 0,
        };
        db.ProjectFencingTokens.Add(fence);
        return fence;
    }

    private async Task<ProjectRecoveryOperation> ResolveRaceAsync(
        ProjectId projectId,
        string key,
        string inputHash,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var existingKey = await db.Set<RecoveryIdempotencyRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.ProjectId == projectId.Value && row.Action == ConfirmManualAction && row.Key == key,
                cancellationToken)
            .ConfigureAwait(false);
        if (existingKey is not null)
        {
            if (!string.Equals(existingKey.InputHash, inputHash, StringComparison.Ordinal))
            {
                throw new RecoveryIdempotencyConflictException(projectId, ConfirmManualAction, key);
            }

            return await MapOperationAsync(
                    projectId,
                    existingKey.OperationId ?? operationId,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        throw new RecoveryRevisionConflictException(projectId, operationId, 0);
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
            Enum.Parse<RecoveryOperationStatus>(row.Status),
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

    private static ReservationLease ToAggregate(ReservationLeaseRow row) => ReservationLease.Rehydrate(
        row.Id,
        new ProjectId(row.ProjectId),
        new WorkRequestId(row.RequestId),
        row.OwnerSessionId,
        row.Reason,
        row.FencingToken,
        Enum.Parse<ReservationLeaseState>(row.State),
        row.Scopes
            .Select(scope => ReservationScope.Create((ReservationScopeKind)scope.Kind, scope.Path))
            .ToList(),
        new DateTimeOffset(row.AcquiredAtUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(row.LastRenewedAtUtcTicks, TimeSpan.Zero),
        new DateTimeOffset(row.ExpiresAtUtcTicks, TimeSpan.Zero),
        row.ReleasedAtUtcTicks.HasValue
            ? new DateTimeOffset(row.ReleasedAtUtcTicks.Value, TimeSpan.Zero)
            : null,
        row.Version);

    private static void ApplyToRow(ReservationLease lease, ReservationLeaseRow row)
    {
        row.OwnerSessionId = lease.OwnerSessionId;
        row.FencingToken = lease.FencingToken;
        row.State = lease.State.ToString();
        row.LastRenewedAtUtcTicks = lease.LastRenewedAt.UtcTicks;
        row.ExpiresAtUtcTicks = lease.ExpiresAt.UtcTicks;
        row.ReleasedAtUtcTicks = lease.ReleasedAt?.UtcTicks;
        row.Version = lease.Version;
    }

    private static string HashInput(ConfirmManualProjectRecoveryCommand command)
    {
        var payload = string.Join(
            "\n",
            [
                command.OperationId.ToString("D"),
                command.ExpectedOperationVersion.ToString(),
                command.ExpectedAttempt.ToString(),
                command.ExactProjectName.Trim(),
                command.Reason.Trim(),
                command.Actor.Trim(),
                command.ConfirmOriginalExecutionCannotResume ? "1" : "0",
                command.WriterAccessPrevented ? "1" : "0",
                command.AcknowledgeEvidenceGaps ? "1" : "0",
                command.ProcessStopEvidence.Trim(),
                command.RepositoryStatusSnapshot.Trim(),
                command.RepositoryStatusSource.Trim(),
                command.RepositoryCollectedAt.ToUniversalTime().UtcTicks.ToString(),
                command.ReservationAndEventGapAccounting.Trim(),
            ]);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void RequireNonBlank(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{name} must be non-blank.", name);
        }
    }

    private static string RequireBound(string value, string name, int max = MaxTextLength)
    {
        if (value.Length > max)
        {
            throw new ArgumentException($"{name} exceeds the maximum length of {max}.", name);
        }

        return value;
    }

    private static string BoundJson(string json)
    {
        if (json.Length > MaxEvidenceJsonLength)
        {
            throw new RecoveryNotReadyException(
                "Operator evidence exceeds the maximum stored length.");
        }

        return json;
    }

    private static bool IsResolved(string? outcome) => !string.IsNullOrEmpty(outcome);

    private static string SerializeEvidence(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static bool IsTerminal(ExecutionAssignmentState state) =>
        state is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;

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
}
