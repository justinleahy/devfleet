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

    [Fact]
    public void Recovery_budgets_default_to_positive_attempt_covering_stop_phases()
    {
        var options = new NodeOptions();

        Assert.Equal(10, options.RecoveryCooperativeStopSeconds);
        Assert.Equal(20, options.RecoveryTerminationSeconds);
        Assert.Equal(60, options.RecoveryAttemptSeconds);

        var result = Validate("http://127.0.0.1:5057");
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Theory]
    [InlineData(0, 20, 60, "RecoveryCooperativeStopSeconds")]
    [InlineData(10, 0, 60, "RecoveryTerminationSeconds")]
    [InlineData(10, 20, 0, "RecoveryAttemptSeconds")]
    [InlineData(-1, 20, 60, "RecoveryCooperativeStopSeconds")]
    [InlineData(10, 20, 29, "RecoveryAttemptSeconds")]
    public void Invalid_recovery_budgets_fail_startup_validation(
        int cooperative,
        int termination,
        int attempt,
        string expectedFailure)
    {
        var options = new NodeOptions
        {
            ControlPlaneUrl = "http://127.0.0.1:5057",
            Id = Guid.NewGuid(),
            DisplayName = "test-node",
            AgentVersion = "test-version",
            CapabilitiesJson = "{}",
            HeartbeatSeconds = 10,
            MaxConcurrentRequests = 1,
            ClaimLeaseSeconds = 60,
            EventSpoolPath = "node-spool.db",
            RecoveryCooperativeStopSeconds = cooperative,
            RecoveryTerminationSeconds = termination,
            RecoveryAttemptSeconds = attempt,
        };

        var result = new NodeOptionsValidator().Validate(NodeOptions.SectionName, options);

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
