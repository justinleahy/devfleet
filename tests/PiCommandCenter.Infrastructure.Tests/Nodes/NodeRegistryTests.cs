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

    [Fact]
    public async Task Heartbeat_persists_the_latest_resource_snapshot_and_reloads_it()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        var first = SampleResources(_clock.GetUtcNow(), cpu: 8d);
        var second = SampleResources(_clock.GetUtcNow().AddMinutes(1), cpu: 33.5);

        await registry.HeartbeatAsync(new NodeHeartbeatCommand(nodeId, [], first), _clock.GetUtcNow());
        await registry.HeartbeatAsync(
            new NodeHeartbeatCommand(nodeId, [], second),
            _clock.GetUtcNow().AddMinutes(1));

        db.ChangeTracker.Clear();
        await using var reload = CreateContext();
        var reloaded = new NodeRegistry(_clock, reload, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var dto = await reloaded.GetAsync(nodeId);

        Assert.NotNull(dto);
        AssertEqualResources(second, dto.Resources);
    }

    [Theory]
    [InlineData(1024L, null, 4096L, null)]
    [InlineData(null, 2048L, null, 8192L)]
    public async Task Heartbeat_persists_and_projects_partial_byte_observations(
        long? memoryUsedBytes,
        long? memoryTotalBytes,
        long? diskUsedBytes,
        long? diskTotalBytes)
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        var partial = SampleResources(_clock.GetUtcNow()) with
        {
            MemoryUsedBytes = memoryUsedBytes,
            MemoryTotalBytes = memoryTotalBytes,
            DiskUsedBytes = diskUsedBytes,
            DiskTotalBytes = diskTotalBytes,
        };

        var projected = await registry.HeartbeatAsync(
            new NodeHeartbeatCommand(nodeId, [], partial),
            _clock.GetUtcNow());
        AssertEqualResources(partial, projected.Resources);

        db.ChangeTracker.Clear();
        await using var reload = CreateContext();
        var reloaded = new NodeRegistry(_clock, reload, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var persisted = await reloaded.GetAsync(nodeId);

        Assert.NotNull(persisted);
        AssertEqualResources(partial, persisted.Resources);
    }

    [Fact]
    public async Task Heartbeat_clears_a_persisted_resource_snapshot_when_resources_are_omitted()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        await registry.HeartbeatAsync(
            new NodeHeartbeatCommand(nodeId, [], SampleResources(_clock.GetUtcNow())),
            _clock.GetUtcNow());

        await registry.HeartbeatAsync(new NodeHeartbeatCommand(nodeId, [], Resources: null), _clock.GetUtcNow().AddMinutes(1));

        db.ChangeTracker.Clear();
        var dto = await registry.GetAsync(nodeId);
        Assert.NotNull(dto);
        Assert.Null(dto.Resources);
    }

    [Fact]
    public async Task Heartbeat_rejects_an_invalid_resource_snapshot_without_persisting_it()
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        var invalid = SampleResources(_clock.GetUtcNow()) with { CpuUsagePercent = 150d };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.HeartbeatAsync(new NodeHeartbeatCommand(nodeId, [], invalid), _clock.GetUtcNow()));

        db.ChangeTracker.Clear();
        var dto = await registry.GetAsync(nodeId);
        Assert.NotNull(dto);
        Assert.Null(dto.Resources);
        Assert.Equal(NodeStatus.Offline, dto.Status);
        Assert.Equal(1, dto.Version);
    }

    [Theory]
    [InlineData(-1L, null, 4096L, 8192L)]
    [InlineData(null, -1L, 4096L, 8192L)]
    [InlineData(null, 0L, 4096L, 8192L)]
    [InlineData(2049L, 2048L, 4096L, 8192L)]
    [InlineData(1024L, 2048L, -1L, null)]
    [InlineData(1024L, 2048L, null, -1L)]
    [InlineData(1024L, 2048L, null, 0L)]
    [InlineData(1024L, 2048L, 8193L, 8192L)]
    public async Task Heartbeat_rejects_invalid_byte_observations_before_mutating_the_node(
        long? memoryUsedBytes,
        long? memoryTotalBytes,
        long? diskUsedBytes,
        long? diskTotalBytes)
    {
        await using var db = CreateContext();
        var registry = new NodeRegistry(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var nodeId = TestNodes.NewNodeId();
        await registry.RegisterAsync(new RegisterNodeCommand(nodeId, "pi-01", "1.2.3", "{}"), _clock.GetUtcNow());
        var invalid = SampleResources(_clock.GetUtcNow()) with
        {
            MemoryUsedBytes = memoryUsedBytes,
            MemoryTotalBytes = memoryTotalBytes,
            DiskUsedBytes = diskUsedBytes,
            DiskTotalBytes = diskTotalBytes,
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            registry.HeartbeatAsync(new NodeHeartbeatCommand(nodeId, [], invalid), _clock.GetUtcNow()));

        db.ChangeTracker.Clear();
        var dto = await registry.GetAsync(nodeId);
        Assert.NotNull(dto);
        Assert.Null(dto.Resources);
        Assert.Equal(NodeStatus.Offline, dto.Status);
        Assert.Equal(1, dto.Version);
    }

    private static NodeResourceSnapshotDto SampleResources(DateTimeOffset observedAt, double cpu = 12.5) =>
        new(
            observedAt,
            cpu,
            MemoryUsedBytes: 1024L,
            MemoryTotalBytes: 2048L,
            DiskUsedBytes: 4096L,
            DiskTotalBytes: 8192L,
            LoadAverageOneMinute: 0.25,
            UptimeSeconds: 90d);

    private static void AssertEqualResources(NodeResourceSnapshotDto expected, NodeResourceSnapshotDto? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ObservedAt, actual.ObservedAt);
        Assert.Equal(expected.CpuUsagePercent, actual.CpuUsagePercent);
        Assert.Equal(expected.MemoryUsedBytes, actual.MemoryUsedBytes);
        Assert.Equal(expected.MemoryTotalBytes, actual.MemoryTotalBytes);
        Assert.Equal(expected.DiskUsedBytes, actual.DiskUsedBytes);
        Assert.Equal(expected.DiskTotalBytes, actual.DiskTotalBytes);
        Assert.Equal(expected.LoadAverageOneMinute, actual.LoadAverageOneMinute);
        Assert.Equal(expected.UptimeSeconds, actual.UptimeSeconds);
    }
    private async Task SeedRegistered(ControlPlaneDbContext db, NodeId nodeId)
    {
        db.FleetNodes.Add(FleetNode.Register(nodeId, "pi-01", "1.2.3", "{}", _clock.GetUtcNow()));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
