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
    public void Route_candidates_must_be_canonical_selectors_and_unique()
    {
        var options = ValidOptions();
        options.AllowedChildRoles = ["reviewer"];
        options.RoleRoutes = new(StringComparer.Ordinal)
        {
            ["reviewer"] =
            [
                new() { Model = "claude-code/default" },
                new() { Model = " claude-code/default " },
                new() { Model = "pi/model" },
                new() { Model = "opus" },
                new() { Model = " " },
            ],
        };

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.False(result.Succeeded);
        var failures = result.Failures ?? [];
        Assert.Contains(failures, failure => failure.Contains("duplicate model candidate 'claude-code/default'", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("got 'pi/model'", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("got 'opus'", StringComparison.Ordinal));
        Assert.Contains(failures, failure => failure.Contains("got ' '", StringComparison.Ordinal));
        Assert.Equal(4, failures.Count());
    }

    [Fact]
    public void Root_model_must_be_a_canonical_selector()
    {
        var options = ValidOptions();
        options.Model = "gpt-6";

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("'Pi:Model'", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_model_rejects_official_harness_providers()
    {
        var options = ValidOptions();
        options.Model = "claude-code/default";

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("'Pi:Model'", StringComparison.Ordinal));
        Assert.Contains(result.Failures ?? [], failure => failure.Contains("Pi-backed provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_model_accepts_any_pi_backed_provider()
    {
        var options = ValidOptions();
        options.Model = "codex/default";

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));

        options.Model = "zai/glm-4.7";

        result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }

    [Fact]
    public void Same_provider_can_appear_multiple_times_with_ordered_model_fallbacks()
    {
        var options = ValidOptions();
        options.AllowedChildRoles = ["reviewer"];
        options.RoleRoutes = new(StringComparer.Ordinal)
        {
            ["reviewer"] =
            [
                new() { Model = "claude-code/model-a" },
                new() { Model = "claude-code/model-b" },
                new() { Model = "codex/model-c" },
            ],
        };

        var result = new PiWorkerOptionsValidator().Validate(PiWorkerOptions.SectionName, options);

        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Failures ?? []));
    }
}
