namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Status values for <see cref="ProviderSubscriptionUsageMessage"/>.
/// <c>available</c> requires nonempty validated windows.
/// </summary>
public static class SubscriptionUsageStatuses
{
    public const string Available = "available";
    public const string Unavailable = "unavailable";
    public const string Error = "error";
}

/// <summary>
/// One provider subscription limit window (not session or context usage).
/// </summary>
public sealed record SubscriptionUsageWindowMessage(
    string Name,
    double? PercentUsed,
    double? PercentRemaining,
    DateTimeOffset? ResetsAt);

/// <summary>
/// Observed subscription usage for one provider. Windows are provider
/// subscription limits, not session or context usage.
/// </summary>
public sealed record ProviderSubscriptionUsageMessage(
    string Provider,
    IReadOnlyList<string> RuntimeProfiles,
    string Status,
    bool? Authenticated,
    string? PlanLabel,
    string? Version,
    IReadOnlyList<SubscriptionUsageWindowMessage> Windows,
    DateTimeOffset ObservedAt,
    string Source,
    string? Diagnostic);

/// <summary>
/// Subscription usage snapshot for one node across providers.
/// </summary>
public sealed record NodeSubscriptionUsageMessage(
    Guid NodeId,
    IReadOnlyList<ProviderSubscriptionUsageMessage> Providers);
