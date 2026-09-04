using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Tests.Requests;

public class RequestClaimTests
{
    private static readonly DateTimeOffset ClaimedAt = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    private static RequestClaim CreateClaim(
        NodeId? nodeId = null,
        string claimToken = "token-1",
        DateTimeOffset? claimedAt = null) =>
        RequestClaim.Create(
            WorkRequestId.New(),
            ProjectId.New(),
            nodeId ?? new NodeId(Guid.NewGuid()),
            claimToken,
            claimedAt ?? ClaimedAt,
            Lease);

    [Fact]
    public void Create_stamps_the_expiry_from_the_lease()
    {
        var claim = CreateClaim();

        Assert.Equal(ClaimedAt + Lease, claim.LeaseExpiresAt);
        Assert.Equal(1, claim.Version);
        Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));
    }

    [Fact]
    public void Renew_by_the_owning_node_extends_the_lease()
    {
        var claim = CreateClaim();
        var renewedAt = ClaimedAt.AddMinutes(2);

        var expiry = claim.Renew(claim.NodeId, claim.ClaimToken, Lease, renewedAt);

        Assert.Equal(renewedAt + Lease, expiry);
        Assert.Equal(renewedAt + Lease, claim.LeaseExpiresAt);
        Assert.Equal(2, claim.Version);
    }

    [Fact]
    public void Renew_rejects_a_foreign_node()
    {
        var claim = CreateClaim();

        Assert.Throws<InvalidOperationException>(() =>
            claim.Renew(new NodeId(Guid.NewGuid()), claim.ClaimToken, Lease, ClaimedAt.AddMinutes(1)));
    }

    [Fact]
    public void Renew_rejects_a_wrong_token()
    {
        var claim = CreateClaim();

        Assert.Throws<InvalidOperationException>(() =>
            claim.Renew(claim.NodeId, "not-the-token", Lease, ClaimedAt.AddMinutes(1)));
    }

    [Fact]
    public void Renew_rejects_an_already_expired_claim()
    {
        var claim = CreateClaim();

        Assert.Throws<InvalidOperationException>(() =>
            claim.Renew(claim.NodeId, claim.ClaimToken, Lease, claim.LeaseExpiresAt.AddSeconds(1)));
    }

    [Fact]
    public void Renew_rejects_a_non_positive_lease()
    {
        var claim = CreateClaim();

        Assert.Throws<ArgumentException>(() =>
            claim.Renew(claim.NodeId, claim.ClaimToken, TimeSpan.Zero, ClaimedAt.AddMinutes(1)));
    }

    [Fact]
    public void Rehydrate_rejects_an_lease_that_expires_before_it_started()
    {
        Assert.Throws<ArgumentException>(() =>
            RequestClaim.Rehydrate(
                WorkRequestId.New(),
                ProjectId.New(),
                new NodeId(Guid.NewGuid()),
                "token-1",
                ClaimedAt,
                ClaimedAt.AddSeconds(-1),
                version: 1));
    }

    [Fact]
    public void Rehydrate_restores_the_persisted_claim_without_mutating_version()
    {
        var requestId = WorkRequestId.New();
        var projectId = ProjectId.New();
        var nodeId = new NodeId(Guid.NewGuid());

        var claim = RequestClaim.Rehydrate(
            requestId, projectId, nodeId, "token-9", ClaimedAt, ClaimedAt + Lease, version: 7);

        Assert.Equal(requestId, claim.RequestId);
        Assert.Equal(projectId, claim.ProjectId);
        Assert.Equal(nodeId, claim.NodeId);
        Assert.Equal("token-9", claim.ClaimToken);
        Assert.Equal(7, claim.Version);
    }
}
