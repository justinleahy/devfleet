using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Persistence;
using ReservationLease = PiCommandCenter.Domain.Reservations.ReservationLease;
using ReservationScope = PiCommandCenter.Domain.Reservations.ReservationScope;

namespace PiCommandCenter.Infrastructure.Reservations;

/// <summary>
/// EF Core SQLite implementation of the Control Plane reservation authority. Acquire and
/// expand run conflict detection and fencing-token increments inside one transaction with
/// an optimistic counter update, so concurrent acquisitions resolve deterministically to
/// exactly one winner per scope.
/// </summary>
public sealed class ReservationService(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier,
    ILogger<ReservationService>? logger = null) : IReservationService
{
    private readonly ILogger _logger = logger ?? NullLogger<ReservationService>.Instance;

    public static readonly TimeSpan DefaultLeaseDuration = TimeSpan.FromSeconds(120);

    public async Task<ReservationLeaseDto> AcquireAsync(
        AcquireReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var projectId = RequireProjectId(command.ProjectId);
        var requestId = RequireRequestId(command.RequestId);
        var owner = RequireSession(command.OwnerSessionId);
        var reason = RequireReason(command.Reason);
        var scopes = MapScopes(command.Scopes);

        // Expiration sweep is idempotent and committed first so expired leases durably
        // enter recovery even when the acquisition itself is denied.
        await SweepExpiredLeasesAsync(projectId.Value, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conflicts = await FindConflictsAsync(projectId.Value, scopes, excludeLeaseId: null, cancellationToken);
        if (conflicts.Count > 0)
        {
            throw new ReservationConflictException(conflicts);
        }

        var fencingToken = await NextFencingTokenAsync(projectId.Value, cancellationToken);
        var lease = ReservationLease.Acquire(
            Guid.NewGuid(),
            projectId,
            requestId,
            owner,
            reason,
            fencingToken,
            scopes,
            Now,
            DefaultLeaseDuration);

        await PersistNewLeaseAsync(lease, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Published(ToDto(lease));
    }

    public async Task<ReservationLeaseDto> RenewAsync(
        RenewReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (row, lease) = await LoadTrackedLeaseAsync(command.LeaseId, cancellationToken);

        lease.Renew(RequireSession(command.SessionId), command.FencingToken, Now, DefaultLeaseDuration);
        ApplyToRow(lease, row);
        await db.SaveChangesAsync(cancellationToken);
        return Published(ToDto(lease));
    }

    public async Task<ReservationLeaseDto> ExpandAsync(
        ExpandReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var scopes = MapScopes(command.Scopes);
        var (row, lease) = await LoadTrackedLeaseAsync(command.LeaseId, cancellationToken);

        await SweepExpiredLeasesAsync(lease.ProjectId.Value, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conflicts = await FindConflictsAsync(lease.ProjectId.Value, scopes, lease.Id, cancellationToken);
        AppendSameLeaseProjectBuildConflicts(lease, scopes, conflicts);
        if (conflicts.Count > 0)
        {
            throw new ReservationConflictException(conflicts);
        }

        lease.Expand(scopes, RequireSession(command.SessionId), command.FencingToken, Now, DefaultLeaseDuration);
        ApplyToRow(lease, row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Published(ToDto(lease));
    }

    public async Task<ReservationLeaseDto> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var (row, lease) = await LoadTrackedLeaseAsync(command.LeaseId, cancellationToken);

        lease.Release(RequireSession(command.SessionId), Now);
        ApplyToRow(lease, row);
        await db.SaveChangesAsync(cancellationToken);

        await RecordAuditAsync(
            lease,
            "Released",
            $"Released by {lease.OwnerSessionId}.",
            snapshot: null,
            actor: lease.OwnerSessionId,
            cancellationToken);
        return Published(ToDto(lease));
    }

    public async Task<ReservationLeaseDto> TransferAsync(
        TransferReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var from = RequireSession(command.FromSessionId);
        var to = RequireSession(command.ToSessionId);
        var (row, lease) = await LoadTrackedLeaseAsync(command.LeaseId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var fencingToken = await NextFencingTokenAsync(lease.ProjectId.Value, cancellationToken);

        lease.Transfer(from, to, fencingToken, Now, DefaultLeaseDuration);
        ApplyToRow(lease, row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await RecordAuditAsync(
            lease,
            "Transferred",
            $"Handoff from {from} to {to}.",
            snapshot: null,
            actor: to,
            cancellationToken);
        return Published(ToDto(lease));
    }

    public async Task AuthorizeAsync(
        MutationAuthorizationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!Enum.IsDefined(typeof(MutationOperation), command.Operation))
        {
            throw new ReservationValidationException($"Unknown mutation operation {command.Operation}.");
        }

        var targetKind = ResolveTargetKind(command.TargetPath, command.TargetScopeKind);
        ReservationScope target;
        try
        {
            target = ReservationScope.Create(targetKind, command.TargetPath);
        }
        catch (InvalidReservationScopeException ex)
        {
            throw new ReservationValidationException(ex.Message);
        }

        var lease = await LoadLeaseAsync(command.LeaseId, cancellationToken);
        var request = new MutationAuthorizationRequest(
            RequireSession(command.SessionId),
            command.FencingToken,
            target,
            (MutationOperation)command.Operation);
        lease.Authorize(request, Now);
    }

    public async Task<IReadOnlyList<ReservationLeaseDto>> ListAsync(
        Guid projectId,
        bool includeReleased = false,
        CancellationToken cancellationToken = default)
    {
        var query = db.ReservationLeases
            .AsNoTracking()
            .Include(l => l.Scopes)
            .Where(l => l.ProjectId == projectId);

        if (!includeReleased)
        {
            query = query.Where(l => l.State != nameof(ReservationLeaseState.Released));
        }

        var rows = await query
            .OrderBy(l => l.AcquiredAtUtcTicks)
            .ToListAsync(cancellationToken);

        return rows.Select(row => ToDto(ToAggregate(row))).ToList();
    }

    public async Task<ReservationLeaseDto> MarkRecoveryRequiredAsync(
        MarkRecoveryRequiredCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        RequireReason(command.Reason);
        var (row, lease) = await LoadTrackedLeaseAsync(command.LeaseId, cancellationToken);

        lease.MarkRecoveryRequired(Now);
        ApplyToRow(lease, row);
        await db.SaveChangesAsync(cancellationToken);

        await RecordAuditAsync(
            lease,
            "Expired",
            command.Reason,
            snapshot: null,
            actor: null,
            cancellationToken);
        return Published(ToDto(lease));
    }

    public async Task<ReservationLeaseDto> ForceReleaseAsync(
        ForceReleaseReservationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var reason = RequireReason(command.Reason);
        if (string.IsNullOrWhiteSpace(command.RepositoryStatusSnapshot))
        {
            throw new ReservationValidationException(
                "Force release requires a repository status snapshot for the audit trail.");
        }

        RequireSession(command.RequestedBy);
        var (row, lease) = await LoadTrackedLeaseAsync(command.LeaseId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var fencingToken = await NextFencingTokenAsync(lease.ProjectId.Value, cancellationToken);

        lease.ForceRelease(reason, command.RepositoryStatusSnapshot, fencingToken, Now);
        ApplyToRow(lease, row);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        await RecordAuditAsync(
            lease,
            "ForceReleased",
            reason,
            command.RepositoryStatusSnapshot,
            actor: command.RequestedBy,
            cancellationToken);
        _logger.LogInformation(
            "AUDIT force-release lease={LeaseId} actor={Actor} reason={Reason}",
            lease.Id,
            command.RequestedBy,
            reason);
        return Published(ToDto(lease));
    }

    /// <summary>
    /// Signals live views after a lease mutation has been committed. Reservation state drives
    /// the attention queue, so the fleet-wide page is woken through the request's project.
    /// </summary>
    private ReservationLeaseDto Published(ReservationLeaseDto lease)
    {
        notifier.Publish(ProjectionChange.Request(lease.ProjectId, lease.RequestId));
        return lease;
    }

    private DateTimeOffset Now => clock.GetUtcNow();

    private async Task SweepExpiredLeasesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var nowTicks = Now.UtcTicks;
        var expired = await db.ReservationLeases
            .Where(l => l.ProjectId == projectId
                && l.State == nameof(ReservationLeaseState.Active)
                && l.ExpiresAtUtcTicks <= nowTicks)
            .ToListAsync(cancellationToken);

        foreach (var row in expired)
        {
            row.State = nameof(ReservationLeaseState.RecoveryRequired);
            row.Version++;
            db.ReservationAuditFacts.Add(new ReservationAuditFactRow
            {
                Id = Guid.NewGuid(),
                LeaseId = row.Id,
                ProjectId = row.ProjectId,
                Kind = "Expired",
                Reason = "Lease deadline passed without renewal; recovery inspection required.",
                AtUtcTicks = nowTicks,
            });
        }

        if (expired.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Conflict detection for acquire and expand. Uses <see cref="ReservationScope.ConflictsWith"/>
    /// so <c>project-build</c> excludes every source scope on the same Project in both directions.
    /// </summary>
    private async Task<List<ReservationConflictDto>> FindConflictsAsync(
        Guid projectId,
        IReadOnlyList<ReservationScope> requested,
        Guid? excludeLeaseId,
        CancellationToken cancellationToken)
    {
        // Recovery-required scopes stay blocked: a scope is re-grantable only when the
        // owning lease is Active or already Released (SPEC 17.9).
        var blockingStates = new[]
        {
            nameof(ReservationLeaseState.Active),
            nameof(ReservationLeaseState.RecoveryRequired),
        };
        var activeScopes = await db.ReservationScopes
            .AsNoTracking()
            .Join(
                db.ReservationLeases.AsNoTracking()
                    .Where(l => l.ProjectId == projectId && blockingStates.Contains(l.State)),
                scope => scope.LeaseId,
                lease => lease.Id,
                (scope, lease) => new { Scope = scope, Lease = lease })
            .Where(joined => excludeLeaseId == null || joined.Scope.LeaseId != excludeLeaseId.Value)
            .Select(joined => new { joined.Lease.Id, joined.Lease.OwnerSessionId, joined.Scope.Kind, joined.Scope.Path })
            .ToListAsync(cancellationToken);

        var conflicts = new List<ReservationConflictDto>();
        foreach (var row in activeScopes)
        {
            var existing = ReservationScope.Create((ReservationScopeKind)row.Kind, row.Path);
            foreach (var scope in requested)
            {
                if (ReservationScope.ConflictsWith(existing, scope))
                {
                    conflicts.Add(new ReservationConflictDto(
                        row.Id,
                        row.OwnerSessionId,
                        (int)existing.Kind,
                        existing.Kind.ToString(),
                        existing.Path));
                    break;
                }
            }
        }

        return conflicts;
    }

    private static void AppendSameLeaseProjectBuildConflicts(
        ReservationLease lease,
        IReadOnlyList<ReservationScope> requested,
        List<ReservationConflictDto> conflicts)
    {
        foreach (var existing in lease.Scopes)
        {
            foreach (var scope in requested)
            {
                if (!ReservationScope.IsProjectBuildSourceConflict(existing, scope))
                {
                    continue;
                }

                conflicts.Add(new ReservationConflictDto(
                    lease.Id,
                    lease.OwnerSessionId,
                    (int)existing.Kind,
                    existing.Kind.ToString(),
                    existing.Path));
                return;
            }
        }
    }

    /// <summary>
    /// Increments the project fencing counter with an optimistic guard so that concurrent
    /// transactions serialize: exactly one caller observes each expected value.
    /// </summary>
    private async Task<long> NextFencingTokenAsync(Guid projectId, CancellationToken cancellationToken)
    {
        // Guarded update: exactly one transaction can flip any observed value, and SQLite's
        // single-writer locking plus the upsert below keep increments strictly monotonic
        // even when two acquisitions race on a project's first lease.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await db.ProjectFencingTokens
                .AsNoTracking()
                .SingleOrDefaultAsync(t => t.ProjectId == projectId, cancellationToken);

            var expected = current?.LastFencingToken ?? 0;
            if (expected > 0)
            {
                var updated = await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE ProjectFencingTokens SET LastFencingToken = {expected + 1} WHERE ProjectId = {projectId} AND LastFencingToken = {expected}",
                    cancellationToken);
                if (updated == 1)
                {
                    return expected + 1;
                }

                continue;
            }

            await db.Database.ExecuteSqlInterpolatedAsync(
                $@"INSERT INTO ProjectFencingTokens (ProjectId, LastFencingToken)
VALUES ({projectId}, 1)
ON CONFLICT(ProjectId) DO UPDATE SET LastFencingToken = LastFencingToken + 1",
                cancellationToken);

            var seeded = await db.ProjectFencingTokens
                .AsNoTracking()
                .Where(t => t.ProjectId == projectId)
                .Select(t => (long?)t.LastFencingToken)
                .SingleOrDefaultAsync(cancellationToken);

            if (seeded.HasValue && seeded.Value >= 1)
            {
                return seeded.Value;
            }
        }

        throw new InvalidOperationException(
            "Concurrent fencing-token update could not be resolved; retry the reservation operation.");
    }

    private async Task PersistNewLeaseAsync(ReservationLease lease, CancellationToken cancellationToken)
    {
        var row = new ReservationLeaseRow
        {
            Id = lease.Id,
            ProjectId = lease.ProjectId.Value,
            RequestId = lease.RequestId.Value,
            OwnerSessionId = lease.OwnerSessionId,
            Reason = lease.Reason,
            FencingToken = lease.FencingToken,
            State = lease.State.ToString(),
            AcquiredAtUtcTicks = lease.AcquiredAt.UtcTicks,
            LastRenewedAtUtcTicks = lease.LastRenewedAt.UtcTicks,
            ExpiresAtUtcTicks = lease.ExpiresAt.UtcTicks,
            ReleasedAtUtcTicks = lease.ReleasedAt?.UtcTicks,
            Version = lease.Version,
        };
        row.Scopes.AddRange(lease.Scopes.Select(scope => new ReservationScopeRow
        {
            Id = Guid.NewGuid(),
            LeaseId = lease.Id,
            Kind = (int)scope.Kind,
            Path = scope.Path,
        }));
        db.ReservationLeases.Add(row);

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecordAuditAsync(
        ReservationLease lease,
        string kind,
        string reason,
        string? snapshot,
        string? actor,
        CancellationToken cancellationToken)
    {
        db.ReservationAuditFacts.Add(new ReservationAuditFactRow
        {
            Id = Guid.NewGuid(),
            LeaseId = lease.Id,
            ProjectId = lease.ProjectId.Value,
            Kind = kind,
            Reason = reason,
            RepositoryStatusSnapshot = snapshot,
            Actor = actor,
            AtUtcTicks = Now.UtcTicks,
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ReservationLease> LoadLeaseAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        var row = await db.ReservationLeases
            .AsNoTracking()
            .Include(l => l.Scopes)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw new ReservationNotFoundException(leaseId);

        return ToAggregate(row);
    }

    private async Task<(ReservationLeaseRow Row, ReservationLease Lease)> LoadTrackedLeaseAsync(
        Guid leaseId,
        CancellationToken cancellationToken)
    {
        if (leaseId == Guid.Empty)
        {
            throw new ReservationValidationException("Lease id must not be empty.");
        }

        var row = await db.ReservationLeases
            .Include(l => l.Scopes)
            .SingleOrDefaultAsync(l => l.Id == leaseId, cancellationToken)
            ?? throw new ReservationNotFoundException(leaseId);

        return (row, ToAggregate(row));
    }

    private static ReservationLease ToAggregate(ReservationLeaseRow row) => ReservationLease.Rehydrate(
        row.Id,
        new ProjectId(row.ProjectId),
        new WorkRequestId(row.RequestId),
        row.OwnerSessionId,
        row.Reason,
        row.FencingToken,
        ParseState(row.State),
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

    /// <summary>Copies validated aggregate mutations back onto the EF-tracked row.</summary>
    private void ApplyToRow(ReservationLease lease, ReservationLeaseRow row)
    {
        row.OwnerSessionId = lease.OwnerSessionId;
        row.FencingToken = lease.FencingToken;
        row.State = lease.State.ToString();
        row.LastRenewedAtUtcTicks = lease.LastRenewedAt.UtcTicks;
        row.ExpiresAtUtcTicks = lease.ExpiresAt.UtcTicks;
        row.ReleasedAtUtcTicks = lease.ReleasedAt?.UtcTicks;
        row.Version = lease.Version;

        var existing = row.Scopes.ToDictionary(scope => (scope.Kind, scope.Path));
        foreach (var scope in lease.Scopes)
        {
            var key = ((int)scope.Kind, scope.Path);
            if (!existing.ContainsKey(key))
            {
                var scopeRow = new ReservationScopeRow
                {
                    Id = Guid.NewGuid(),
                    LeaseId = lease.Id,
                    Kind = (int)scope.Kind,
                    Path = scope.Path,
                };
                // DbSet.Add is required: a row appended only to the tracked navigation
                // would be graph-discovered as an existing entity (preset key) and saved
                // as an UPDATE that matches zero rows.
                db.ReservationScopes.Add(scopeRow);
                row.Scopes.Add(scopeRow);
            }
        }
    }

    private static ReservationLeaseState ParseState(string state) =>
        Enum.TryParse<ReservationLeaseState>(state, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Unknown lease state '{state}'.");

    private static ReservationLeaseDto ToDto(ReservationLease lease) => new(
        lease.Id,
        lease.ProjectId.Value,
        lease.RequestId.Value,
        lease.OwnerSessionId,
        lease.FencingToken,
        (int)lease.State,
        lease.State.ToString(),
        lease.Reason,
        lease.AcquiredAt,
        lease.ExpiresAt,
        lease.ReleasedAt,
        lease.Scopes.Select(scope => new ReservationScopeDto(
            (int)scope.Kind,
            scope.Kind.ToString(),
            scope.Path)).ToList());

    private static IReadOnlyList<ReservationScope> MapScopes(IReadOnlyList<ReservationScopeDto> scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ReservationValidationException("At least one scope is required.");
        }

        return scopes.Select(ReservationScopeMapper.ToDomain).ToList();
    }

    private static ReservationScopeKind ResolveTargetKind(string targetPath, string? targetScopeKind)
    {
        if (!string.IsNullOrWhiteSpace(targetScopeKind)
            && Enum.TryParse<ReservationScopeKind>(targetScopeKind, ignoreCase: true, out var kind))
        {
            return kind;
        }

        // A trailing slash is the unambiguous directory marker; a target whose exact file
        // path was reserved still authorizes via File equality, so default to File.
        return targetPath.EndsWith('/') ? ReservationScopeKind.Directory : ReservationScopeKind.File;
    }

    private static ProjectId RequireProjectId(Guid projectId) =>
        projectId != Guid.Empty
            ? new ProjectId(projectId)
            : throw new ReservationValidationException("Project id must not be empty.");

    private static WorkRequestId RequireRequestId(Guid requestId) =>
        requestId != Guid.Empty
            ? new WorkRequestId(requestId)
            : throw new ReservationValidationException("Request id must not be empty.");

    private static string RequireSession(string sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId)
            ? sessionId.Trim()
            : throw new ReservationValidationException("Session id must not be empty.");

    private static string RequireReason(string reason) =>
        !string.IsNullOrWhiteSpace(reason)
            ? reason.Trim()
            : throw new ReservationValidationException("A non-empty reason is required.");
}
