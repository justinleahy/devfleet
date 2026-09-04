using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

public class HealthEndpointTests : IClassFixture<ControlPlaneFixture>
{
    private readonly ControlPlaneFixture _fixture;

    public HealthEndpointTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Health_endpoint_reports_healthy()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/health");
        var payload = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}: {payload}");
        Assert.Equal("Healthy", payload.Trim());
    }

    [Fact]
    public async Task Home_page_serves_command_center_status_content()
    {
        var client = _fixture.CreateClient();

        var response = await client.GetAsync("/");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(response.IsSuccessStatusCode, $"status {(int)response.StatusCode}: {html}");
        Assert.Contains("Pi Command Center", html);
        Assert.Contains("Fleet dashboard", html);
        Assert.Contains("Active projects", html);
        Assert.Contains("Queued requests", html);
        Assert.Contains("Projects", html);
    }
}
