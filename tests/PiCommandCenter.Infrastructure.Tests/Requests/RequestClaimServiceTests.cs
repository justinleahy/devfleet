using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Infrastructure.Nodes;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests.Requests;

public class RequestClaimServiceTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<(RequestClaimService Service, NodeId NodeId)> CreateWorldAsync(
        bool enabled = true,
        int maxReadOnlyRequests = 4)
    {
        var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        TestNodes.SeedProject(db, nodeId, _clock, enabled: enabled, maxReadOnlyRequests: maxReadOnlyRequests);
        await TestNodes.SaveAsync(db);
        return (new RequestClaimService(_clock, db), nodeId);
    }

    [Fact]
    public async Task Claim_selects_priority_descending_then_created_ascending()
    {
        var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, nodeId, _clock);
        var oldestNormal = TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Analysis, RequestPriority.Normal, "normal-old");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var newerNormal = TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Analysis, RequestPriority.Normal, "normal-new");
        _clock.Advance(TimeSpan.FromSeconds(1));
        var urgent = TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Analysis, RequestPriority.Urgent, "urgent");
        await TestNodes.SaveAsync(db);
        var service = new RequestClaimService(_clock, db);

        var first = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));
        var second = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));
        var third = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Equal(urgent.Id.Value, first.RequestId);
        Assert.Equal(project.Id.Value, first.ProjectId);
        Assert.NotEmpty(first.ClaimToken);
        Assert.Equal(first.ClaimedAt + TimeSpan.FromMinutes(5), first.LeaseExpiresAt);
        Assert.NotNull(second);
        Assert.Equal(oldestNormal.Id.Value, second.RequestId);
        Assert.NotNull(third);
        Assert.Equal(newerNormal.Id.Value, third.RequestId);
    }

    [Fact]
    public async Task Claim_returns_null_when_nothing_is_eligible()
    {
        var (service, nodeId) = await CreateWorldAsync();

        Assert.Null(await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Claim_ignores_projects_assigned_to_other_nodes_or_disabled()
    {
        var db = CreateContext();
        var mine = TestNodes.NewNodeId();
        var other = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, mine, _clock);
        TestNodes.SeedNode(db, other, _clock);
        var theirs = TestNodes.SeedProject(db, other, _clock);
        var disabled = TestNodes.SeedProject(db, mine, _clock, enabled: false);
        TestNodes.SeedRequest(db, theirs, _clock, WorkRequestKind.Analysis);
        TestNodes.SeedRequest(db, disabled, _clock, WorkRequestKind.Analysis);
        await TestNodes.SaveAsync(db);
        var service = new RequestClaimService(_clock, db);

        Assert.Null(await service.ClaimNextAsync(mine, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Claim_of_an_unregistered_node_throws()
    {
        var db = CreateContext();
        var service = new RequestClaimService(_clock, db);

        await Assert.ThrowsAsync<NodeNotFoundException>(
            () => service.ClaimNextAsync(TestNodes.NewNodeId(), TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Only_one_Development_request_may_be_active_per_project()
    {
        var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, nodeId, _clock, maxActiveWriteRequests: 4);
        TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Development, title: "dev-1");
        TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Development, title: "dev-2");
        await TestNodes.SaveAsync(db);
        var service = new RequestClaimService(_clock, db);

        var first = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));
        var second = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Equal(WorkRequestKind.Development.ToString(), KindOf(db, first.RequestId));
        Assert.Null(second);
    }

    [Fact]
    public async Task Read_only_claims_respect_the_project_limit()
    {
        var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, nodeId, _clock, maxReadOnlyRequests: 1);
        TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Analysis, title: "read-1");
        TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Review, title: "read-2");
        await TestNodes.SaveAsync(db);
        var service = new RequestClaimService(_clock, db);

        var first = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));
        var second = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task An_expired_read_only_claim_frees_its_capacity_slot()
    {
        var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, nodeId, _clock, maxReadOnlyRequests: 1);
        TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Analysis, title: "held");
        await TestNodes.SaveAsync(db);
        var service = new RequestClaimService(_clock, db);
        _ = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(1));

        _clock.Advance(TimeSpan.FromMinutes(2));
        var blocked = TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Analysis, title: "waiting");
        await TestNodes.SaveAsync(db);

        var next = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));
        Assert.NotNull(next);
        Assert.Equal(blocked.Id.Value, next.RequestId);
    }

    [Fact]
    public async Task Renew_extends_the_active_claim_lease()
    {
        var (service, nodeId) = await CreateWorldAsync();
        var db = CreateContext();
        var project = db.Projects.Single();
        TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);
        var claim = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5))
            ?? throw new InvalidOperationException("expected a claim");
        _clock.Advance(TimeSpan.FromMinutes(1));

        var newExpiry = await service.RenewAsync(
            new WorkRequestId(claim.RequestId), nodeId, claim.ClaimToken, TimeSpan.FromMinutes(5));

        Assert.Equal(claim.ClaimedAt.AddMinutes(1) + TimeSpan.FromMinutes(5), newExpiry);
    }

    [Fact]
    public async Task Renew_rejects_a_wrong_token_or_node()
    {
        var (service, nodeId) = await CreateWorldAsync();
        var db = CreateContext();
        var project = db.Projects.Single();
        TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);
        var claim = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5))
            ?? throw new InvalidOperationException("expected a claim");
        var requestId = new WorkRequestId(claim.RequestId);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenewAsync(requestId, nodeId, "wrong-token", TimeSpan.FromMinutes(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenewAsync(requestId, TestNodes.NewNodeId(), claim.ClaimToken, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Renew_rejects_an_expired_claim()
    {
        var (service, nodeId) = await CreateWorldAsync();
        var db = CreateContext();
        var project = db.Projects.Single();
        TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);
        var claim = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(1))
            ?? throw new InvalidOperationException("expected a claim");
        _clock.Advance(TimeSpan.FromMinutes(2));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RenewAsync(new WorkRequestId(claim.RequestId), nodeId, claim.ClaimToken, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Claim_rejects_a_non_positive_lease()
    {
        var (service, nodeId) = await CreateWorldAsync();
        var db = CreateContext();
        var project = db.Projects.Single();
        TestNodes.SeedRequest(db, project, _clock);
        await TestNodes.SaveAsync(db);

        await Assert.ThrowsAsync<ArgumentException>(() => service.ClaimNextAsync(nodeId, TimeSpan.Zero));
    }

    [Fact]
    public async Task Claim_carries_the_assignment_fields_needed_to_start_root_work()
    {
        var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, nodeId, _clock);
        TestNodes.SeedRequest(db, project, _clock, WorkRequestKind.Review, title: "Review the diff");
        await TestNodes.SaveAsync(db);
        var service = new RequestClaimService(_clock, db);

        var claim = await service.ClaimNextAsync(nodeId, TimeSpan.FromMinutes(5));

        Assert.NotNull(claim);
        Assert.Equal(project.RepositoryPath, claim.RepositoryPath);
        Assert.Equal(project.DefaultBranch, claim.DefaultBranch);
        Assert.Equal("Review the diff", claim.Title);
        Assert.Equal("Do the thing", claim.Prompt);
        Assert.Equal(WorkRequestKind.Review.ToString(), claim.Kind);
        Assert.Equal(RiskLevel.Standard.ToString(), claim.RiskLevel);
    }

    private static string KindOf(ControlPlaneDbContext db, Guid requestId) =>
        db.WorkRequests.Single(r => r.Id == new WorkRequestId(requestId)).Kind.ToString();
}
