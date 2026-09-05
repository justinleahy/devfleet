using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Infrastructure.Nodes;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests.Nodes;

public class NodeRegistryTests : IDisposable
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

    [Fact]
    public async Task Register_persists_a_new_offline_node()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();

        var dto = await registry.RegisterAsync(
            new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{\"arch\":\"arm64\"}"), _clock.GetUtcNow());

        Assert.Equal(nodeId.Value, dto.Id);
        Assert.Equal("pi-01", dto.DisplayName);
        Assert.Equal("1.2.3", dto.AgentVersion);
        Assert.Equal(NodeStatus.Offline, dto.Status);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Re_registering_an_existing_node_refreshes_it_instead_of_forking_a_row()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));

        var dto = await registry.RegisterAsync(
            new RegisterNodeCommand(nodeId, "pi-01-renamed", "2.0.0", "{\"gpu\":true}"), _clock.GetUtcNow());

        Assert.Equal("pi-01-renamed", dto.DisplayName);
        Assert.Equal("2.0.0", dto.AgentVersion);
        Assert.Equal("{\"gpu\":true}", dto.CapabilitiesJson);
        Assert.Equal(NodeStatus.Online, dto.Status);
        Assert.Equal(2, dto.Version);

        await using var verify = CreateContext();
        Assert.Single(verify.FleetNodes);
    }

    [Fact]
    public async Task Register_retry_after_concurrency_loss_keeps_the_requested_metadata()
    {
        await using var db = CreateContext();
        var nodeId = TestNodes.NewNodeId();
        await SeedRegistered(db, nodeId);
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var conflicted = false;
        db.SavingChanges += (_, _) =>
        {
            if (conflicted)
            {
                return;
            }

            conflicted = true;
            using var rival = CreateContext();
            rival.Database.ExecuteSql(
                $"UPDATE FleetNodes SET Version = Version + 1 WHERE Id = {nodeId.Value}");
        };
        var dto = await registry.RegisterAsync(
            new RegisterNodeCommand(nodeId, "pi-01-renamed", "2.0.0", "{\"gpu\":true}"), _clock.GetUtcNow());

        Assert.True(conflicted);
        Assert.Equal("pi-01-renamed", dto.DisplayName);
        Assert.Equal("2.0.0", dto.AgentVersion);
        Assert.Equal("{\"gpu\":true}", dto.CapabilitiesJson);
        Assert.Equal(3, dto.Version);
    }

    [Fact]
    public async Task Heartbeat_of_an_unregistered_node_throws()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());

        await Assert.ThrowsAsync<NodeNotFoundException>(() => registry.HeartbeatAsync(
            new NodeHeartbeatCommand(TestNodes.NewNodeId(), ActiveSessionIds: []), _clock.GetUtcNow()));
    }

    [Fact]
    public async Task Heartbeat_takes_the_node_online_and_advances_the_last_seen_time()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        var seenAt = _clock.GetUtcNow().AddMinutes(1);

        var dto = await registry.HeartbeatAsync(new NodeHeartbeatCommand(nodeId, ["session-a"]), seenAt);

        Assert.Equal(NodeStatus.Online, dto.Status);
        Assert.Equal(seenAt, dto.LastHeartbeatAt);
        Assert.Equal(2, dto.Version);
    }

    [Fact]
    public async Task MarkStaleOffline_takes_an_online_node_offline()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        await registry.HeartbeatAsync(new NodeHeartbeatCommand(nodeId, []), _clock.GetUtcNow());

        await registry.MarkStaleOfflineAsync(nodeId, _clock.GetUtcNow().AddMinutes(5));

        var dto = await registry.GetAsync(nodeId);
        Assert.NotNull(dto);
        Assert.Equal(NodeStatus.Offline, dto.Status);
    }

    [Fact]
    public async Task MarkStaleOffline_is_a_no_op_for_unknown_and_already_offline_nodes()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();

        await registry.MarkStaleOfflineAsync(nodeId, _clock.GetUtcNow());
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        await registry.MarkStaleOfflineAsync(nodeId, _clock.GetUtcNow());

        var dto = await registry.GetAsync(nodeId);
        Assert.NotNull(dto);
        Assert.Equal(NodeStatus.Offline, dto.Status);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Get_returns_null_for_an_unknown_node_and_List_orders_by_display_name()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());

        Assert.Null(await registry.GetAsync(TestNodes.NewNodeId()));

        await registry.RegisterAsync(new RegisterNodeCommand(TestNodes.NewNodeId(), "zeta", "1.0.0", "{}"), _clock.GetUtcNow());
        await registry.RegisterAsync(new RegisterNodeCommand(TestNodes.NewNodeId(), "alpha", "1.0.0", "{}"), _clock.GetUtcNow());

        var nodes = await registry.ListAsync();
        Assert.Equal(["alpha", "zeta"], nodes.Select(n => n.DisplayName).ToList());
    }
    private async Task SeedRegistered(ControlPlaneDbContext db, NodeId nodeId)
    {
        db.FleetNodes.Add(FleetNode.Register(nodeId, "pi-01", "1.2.3", "{}", _clock.GetUtcNow()));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
