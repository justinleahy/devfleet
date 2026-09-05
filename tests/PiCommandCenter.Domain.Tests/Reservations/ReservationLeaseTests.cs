using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.Domain.Tests.Reservations;

public class ReservationLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 20, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(120);

    private static ReservationLease NewLease(
        string owner = "session-a",
        long token = 1) => ReservationLease.Acquire(
            Guid.NewGuid(),
            ProjectId.New(),
            WorkRequestId.New(),
            owner,
            "Implement domain model",
            token,
            [ReservationScope.Create(ReservationScopeKind.File, "src/a.cs")],
            Now,
            Ttl);

    [Fact]
    public void Acquire_starts_active_with_full_ttl_and_one_scope()
    {
        var lease = NewLease();

        Assert.Equal(ReservationLeaseState.Active, lease.State);
        Assert.Equal(1, lease.FencingToken);
        Assert.Equal(Now.Add(Ttl), lease.ExpiresAt);
        Assert.Single(lease.Scopes);
        Assert.Equal(1, lease.Version);
    }

    [Fact]
    public void Renew_by_owner_with_current_token_extends_the_deadline()
    {
        var lease = NewLease();
        var later = Now.AddSeconds(30);

        lease.Renew("session-a", 1, later, Ttl);

        Assert.Equal(later.Add(Ttl), lease.ExpiresAt);
        Assert.Equal(later, lease.LastRenewedAt);
    }

    [Fact]
    public void Stale_token_is_rejected_on_renew_and_authorize()
    {
        var lease = NewLease();
        lease.Transfer("session-a", "session-b", 2, Now, Ttl);

        Assert.Throws<InvalidFencingTokenException>(
            () => lease.Renew("session-b", 1, Now.AddSeconds(1), Ttl));

        var request = new MutationAuthorizationRequest(
            "session-b",
            1,
            ReservationScope.Create(ReservationScopeKind.File, "src/a.cs"),
            MutationOperation.Write);
        Assert.Throws<InvalidFencingTokenException>(() => lease.Authorize(request, Now.AddSeconds(1)));
    }

    [Fact]
    public void Wrong_owner_is_rejected_even_with_current_token()
    {
        var lease = NewLease();

        Assert.Throws<InvalidOperationException>(
            () => lease.Renew("session-b", 1, Now, Ttl));
        Assert.Throws<InvalidOperationException>(() => lease.Release("session-b", Now));
    }

    [Fact]
    public void Transfer_is_atomic_ownership_change_with_strictly_monotonic_token()
    {
        var lease = NewLease();
        var later = Now.AddSeconds(10);

        lease.Transfer("session-a", "session-b", 7, later, Ttl);

        Assert.Equal("session-b", lease.OwnerSessionId);
        Assert.Equal(7, lease.FencingToken);
        Assert.Equal(ReservationLeaseState.Active, lease.State);
        Assert.Equal(later.Add(Ttl), lease.ExpiresAt);
    }

    [Fact]
    public void Transfer_rejects_non_owners_and_non_monotonic_tokens()
    {
        var lease = NewLease();

        Assert.Throws<InvalidOperationException>(
            () => lease.Transfer("session-b", "session-c", 5, Now, Ttl));
        Assert.Throws<InvalidOperationException>(
            () => lease.Transfer("session-a", "session-b", 1, Now, Ttl));
        Assert.Equal("session-a", lease.OwnerSessionId);
    }

    [Fact]
    public void Release_sets_released_state_and_timestamp()
    {
        var lease = NewLease();
        var later = Now.AddMinutes(1);

        lease.Release("session-a", later);

        Assert.Equal(ReservationLeaseState.Released, lease.State);
        Assert.Equal(later, lease.ReleasedAt);
    }

    [Fact]
    public void Expired_active_leases_can_be_marked_recovery_required_but_not_early()
    {
        var lease = NewLease();

        Assert.Throws<InvalidLeaseStateException>(() => lease.MarkRecoveryRequired(Now.AddSeconds(119)));

        lease.MarkRecoveryRequired(Now.Add(Ttl).AddSeconds(1));
        Assert.Equal(ReservationLeaseState.RecoveryRequired, lease.State);
        Assert.ThrowsAny<InvalidOperationException>(() => lease.Release("session-a", Now));
        Assert.ThrowsAny<InvalidOperationException>(() => lease.Transfer("session-a", "session-b", 2, Now, Ttl));
    }

    [Fact]
    public void Force_release_requires_reason_snapshot_and_token_increment()
    {
        var lease = NewLease();

        Assert.Throws<InvalidOperationException>(
            () => lease.ForceRelease("", "dirty", 2, Now));
        Assert.Throws<InvalidOperationException>(
            () => lease.ForceRelease("admin override", "  ", 2, Now));
        Assert.Throws<InvalidOperationException>(
            () => lease.ForceRelease("admin override", "dirty", 1, Now));

        lease.ForceRelease("admin override", "git status: 3 modified files", 2, Now);

        Assert.Equal(ReservationLeaseState.Released, lease.State);
        Assert.Equal(2, lease.FencingToken);
        Assert.NotNull(lease.ReleasedAt);
    }

    [Fact]
    public void Authorize_rejects_expired_state_and_uncovered_targets()
    {
        var lease = NewLease();

        var uncovered = new MutationAuthorizationRequest(
            "session-a", 1, ReservationScope.Create(ReservationScopeKind.File, "src/other.cs"), MutationOperation.Edit);
        Assert.Throws<InvalidOperationException>(() => lease.Authorize(uncovered, Now));

        var covered = new MutationAuthorizationRequest(
            "session-a", 1, ReservationScope.Create(ReservationScopeKind.File, "src/a.cs"), MutationOperation.Edit);
        lease.Authorize(covered, Now.AddSeconds(119));

        Assert.Throws<InvalidOperationException>(() => lease.Authorize(covered, Now.Add(Ttl).AddTicks(1)));
    }

    [Fact]
    public void Expand_adds_scopes_and_refreshes_deadline_without_changing_token()
    {
        var lease = NewLease();
        var later = Now.AddSeconds(60);

        lease.Expand(
            [ReservationScope.Create(ReservationScopeKind.File, "tests/ATests.cs")],
            "session-a",
            1,
            later,
            Ttl);

        Assert.Equal(2, lease.Scopes.Count);
        Assert.Equal(1, lease.FencingToken);
        Assert.Equal(later.Add(Ttl), lease.ExpiresAt);
    }
}
