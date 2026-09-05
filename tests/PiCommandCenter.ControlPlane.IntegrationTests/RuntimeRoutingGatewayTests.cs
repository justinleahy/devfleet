using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class RuntimeRoutingGatewayTests : IClassFixture<ControlPlaneFixture>, IAsyncDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;

    public RuntimeRoutingGatewayTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = fixture.CreateNodeHubConnection();
    }

    [Fact]
    public async Task Gateway_invokes_configuration_model_and_update_handlers_on_registered_node()
    {
        var nodeId = Guid.NewGuid();
        var configuration = new NodeRuntimeConfigurationMessage(
            nodeId,
            ["reviewer"],
            ["local-pi"],
            [new RuntimeRoleRouteMessage("reviewer", [new RuntimeRouteCandidateMessage("local-pi", null)])]);
        var catalogs = new[]
        {
            new RuntimeModelCatalogMessage(
                "local-pi",
                [new RuntimeModelMessage("provider/model", "Model", "provider")],
                null),
        };
        UpdateNodeRuntimeConfigurationMessage? receivedUpdate = null;
        _connection.On<NodeRuntimeConfigurationMessage>(
            "GetRuntimeConfiguration", () => Task.FromResult(configuration));
        _connection.On<IReadOnlyList<RuntimeModelCatalogMessage>>(
            "DiscoverRuntimeModels", () => Task.FromResult<IReadOnlyList<RuntimeModelCatalogMessage>>(catalogs));
        _connection.On<UpdateNodeRuntimeConfigurationMessage, NodeRuntimeConfigurationMessage>(
            "UpdateRuntimeConfiguration",
            update =>
            {
                receivedUpdate = update;
                return Task.FromResult(configuration);
            });
        await _connection.StartAsync();
        _ = await _connection.InvokeAsync<object>(
            "Register", new NodeRegistrationMessage(nodeId, "routing-node", "1.0", "{}"));

        var gateway = _fixture.Factory.Services.GetRequiredService<INodeRuntimeConfigurationGateway>();
        var loaded = await gateway.GetAsync(nodeId);
        var discovered = await gateway.DiscoverModelsAsync(nodeId);
        var update = new UpdateNodeRuntimeConfigurationMessage(configuration.RoleRoutes);
        var saved = await gateway.UpdateAsync(nodeId, update);

        Assert.Equal(nodeId, loaded.NodeId);
        Assert.Equal(configuration.AllowedRoles, loaded.AllowedRoles);
        Assert.Equal("local-pi", loaded.RoleRoutes.Single().Candidates.Single().RuntimeProfile);
        Assert.Equal("provider/model", discovered.Single().Models.Single().Id);
        Assert.Equal(nodeId, saved.NodeId);
        Assert.NotNull(receivedUpdate);
        Assert.Equal("reviewer", receivedUpdate.RoleRoutes.Single().Role);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
