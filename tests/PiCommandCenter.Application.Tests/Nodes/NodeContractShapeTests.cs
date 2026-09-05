using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;

namespace PiCommandCenter.Application.Tests;

/// <summary>
/// Shape tests for the node application contracts shared by the hub, the node worker, and the UI.
/// </summary>
public class NodeContractShapeTests
{
    [Fact]
    public void Node_status_is_an_explicit_offline_online_pair()
    {
        Assert.Equal(0, (int)NodeStatus.Offline);
        Assert.Equal(1, (int)NodeStatus.Online);
    }

    [Fact]
    public void NodeDto_carries_the_projection_the_ui_renders()
    {
        var id = Guid.NewGuid();
        var seen = DateTimeOffset.UtcNow;

        var dto = new NodeDto(id, "pi-01", "1.2.3", seen, NodeStatus.Online, "{}", Version: 7);

        Assert.Equal(id, dto.Id);
        Assert.Equal("pi-01", dto.DisplayName);
        Assert.Equal("1.2.3", dto.AgentVersion);
        Assert.Equal(seen, dto.LastHeartbeatAt);
        Assert.Equal(NodeStatus.Online, dto.Status);
        Assert.Equal("{}", dto.CapabilitiesJson);
        Assert.Equal(7, dto.Version);
    }

    [Fact]
    public void RequestClaimDto_exposes_the_lease_a_node_needs_to_renew()
    {
        var claimedAt = DateTimeOffset.UtcNow;
        var dto = new RequestClaimDto(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "token", claimedAt, claimedAt.AddMinutes(1),
            "/repos/hub", "main", "Hub request", "Do hub work", "Development", "Standard", true, false);

        Assert.False(string.IsNullOrWhiteSpace(dto.ClaimToken));
        Assert.True(dto.LeaseExpiresAt > dto.ClaimedAt);
        Assert.Equal("/repos/hub", dto.RepositoryPath);
        Assert.Equal("main", dto.DefaultBranch);
        Assert.Equal("Hub request", dto.Title);
        Assert.Equal("Do hub work", dto.Prompt);
        Assert.Equal("Development", dto.Kind);
        Assert.Equal("Standard", dto.RiskLevel);
        Assert.True(dto.CreateRequestBranch);
        Assert.False(dto.CreateRequestCommit);

    }

    [Fact]
    public void NodeNotFoundException_identifies_the_missing_node()
    {
        var id = new NodeId(Guid.NewGuid());

        var exception = new NodeNotFoundException(id);

        Assert.Equal(id, exception.Id);
        Assert.Contains(id.ToString(), exception.Message);
    }
}
