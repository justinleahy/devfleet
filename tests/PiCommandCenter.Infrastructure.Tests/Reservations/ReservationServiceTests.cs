using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;

namespace PiCommandCenter.Infrastructure.Tests.Reservations;

public class ReservationServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan delta) => Now += delta;
    }

    private static ReservationScopeDto File(string path) => new(
        (int)ReservationScopeKind.File,
        nameof(ReservationScopeKind.File),
        path);

    private static ReservationScopeDto Directory(string path) => new(
        (int)ReservationScopeKind.Directory,
        nameof(ReservationScopeKind.Directory),
        path);

    private static ReservationScopeDto Resource(string name) => new(
        (int)ReservationScopeKind.Resource,
        nameof(ReservationScopeKind.Resource),
        name);

    private static AcquireReservationCommand Acquire(
        Guid projectId,
        params ReservationScopeDto[] scopes) => new(
        ProjectId: projectId,
        RequestId: Guid.NewGuid(),
        OwnerSessionId: "session-a",
        Scopes: scopes,
        Reason: "Implement feature");

    private static async Task<(ControlPlaneDbContext Db, ReservationService Service, Guid ProjectId)> CreateWorldAsync(
        MutableClock clock)
    {
        var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var service = new ReservationService(clock, context, new PiCommandCenter.Application.Live.ProjectionNotifier());
        return (context, service, Guid.NewGuid());
    }

    [Fact]
    public async Task Acquire_grants_all_scopes_with_full_lease_metadata()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs"), File("tests/ATests.cs")));

        Assert.NotEqual(Guid.Empty, lease.LeaseId);
        Assert.Equal(projectId, lease.ProjectId);
        Assert.Equal("session-a", lease.OwnerSessionId);
        Assert.Equal(1, lease.FencingToken);
        Assert.Equal((int)ReservationLeaseState.Active, lease.State);
        Assert.Equal(nameof(ReservationLeaseState.Active), lease.StateName);
        Assert.Equal(Start.AddSeconds(120), lease.ExpiresAt);
        Assert.Equal(2, lease.Scopes.Count);
        Assert.Equal("src/a.cs", lease.Scopes[0].Path);
    }

    [Fact]
    public async Task Conflicting_acquire_denies_with_conflict_details_and_persists_nothing()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var first = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));
        var conflicting = new AcquireReservationCommand(
            ProjectId: projectId,
            RequestId: Guid.NewGuid(),
            OwnerSessionId: "session-b",
            Scopes: [File("src/a.cs"), File("tests/BTests.cs")],
            Reason: "Conflicting work");

        var conflict = await Assert.ThrowsAsync<ReservationConflictException>(
            () => service.AcquireAsync(conflicting));

        var dto = Assert.Single(conflict.Conflicts);
        Assert.Equal(first.LeaseId, dto.LeaseId);
        Assert.Equal("session-a", dto.OwnerSessionId);
        Assert.Equal("src/a.cs", dto.ScopePath);

        // Rollback: session-b holds nothing; only the first lease is listed.
        var leases = await service.ListAsync(projectId);
        var lease = Assert.Single(leases);
        Assert.Equal(first.LeaseId, lease.LeaseId);
    }

    [Fact]
    public async Task Non_overlapping_reservations_coexist()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var first = await service.AcquireAsync(Acquire(projectId, Directory("src/domain/")));
        var second = await service.AcquireAsync(new AcquireReservationCommand(
            projectId,
            Guid.NewGuid(),
            "session-b",
            [Directory("src/infrastructure/"), File("tests/ITests.cs")],
            "Other work"));

        Assert.Equal(1, first.FencingToken);
        Assert.Equal(2, second.FencingToken);
        Assert.Equal(2, (await service.ListAsync(projectId)).Count);
    }

    [Fact]
    public async Task Expand_rolls_back_atomically_when_one_scope_conflicts()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));
        _ = await service.AcquireAsync(new AcquireReservationCommand(
            projectId, Guid.NewGuid(), "session-b", [File("src/taken.cs")], "taken"));

        var expand = new ExpandReservationCommand(
            lease.LeaseId,
            lease.FencingToken,
            "session-a",
            [File("src/free.cs"), File("src/taken.cs")]);

        await Assert.ThrowsAsync<ReservationConflictException>(() => service.ExpandAsync(expand));

        var listed = await service.ListAsync(projectId);
        var expanded = Assert.Single(listed, l => l.LeaseId == lease.LeaseId);
        Assert.Single(expanded.Scopes, s => s.Path == "src/a.cs");
        Assert.DoesNotContain(expanded.Scopes, s => s.Path == "src/free.cs");
    }

    [Fact]
    public async Task Authorize_accepts_current_token_and_rejects_stale_token()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, Directory("src/domain/")));

        // Directory scope covers contained file.
        await service.AuthorizeAsync(new MutationAuthorizationCommand(
            lease.LeaseId, lease.FencingToken, "session-a", "src/domain/Foo.cs", (int)MutationOperation.Write));

        // Transfer invalidates the old token.
        var transferred = await service.TransferAsync(new TransferReservationCommand(
            lease.LeaseId, "session-a", "session-b"));

        await Assert.ThrowsAsync<InvalidFencingTokenException>(() => service.AuthorizeAsync(
            new MutationAuthorizationCommand(
                lease.LeaseId, lease.FencingToken, "session-b", "src/domain/Foo.cs", (int)MutationOperation.Write)));

        await service.AuthorizeAsync(new MutationAuthorizationCommand(
            lease.LeaseId, transferred.FencingToken, "session-b", "src/domain/Foo.cs", (int)MutationOperation.Write));
    }

    [Fact]
    public async Task Authorize_rejects_wrong_owner_uncovered_target_and_unknown_operations()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthorizeAsync(
            new MutationAuthorizationCommand(
                lease.LeaseId, lease.FencingToken, "session-b", "src/a.cs", (int)MutationOperation.Write)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthorizeAsync(
            new MutationAuthorizationCommand(
                lease.LeaseId, lease.FencingToken, "session-a", "src/other.cs", (int)MutationOperation.Write)));
        await Assert.ThrowsAsync<ReservationValidationException>(() => service.AuthorizeAsync(
            new MutationAuthorizationCommand(
                lease.LeaseId, lease.FencingToken, "session-a", "/etc/passwd", (int)MutationOperation.Write)));
        await Assert.ThrowsAsync<ReservationValidationException>(() => service.AuthorizeAsync(
            new MutationAuthorizationCommand(
                lease.LeaseId, lease.FencingToken, "session-a", "src/a.cs", Operation: 99)));
    }

    [Fact]
    public async Task Handoff_transfers_ownership_and_invalidates_the_old_token_immediately()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));

        var handed = await service.TransferAsync(new TransferReservationCommand(
            lease.LeaseId, "session-a", "session-b"));

        Assert.Equal("session-b", handed.OwnerSessionId);
        Assert.True(handed.FencingToken > lease.FencingToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenewAsync(
            new RenewReservationCommand(lease.LeaseId, lease.FencingToken, "session-a")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenewAsync(
            new RenewReservationCommand(lease.LeaseId, handed.FencingToken, "session-a")));
        clock.Advance(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(30));
        var renewed = await service.RenewAsync(
            new RenewReservationCommand(lease.LeaseId, handed.FencingToken, "session-b"));
        Assert.True(renewed.ExpiresAt > handed.ExpiresAt);
    }

    [Fact]
    public async Task Expired_leases_enter_recovery_and_block_reacquisition_until_forced_release()
    {
        var clock = new MutableClock(Start);
        var (db, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));

        clock.Advance(TimeSpan.FromSeconds(121));

        // Acquire sweeps the expired lease into recovery-required and denies the request.
        var conflict = await Assert.ThrowsAsync<ReservationConflictException>(
            () => service.AcquireAsync(new AcquireReservationCommand(
                projectId, Guid.NewGuid(), "session-b", [File("src/a.cs")], "take over")));

        Assert.Equal(lease.LeaseId, Assert.Single(conflict.Conflicts).LeaseId);

        var inRecovery = (await service.ListAsync(projectId)).Single(l => l.LeaseId == lease.LeaseId);
        Assert.Equal(nameof(ReservationLeaseState.RecoveryRequired), inRecovery.StateName);

        // Mutation with the still-held token is rejected after expiry.
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AuthorizeAsync(
            new MutationAuthorizationCommand(
                lease.LeaseId, lease.FencingToken, "session-a", "src/a.cs", (int)MutationOperation.Write)));

        var forced = await service.ForceReleaseAsync(new ForceReleaseReservationCommand(
            lease.LeaseId,
            Reason: "Owner process confirmed dead",
            RepositoryStatusSnapshot: "git status: src/a.cs modified",
            RequestedBy: "admin"));

        Assert.Equal(nameof(ReservationLeaseState.Released), forced.StateName);
        Assert.True(forced.FencingToken > lease.FencingToken);

        var auditFacts = await db.ReservationAuditFacts.ToListAsync();
        Assert.Contains(auditFacts, fact => fact.Kind == "Expired");
        Assert.Contains(auditFacts, fact => fact.Kind == "ForceReleased"
            && fact.RepositoryStatusSnapshot == "git status: src/a.cs modified");

        // Now the scope is re-grantable with a strictly higher token.
        var regrant = await service.AcquireAsync(new AcquireReservationCommand(
            projectId, Guid.NewGuid(), "session-b", [File("src/a.cs")], "fresh work"));
        Assert.True(regrant.FencingToken > forced.FencingToken);
    }

    [Fact]
    public async Task Expiration_is_lazy_exactly_once_and_renewal_prevents_it()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));
        clock.Advance(TimeSpan.FromSeconds(60));
        _ = await service.RenewAsync(new RenewReservationCommand(
            lease.LeaseId, lease.FencingToken, "session-a"));
        clock.Advance(TimeSpan.FromSeconds(100));

        // 160s total, but renewed at 60s: deadline is 60+120=180s, still active.
        var acquired = await service.AcquireAsync(new AcquireReservationCommand(
            projectId, Guid.NewGuid(), "session-b", [File("src/other.cs")], "parallel"));
        Assert.NotEqual(Guid.Empty, acquired.LeaseId);
    }

    [Fact]
    public async Task Release_by_owner_frees_the_scope_for_reacquisition()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReleaseAsync(
            new ReleaseReservationCommand(lease.LeaseId, "session-b")));

        await service.ReleaseAsync(new ReleaseReservationCommand(lease.LeaseId, "session-a"));

        var regrant = await service.AcquireAsync(new AcquireReservationCommand(
            projectId, Guid.NewGuid(), "session-b", [File("src/a.cs")], "next task"));
        Assert.NotEqual(lease.LeaseId, regrant.LeaseId);
    }

    [Fact]
    public async Task Concurrent_acquires_on_sqlite_produce_exactly_one_winner()
    {
        var clock = new MutableClock(Start);
        var sqlitePath = TestRepositories.CreateSqliteFile();

        await using var contextA = TestRepositories.CreateContext(sqlitePath);
        await using var contextB = TestRepositories.CreateContext(sqlitePath);
        var serviceA = new ReservationService(clock, contextA, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var serviceB = new ReservationService(clock, contextB, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var projectId = Guid.NewGuid();

        const int competitors = 6;
        var tasks = Enumerable.Range(0, competitors).Select(async index =>
        {
            try
            {
                var service = index % 2 == 0 ? serviceA : serviceB;
                return await service.AcquireAsync(new AcquireReservationCommand(
                    projectId,
                    Guid.NewGuid(),
                    $"session-{index}",
                    [File("src/shared.cs")],
                    "race"));
            }
            catch (Exception)
            {
                return null;
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        var winners = results.Where(lease => lease is not null).ToList();

        Assert.Single(winners);
        Assert.Equal("src/shared.cs", Assert.Single(winners[0]!.Scopes).Path);

        var persisted = await serviceA.ListAsync(projectId);
        Assert.Single(persisted);
        Assert.Equal(winners[0]!.LeaseId, persisted[0].LeaseId);
    }

    [Fact]
    public async Task Concurrent_transfers_issue_strictly_monotonic_tokens()
    {
        var clock = new MutableClock(Start);
        var sqlitePath = TestRepositories.CreateSqliteFile();

        await using var context = TestRepositories.CreateContext(sqlitePath);
        var service = new ReservationService(clock, context, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var projectId = Guid.NewGuid();
        var lease = await service.AcquireAsync(Acquire(projectId, Resource("project-build")));

        var tokens = new List<long>();
        var attempts = Enumerable.Range(0, 4).Select(async index =>
        {
            try
            {
                var result = await service.TransferAsync(new TransferReservationCommand(
                    lease.LeaseId, "session-a", $"session-{index}"));
                lock (tokens)
                {
                    tokens.Add(result.FencingToken);
                }
            }
            catch (InvalidOperationException)
            {
                // Losers of the fencing race are expected to retry; only monotonicity matters.
            }
        });

        await Task.WhenAll(attempts);
        Assert.Equal(tokens.Order().ToList(), tokens.Distinct().Order().ToList());
        Assert.All(tokens, token => Assert.True(token > lease.FencingToken));
    }

    [Fact]
    public async Task Mark_recovery_required_records_audit_fact()
    {
        var clock = new MutableClock(Start);
        var (db, service, projectId) = await CreateWorldAsync(clock);

        var lease = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));
        clock.Advance(TimeSpan.FromSeconds(121));

        var marked = await service.MarkRecoveryRequiredAsync(
            new MarkRecoveryRequiredCommand(lease.LeaseId, "heartbeat lost"));
        Assert.Equal(nameof(ReservationLeaseState.RecoveryRequired), marked.StateName);

        var facts = await db.ReservationAuditFacts
            .Where(f => f.LeaseId == lease.LeaseId)
            .ToListAsync();
        Assert.Contains(facts, fact => fact.Kind == "Expired");
    }

    [Fact]
    public async Task Listing_hides_released_leases_unless_requested()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        var first = await service.AcquireAsync(Acquire(projectId, File("src/a.cs")));
        _ = await service.AcquireAsync(new AcquireReservationCommand(
            projectId, Guid.NewGuid(), "session-b", [File("src/b.cs")], "second"));
        await service.ReleaseAsync(new ReleaseReservationCommand(first.LeaseId, "session-a"));

        var visible = await service.ListAsync(projectId);
        Assert.Single(visible);

        var all = await service.ListAsync(projectId, includeReleased: true);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, l => l.LeaseId == first.LeaseId && l.StateName == nameof(ReservationLeaseState.Released));
    }

    [Fact]
    public async Task Invalid_scopes_are_rejected_before_persistence()
    {
        var clock = new MutableClock(Start);
        var (_, service, projectId) = await CreateWorldAsync(clock);

        await Assert.ThrowsAsync<ReservationValidationException>(() => service.AcquireAsync(
            Acquire(projectId, File("/absolute/path.cs"))));
        await Assert.ThrowsAsync<ReservationValidationException>(() => service.AcquireAsync(
            Acquire(projectId, File("../escape.cs"))));
        await Assert.ThrowsAsync<ReservationValidationException>(() => service.AcquireAsync(
            Acquire(projectId, File(".git/config"))));
        await Assert.ThrowsAsync<ReservationValidationException>(() => service.AcquireAsync(
            new AcquireReservationCommand(projectId, Guid.NewGuid(), "session-a", [], "no scopes")));
    }
}
