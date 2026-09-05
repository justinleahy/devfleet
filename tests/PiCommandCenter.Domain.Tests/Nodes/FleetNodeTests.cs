using System.Text.Json;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;

namespace PiCommandCenter.Domain.Tests.Nodes;

public class FleetNodeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static FleetNode RegisterNode(string displayName = "pi-01", string agentVersion = "1.2.3") =>
        FleetNode.Register(new NodeId(Guid.NewGuid()), displayName, agentVersion, "{}", Now);

    [Fact]
    public void Register_starts_offline_and_stamps_all_timestamps()
    {
        var id = new NodeId(Guid.NewGuid());

        var node = FleetNode.Register(id, "  pi-01  ", "1.2.3", "{\"arch\":\"arm64\"}", Now);

        Assert.Equal(id, node.Id);
        Assert.Equal("pi-01", node.DisplayName);
        Assert.Equal("1.2.3", node.AgentVersion);
        Assert.Equal(NodeStatus.Offline, node.Status);
        Assert.Equal("{\"arch\":\"arm64\"}", node.CapabilitiesJson);
        Assert.Equal(Now, node.CreatedAt);
        Assert.Equal(Now, node.UpdatedAt);
        Assert.Equal(Now, node.LastHeartbeatAt);
        Assert.Equal(1, node.Version);
    }

    [Fact]
    public void Heartbeat_bring_the_node_online_and_bump_the_version()
    {
        var node = RegisterNode();
        var later = Now.AddMinutes(5);

        node.Heartbeat("1.3.0", "{\"arch\":\"arm64\",\"gpu\":true}", later);

        Assert.Equal(NodeStatus.Online, node.Status);
        Assert.Equal("1.3.0", node.AgentVersion);
        Assert.Equal("{\"arch\":\"arm64\",\"gpu\":true}", node.CapabilitiesJson);
        Assert.Equal(later, node.LastHeartbeatAt);
        Assert.Equal(later, node.UpdatedAt);
        Assert.Equal(2, node.Version);
    }

    [Fact]
    public void MarkOffline_takes_an_online_node_down_without_touching_the_last_heartbeat()
    {
        var node = RegisterNode();
        node.Heartbeat("1.2.3", "{}", Now);
        var stale = Now.AddMinutes(10);

        node.MarkOffline(stale);

        Assert.Equal(NodeStatus.Offline, node.Status);
        Assert.Equal(Now, node.LastHeartbeatAt);
        Assert.Equal(stale, node.UpdatedAt);
        Assert.Equal(3, node.Version);
    }

    [Fact]
    public void RefreshRegistration_renames_the_node_brings_it_online_and_bumps_the_version()
    {
        var node = RegisterNode();
        var later = Now.AddMinutes(5);

        node.RefreshRegistration("pi-01-renamed", "2.0.0", "{\"gpu\":true}", later);

        Assert.Equal("pi-01-renamed", node.DisplayName);
        Assert.Equal("2.0.0", node.AgentVersion);
        Assert.Equal("{\"gpu\":true}", node.CapabilitiesJson);
        Assert.Equal(NodeStatus.Online, node.Status);
        Assert.Equal(later, node.LastHeartbeatAt);
        Assert.Equal(later, node.UpdatedAt);
        Assert.Equal(2, node.Version);
        Assert.Equal(Now, node.CreatedAt);
    }

    [Fact]
    public void RefreshRegistration_rejects_invalid_metadata_without_losing_state()
    {
        var node = RegisterNode();

        Assert.Throws<ArgumentException>(() =>
            node.RefreshRegistration("  ", "2.0.0", "{}", Now.AddMinutes(1)));
        Assert.Throws<ArgumentException>(() =>
            node.RefreshRegistration("pi-01-renamed", "2.0.0", "not-json", Now.AddMinutes(1)));

        Assert.Equal("pi-01", node.DisplayName);
        Assert.Equal("1.2.3", node.AgentVersion);
        Assert.Equal(NodeStatus.Offline, node.Status);
        Assert.Equal(1, node.Version);
    }

    [Fact]
    public void MarkOffline_is_idempotent_for_an_already_offline_node()
    {
        var node = RegisterNode();

        node.MarkOffline(Now.AddMinutes(10));

        Assert.Equal(NodeStatus.Offline, node.Status);
        Assert.Equal(Now, node.UpdatedAt);
        Assert.Equal(1, node.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_a_blank_display_name(string? displayName)
    {
        Assert.Throws<ArgumentException>(() =>
            FleetNode.Register(new NodeId(Guid.NewGuid()), displayName!, "1.2.3", "{}", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_a_blank_agent_version(string? agentVersion)
    {
        Assert.Throws<ArgumentException>(() =>
            FleetNode.Register(new NodeId(Guid.NewGuid()), "pi-01", agentVersion!, "{}", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not-json")]
    public void Register_rejects_malformed_capabilities(string? capabilitiesJson)
    {
        Assert.Throws<ArgumentException>(() =>
            FleetNode.Register(new NodeId(Guid.NewGuid()), "pi-01", "1.2.3", capabilitiesJson!, Now));
    }

    [Fact]
    public void Heartbeat_rejects_malformed_capabilities_without_losing_state()
    {
        var node = RegisterNode();

        Assert.Throws<ArgumentException>(() => node.Heartbeat("1.3.0", "not-json", Now.AddMinutes(1)));

        Assert.Equal(NodeStatus.Offline, node.Status);
        Assert.Equal("1.2.3", node.AgentVersion);
        Assert.Equal(1, node.Version);
    }

    [Fact]
    public void Rehydrate_restores_the_persisted_state_without_mutating_version()
    {
        var id = new NodeId(Guid.NewGuid());
        var createdAt = Now.AddDays(-3);
        var updatedAt = Now.AddMinutes(-1);

        var node = FleetNode.Rehydrate(
            id, "pi-02", "2.0.0", NodeStatus.Online, Now.AddMinutes(-1), "{}",
            createdAt, updatedAt, version: 42);

        Assert.Equal(id, node.Id);
        Assert.Equal(NodeStatus.Online, node.Status);
        Assert.Equal(createdAt, node.CreatedAt);
        Assert.Equal(updatedAt, node.UpdatedAt);
        Assert.Equal(42, node.Version);
    }

    [Fact]
    public void Heartbeat_overwrites_the_latest_resource_snapshot()
    {
        var node = RegisterNode();
        var firstAt = Now.AddMinutes(1);
        var secondAt = Now.AddMinutes(2);

        node.Heartbeat("1.2.3", "{}", firstAt, SnapshotJson(firstAt, cpu: 10d));
        node.Heartbeat("1.2.3", "{}", secondAt, SnapshotJson(secondAt, cpu: 42.5));

        AssertSnapshot(node.ResourceSnapshotJson, secondAt, cpu: 42.5);
    }

    [Fact]
    public void Heartbeat_clears_the_resource_snapshot_when_resources_are_omitted()
    {
        var node = RegisterNode();
        node.Heartbeat("1.2.3", "{}", Now.AddMinutes(1), SnapshotJson(Now.AddMinutes(1)));

        node.Heartbeat("1.2.3", "{}", Now.AddMinutes(2), resourceSnapshotJson: null);

        Assert.Null(node.ResourceSnapshotJson);
    }

    [Fact]
    public void RefreshRegistration_preserves_the_latest_resource_snapshot()
    {
        var node = RegisterNode();
        var observedAt = Now.AddMinutes(1);
        node.Heartbeat("1.2.3", "{}", observedAt, SnapshotJson(observedAt));

        node.RefreshRegistration("pi-01-renamed", "2.0.0", "{}", Now.AddMinutes(2));

        AssertSnapshot(node.ResourceSnapshotJson, observedAt, cpu: 12.5);
        Assert.Equal("pi-01-renamed", node.DisplayName);
    }

    private static string SnapshotJson(DateTimeOffset observedAt, double cpu = 12.5) =>
        JsonSerializer.Serialize(new
        {
            observedAt,
            cpuUsagePercent = cpu,
            memoryUsedBytes = 1024L,
            memoryTotalBytes = 2048L,
            diskUsedBytes = 4096L,
            diskTotalBytes = 8192L,
            loadAverageOneMinute = 0.25,
            uptimeSeconds = 90d,
        });

    private static void AssertSnapshot(string? json, DateTimeOffset observedAt, double cpu)
    {
        Assert.False(string.IsNullOrWhiteSpace(json));
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(observedAt, root.GetProperty("observedAt").GetDateTimeOffset());
        Assert.Equal(cpu, root.GetProperty("cpuUsagePercent").GetDouble());
        Assert.Equal(1024L, root.GetProperty("memoryUsedBytes").GetInt64());
        Assert.Equal(2048L, root.GetProperty("memoryTotalBytes").GetInt64());
        Assert.Equal(4096L, root.GetProperty("diskUsedBytes").GetInt64());
        Assert.Equal(8192L, root.GetProperty("diskTotalBytes").GetInt64());
        Assert.Equal(0.25, root.GetProperty("loadAverageOneMinute").GetDouble());
        Assert.Equal(90d, root.GetProperty("uptimeSeconds").GetDouble());
    }
}
