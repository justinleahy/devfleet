using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class WorkspaceDirectoryGatewayTests : IClassFixture<ControlPlaneFixture>, IAsyncDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;

    public WorkspaceDirectoryGatewayTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = fixture.CreateNodeHubConnection();
    }

    [Fact]
    public async Task Gateway_invokes_browse_handler_on_registered_node_and_returns_response()
    {
        var nodeId = _fixture.AuthenticatedNodeId;
        var response = new WorkspaceDirectoryBrowseResponseMessage(
            "/srv/repos",
            null,
            [new WorkspaceDirectoryEntryMessage("alpha", "/srv/repos/alpha")],
            null,
            null);
        WorkspaceDirectoryBrowseRequestMessage? receivedRequest = null;
        _connection.On<WorkspaceDirectoryBrowseRequestMessage, WorkspaceDirectoryBrowseResponseMessage>(
            WorkspaceDirectoryBrowseCallback.MethodName,
            request =>
            {
                receivedRequest = request;
                return Task.FromResult(response);
            });
        await _connection.StartAsync();
        _ = await _connection.InvokeAsync<object>(
            "Register", new NodeRegistrationMessage(nodeId, "browse-node", "1.0", "{}"));

        var gateway = _fixture.Factory.Services.GetRequiredService<INodeWorkspaceDirectoryGateway>();
        var request = new WorkspaceDirectoryBrowseRequestMessage("/srv/repos");
        var loaded = await gateway.BrowseAsync(nodeId, request);

        Assert.NotNull(receivedRequest);
        Assert.Equal("/srv/repos", receivedRequest.Path);
        Assert.Equal("/srv/repos", loaded.CurrentPath);
        Assert.Null(loaded.ParentPath);
        var entry = Assert.Single(loaded.Directories);
        Assert.Equal("alpha", entry.Name);
        Assert.Equal("/srv/repos/alpha", entry.Path);
        Assert.Null(loaded.ErrorCode);
        Assert.Null(loaded.ErrorDetail);
    }

    [Fact]
    public async Task Gateway_throws_deterministic_error_for_offline_node()
    {
        var nodeId = Guid.NewGuid();
        var gateway = _fixture.Factory.Services.GetRequiredService<INodeWorkspaceDirectoryGateway>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.BrowseAsync(nodeId, new WorkspaceDirectoryBrowseRequestMessage(null)));

        Assert.Equal($"Node '{nodeId}' is not connected.", ex.Message);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
