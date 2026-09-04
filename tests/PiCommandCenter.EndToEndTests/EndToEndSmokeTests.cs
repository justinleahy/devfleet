using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiCommandCenter.Contracts;
using PiCommandCenter.Node;

namespace PiCommandCenter.EndToEndTests;

public class EndToEndSmokeTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public EndToEndSmokeTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ControlPlane_serves_the_command_center_home_page_and_health_endpoint()
    {
        var client = _factory.CreateClient();

        var healthResponse = await client.GetAsync("/health");
        var healthPayload = await healthResponse.Content.ReadAsStringAsync();
        var homeResponse = await client.GetAsync("/");
        var html = await homeResponse.Content.ReadAsStringAsync();

        Assert.True(healthResponse.IsSuccessStatusCode, $"status {(int)healthResponse.StatusCode}: {healthPayload}");
        Assert.Equal("Healthy", healthPayload.Trim());
        Assert.True(homeResponse.IsSuccessStatusCode, html);
        Assert.Contains("Pi Command Center", html);
        Assert.Contains("Control plane is running", html);
        Assert.Contains($"Protocol version: {ProtocolVersion.Current}", html);
    }

    [Fact]
    public void Node_hosting_registers_a_hosted_worker_and_matches_the_protocol_contract()
    {
        using var app = new HostBuilder()
            .ConfigureServices(services => services.AddPiNode())
            .Build();

        Assert.Contains(app.Services.GetServices<IHostedService>(), s => s is NodeWorker);
        Assert.Equal(1, ProtocolVersion.Current);
    }
}
