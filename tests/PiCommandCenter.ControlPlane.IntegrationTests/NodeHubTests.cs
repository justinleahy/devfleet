using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Exercises the /nodeHub SignalR contract through a real hub connection (long polling over the
/// test server's message handler): registration, heartbeats, atomic claims, and idempotent events.
/// </summary>
public sealed class NodeHubTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;
    private readonly Guid _nodeId = Guid.NewGuid();

    public NodeHubTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = fixture.CreateNodeHubConnection();
        _connection.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();


    [Fact]
    public async Task NodeHub_is_not_a_browser_navigable_page()
    {
        var response = await _fixture.CreateAnonymousClient().GetAsync("/nodeHub");
        var body = await response.Content.ReadAsStringAsync();

        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal(401, (int)response.StatusCode);
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Register_creates_an_offline_node_and_heartbeat_bring_it_online()
    {
        var registered = await _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-01", "1.0.0", "{}"));

        Assert.Equal(_nodeId, registered.Id);
        Assert.Equal("pi-hub-01", registered.DisplayName);
        Assert.Equal(NodeStatus.Offline, registered.Status);
        Assert.Equal(1, registered.Version);

        var heartbeaten = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, ["session-a"]));

        Assert.Equal(NodeStatus.Online, heartbeaten.Status);
        Assert.Equal(2, heartbeaten.Version);

        var persisted = await GetNodeAsync(registered.Id);
        Assert.NotNull(persisted);
        Assert.Equal(NodeStatus.Online, persisted.Status);
        Assert.Equal(2, persisted.Version);
    }

    [Fact]
    public async Task Heartbeat_maps_every_resource_snapshot_field_onto_the_node_projection()
    {
        await _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-resources", "1.0.0", "{}"));
        var observedAt = new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero);
        var resources = new NodeResourceSnapshotMessage(
            observedAt,
            CpuUsagePercent: 12.5,
            MemoryUsedBytes: 1024L * 1024L,
            MemoryTotalBytes: 2L * 1024L * 1024L,
            DiskUsedBytes: 3L * 1024L * 1024L,
            DiskTotalBytes: 4L * 1024L * 1024L,
            LoadAverageOneMinute: 0.5,
            UptimeSeconds: 3661d);

        var heartbeaten = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, ["session-a"], resources));

        Assert.NotNull(heartbeaten.Resources);
        Assert.Equal(observedAt, heartbeaten.Resources.ObservedAt);
        Assert.Equal(12.5, heartbeaten.Resources.CpuUsagePercent);
        Assert.Equal(1024L * 1024L, heartbeaten.Resources.MemoryUsedBytes);
        Assert.Equal(2L * 1024L * 1024L, heartbeaten.Resources.MemoryTotalBytes);
        Assert.Equal(3L * 1024L * 1024L, heartbeaten.Resources.DiskUsedBytes);
        Assert.Equal(4L * 1024L * 1024L, heartbeaten.Resources.DiskTotalBytes);
        Assert.Equal(0.5, heartbeaten.Resources.LoadAverageOneMinute);
        Assert.Equal(3661d, heartbeaten.Resources.UptimeSeconds);

        var cleared = await _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(_nodeId, ["session-a"], Resources: null));
        Assert.Null(cleared.Resources);
    }

    [Fact]
    public async Task Heartbeat_of_an_unregistered_node_fails_the_call()
    {
        await Assert.ThrowsAnyAsync<HubException>(() => _connection.InvokeAsync<NodeDto>(
            "Heartbeat", new NodeHeartbeatMessage(Guid.NewGuid(), [])));
    }

    [Fact]
    public async Task ClaimNext_returns_the_queued_request_then_nothing_while_the_development_cap_holds()
    {
        await RegisterNodeAsync();
        var requestId = await SeedRequestAsync(priority: RequestPriority.High, kind: WorkRequestKind.Development);
        await SeedRequestAsync(priority: RequestPriority.Normal, kind: WorkRequestKind.Development);

        var claim = await _connection.InvokeAsync<RequestClaimMessage?>(
            "ClaimNext", new ClaimRequestMessage(_nodeId, LeaseSeconds: 60));

        Assert.NotNull(claim);
        Assert.Equal(requestId, claim.RequestId);
        Assert.Equal(_nodeId, claim.NodeId);
        Assert.False(string.IsNullOrWhiteSpace(claim.ClaimToken));
        Assert.True(claim.LeaseExpiresAt > claim.ClaimedAt);
        Assert.False(string.IsNullOrWhiteSpace(claim.RepositoryPath));
        Assert.Equal("main", claim.DefaultBranch);
        Assert.Equal("Hub request", claim.Title);
        Assert.Equal("Do hub work", claim.Prompt);
        Assert.Equal(WorkRequestKind.Development.ToString(), claim.Kind);
        Assert.Equal(RiskLevel.Standard.ToString(), claim.RiskLevel);
        Assert.True(claim.CreateRequestBranch);
        Assert.True(claim.CreateRequestCommit);

        var second = await _connection.InvokeAsync<RequestClaimMessage?>(
            "ClaimNext", new ClaimRequestMessage(_nodeId, LeaseSeconds: 60));

        Assert.Null(second);
    }

    [Fact]
    public async Task RenewClaim_extends_the_lease_and_returns_null_for_a_wrong_token()
    {
        await RegisterNodeAsync();
        _ = await SeedRequestAsync(kind: WorkRequestKind.Analysis);
        var claim = await _connection.InvokeAsync<RequestClaimMessage?>(
            "ClaimNext", new ClaimRequestMessage(_nodeId, LeaseSeconds: 60));

        Assert.NotNull(claim);
        var renewedExpiry = await _connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim", new ClaimRenewalMessage(claim.RequestId, _nodeId, claim.ClaimToken, LeaseSeconds: 60));

        Assert.True(renewedExpiry >= claim.LeaseExpiresAt);

        var rejected = await _connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim", new ClaimRenewalMessage(claim.RequestId, _nodeId, "wrong-token", LeaseSeconds: 60));
        Assert.Null(rejected);
    }

    [Fact]
    public async Task PublishEvents_persists_the_batch_and_replaying_it_creates_no_duplicates()
    {
        await RegisterNodeAsync();
        var batch = new NodeEventBatchMessage(
        [
            new NodeEventMessage("evt-hub-1", _nodeId, Guid.NewGuid(), null, "s-1", 1, "session.log", DateTimeOffset.UtcNow, "{}"),
            new NodeEventMessage("evt-hub-2", _nodeId, Guid.NewGuid(), null, "s-1", 2, "session.log", DateTimeOffset.UtcNow, "{}"),
        ]);

        var ack = await _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", batch);
        Assert.Equal(batch.Events.Select(e => e.EventId), ack.EventIds);

        var replayAck = await _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", batch);
        Assert.Equal(ack.EventIds, replayAck.EventIds);

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        Assert.Equal(2, db.SessionEvents.Count(e => e.EventId.StartsWith("evt-hub-")));
    }

    [Fact]
    public async Task PublishEvents_rejects_batches_over_the_transport_limit()
    {
        await RegisterNodeAsync();
        var oversized = new NodeEventBatchMessage(Enumerable.Range(0, 501)
            .Select(i => new NodeEventMessage(
                "evt-oversize-" + i, _nodeId, Guid.NewGuid(), null, null, i, "t", DateTimeOffset.UtcNow, "{}"))
            .ToList());

        await Assert.ThrowsAnyAsync<HubException>(() =>
            _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", oversized));
    }

    private async Task RegisterNodeAsync() => _ = await _connection.InvokeAsync<NodeDto>(
        "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-claim", "1.0.0", "{}"));

    private async Task<NodeDto?> GetNodeAsync(Guid id)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<INodeRegistry>();
        return await registry.GetAsync(new NodeId(id));
    }

    private async Task<Guid> SeedRequestAsync(
        RequestPriority priority = RequestPriority.Normal,
        WorkRequestKind kind = WorkRequestKind.Development)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(_nodeId);
        var project = await db.Projects.SingleOrDefaultAsync(p => p.NodeId == nodeId);
        if (project is null)
        {
            project = Project.Register(
                nodeId, "Hub project " + Guid.NewGuid().ToString("N")[..6],
                Path.Combine(Path.GetTempPath(), "pi-cc-integration", Guid.NewGuid().ToString("N")),
                "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
                maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: true,
                createRequestCommit: true, autoMerge: false, now);
            db.Projects.Add(project);
        }
        var request = WorkRequest.Enqueue(project.Id, kind, priority, RiskLevel.Standard,
            "Hub request", "Do hub work", now);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id.Value;
    }
}
