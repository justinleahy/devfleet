namespace HealthDetailsFixture;

/// <summary>
/// Shared registration file. Two writers must not edit this without a handoff.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddFixtureServices(this IServiceCollection services) => services;
}
