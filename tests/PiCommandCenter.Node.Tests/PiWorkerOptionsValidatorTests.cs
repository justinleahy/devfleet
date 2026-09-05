namespace PiCommandCenter.Node.Tests;

public sealed class PiWorkerOptionsValidatorTests
{
    private static PiWorkerOptions ValidOptions() => new()
    {
        WorkerPath = typeof(PiWorkerOptionsValidatorTests).Assembly.Location,
        NodeExecutable = "node",
        AgentDataDirectory = "/tmp/devfleet-agent-data",
    };

    [Fact]
    public void Default_ordered_role_routes_are_valid()
    {
        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, ValidOptions());

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Every_allowed_role_requires_a_nonempty_route()
    {
        var options = ValidOptions();
        options.RoleRoutes.Remove("reviewer");

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("Pi:RoleRoutes:reviewer", StringComparison.Ordinal));
    }

    [Fact]
    public void Route_candidates_must_be_allowed_nonblank_and_unique()
    {
        var options = ValidOptions();
        options.AllowedChildRoles = ["reviewer"];
        options.RoleRoutes = new(StringComparer.Ordinal)
        {
            ["reviewer"] =
            [
                new() { RuntimeProfile = "claude-readonly", Model = " " },
                new() { RuntimeProfile = "claude-readonly", Model = " " },
                new() { RuntimeProfile = "arbitrary-executable", Model = "model" },
            ],
        };

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("null or non-empty", StringComparison.Ordinal));
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("duplicate", StringComparison.Ordinal));
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("not present", StringComparison.Ordinal));
    }

    [Fact]
    public void Same_runtime_can_appear_multiple_times_with_ordered_model_fallbacks()
    {
        var options = ValidOptions();
        options.AllowedChildRoles = ["reviewer"];
        options.RoleRoutes = new(StringComparer.Ordinal)
        {
            ["reviewer"] =
            [
                new() { RuntimeProfile = "claude-readonly", Model = "model-a" },
                new() { RuntimeProfile = "claude-readonly", Model = "model-b" },
                new() { RuntimeProfile = "local-pi", Model = "model-c" },
            ],
        };

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }
}
