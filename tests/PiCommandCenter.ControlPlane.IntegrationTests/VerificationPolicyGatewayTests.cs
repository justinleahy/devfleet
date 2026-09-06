using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.VerificationPolicy;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class VerificationPolicyGatewayTests : IClassFixture<ControlPlaneFixture>, IAsyncDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;

    public VerificationPolicyGatewayTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = fixture.CreateNodeHubConnection();
    }

    [Fact]
    public async Task Gateway_invokes_catalog_and_validation_handlers_on_registered_node()
    {
        var nodeId = _fixture.AuthenticatedNodeId;
        var catalog = new VerificationPolicyCatalogMessage(
            DateTimeOffset.UtcNow,
            BaselineAvailable: true,
            BaselineVersion: VerificationBaselineIds.Version,
            [
                new VerificationPolicyProfileMessage(
                    "dotnet-ci",
                    "rev-3",
                    "Dotnet CI",
                    [
                        new VerificationPolicyCommandMessage(
                            "test",
                            "Test",
                            "repository",
                            Mandatory: true,
                            TimeoutSeconds: 60),
                    ]),
            ]);
        VerificationProfileSelectionRequestMessage? received = null;
        _connection.On<VerificationPolicyCatalogMessage>(
            "GetVerificationPolicyCatalog",
            () => Task.FromResult(catalog));
        _connection.On<VerificationProfileSelectionRequestMessage, VerificationProfileSelectionResultMessage>(
            "ValidateVerificationProfileSelection",
            request =>
            {
                received = request;
                return Task.FromResult(new VerificationProfileSelectionResultMessage(
                    Accepted: true,
                    VerificationPolicySelectionCodes.Accepted,
                    Detail: string.Empty,
                    request.ProfileId,
                    request.ProfileRevision));
            });
        await _connection.StartAsync();
        _ = await _connection.InvokeAsync<object>(
            "Register", new NodeRegistrationMessage(nodeId, "policy-node", "1.0", "{}"));

        var gateway = _fixture.Factory.Services.GetRequiredService<INodeVerificationPolicyGateway>();
        var loaded = await gateway.GetCatalogAsync(nodeId);
        var validation = await gateway.ValidateSelectionAsync(
            nodeId,
            new VerificationProfileSelectionRequestMessage(
                Guid.NewGuid(),
                Guid.NewGuid(),
                WorkspaceBindingRevision: 4,
                "dotnet-ci",
                "rev-3"));

        Assert.True(loaded.BaselineAvailable);
        Assert.Equal(VerificationBaselineIds.Version, loaded.BaselineVersion);
        Assert.Equal("dotnet-ci", loaded.Profiles.Single().Id);
        Assert.Equal("Test", loaded.Profiles.Single().Commands.Single().DisplayLabel);
        Assert.True(validation.Accepted);
        Assert.NotNull(received);
        Assert.Equal("dotnet-ci", received.ProfileId);
        Assert.Equal(4, received.WorkspaceBindingRevision);
    }

    [Fact]
    public async Task Gateway_reports_when_the_node_is_not_connected()
    {
        var gateway = _fixture.Factory.Services.GetRequiredService<INodeVerificationPolicyGateway>();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.GetCatalogAsync(Guid.NewGuid()));
        Assert.Contains("is not connected", error.Message, StringComparison.Ordinal);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
