namespace PiCommandCenter.Node.Tests;

public sealed class NodeOptionsValidatorTests
{
    [Theory]
    [InlineData("http://127.0.0.1:5057")]
    [InlineData("http://[::1]:5057")]
    [InlineData("http://localhost:5057")]
    public void Loopback_http_control_plane_urls_are_valid(string controlPlaneUrl)
    {
        var result = Validate(controlPlaneUrl);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Theory]
    [InlineData("https://control.example.com")]
    [InlineData("https://192.0.2.1:5057")]
    public void Https_control_plane_urls_are_valid(string controlPlaneUrl)
    {
        var result = Validate(controlPlaneUrl);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Theory]
    [InlineData("http://control.example.com", "HTTPS")]
    [InlineData("https://", "absolute http(s) URL")]
    [InlineData("https://node:secret@control.example.com", "user information")]
    [InlineData("ftp://localhost/control", "absolute http(s) URL")]
    public void Invalid_control_plane_urls_fail_startup_validation(string controlPlaneUrl, string expectedFailure)
    {
        var result = Validate(controlPlaneUrl);

        Assert.False(result.Succeeded);
        Assert.Contains(
            result.Failures ?? [],
            failure => failure.Contains(expectedFailure, StringComparison.Ordinal));
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(string controlPlaneUrl)
    {
        var options = new NodeOptions
        {
            ControlPlaneUrl = controlPlaneUrl,
            Id = Guid.NewGuid(),
            DisplayName = "test-node",
            AgentVersion = "test-version",
            CapabilitiesJson = "{}",
            HeartbeatSeconds = 10,
            MaxConcurrentRequests = 1,
            ClaimLeaseSeconds = 60,
            EventSpoolPath = "node-spool.db",
        };

        return new NodeOptionsValidator().Validate(NodeOptions.SectionName, options);
    }
}
