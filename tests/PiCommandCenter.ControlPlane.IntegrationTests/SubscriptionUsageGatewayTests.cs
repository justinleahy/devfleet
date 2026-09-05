using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public sealed class SubscriptionUsageGatewayTests : IClassFixture<ControlPlaneFixture>, IAsyncDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;

    public SubscriptionUsageGatewayTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _connection = fixture.CreateNodeHubConnection();
    }

    [Fact]
    public async Task Gateway_invokes_subscription_usage_handler_on_registered_node()
    {
        var nodeId = Guid.NewGuid();
        var observedAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new NodeSubscriptionUsageMessage(
            nodeId,
            [
                new ProviderSubscriptionUsageMessage(
                    "claude",
                    ["claude-code"],
                    SubscriptionUsageStatuses.Available,
                    true,
                    "Pro",
                    "1.0",
                    [
                        new SubscriptionUsageWindowMessage(
                            "week",
                            42.5,
                            57.5,
                            observedAt.AddDays(2)),
                    ],
                    observedAt,
                    "cli",
                    null),
                new ProviderSubscriptionUsageMessage(
                    "codex",
                    ["codex"],
                    SubscriptionUsageStatuses.Unavailable,
                    null,
                    null,
                    null,
                    [],
                    observedAt,
                    "cli",
                    "quota surface unavailable"),
            ]);
        _connection.On<NodeSubscriptionUsageMessage>(
            "GetSubscriptionUsage",
            () => Task.FromResult(snapshot));
        await _connection.StartAsync();
        _ = await _connection.InvokeAsync<object>(
            "Register", new NodeRegistrationMessage(nodeId, "usage-node", "1.0", "{}"));

        var gateway = _fixture.Factory.Services.GetRequiredService<INodeSubscriptionUsageGateway>();
        var loaded = await gateway.GetAsync(nodeId);

        Assert.Equal(nodeId, loaded.NodeId);
        Assert.Equal(2, loaded.Providers.Count);
        var available = loaded.Providers[0];
        Assert.Equal("claude", available.Provider);
        Assert.Equal(SubscriptionUsageStatuses.Available, available.Status);
        Assert.Equal("week", available.Windows.Single().Name);
        Assert.Equal(42.5, available.Windows.Single().PercentUsed);
        var unavailable = loaded.Providers[1];
        Assert.Equal("codex", unavailable.Provider);
        Assert.Equal(SubscriptionUsageStatuses.Unavailable, unavailable.Status);
        Assert.Empty(unavailable.Windows);
        Assert.Equal("quota surface unavailable", unavailable.Diagnostic);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
