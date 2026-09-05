using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.SubscriptionUsage;

/// <summary>
/// Reads one provider-native subscription usage card that supplements the Pi sidecar report.
/// A null result means the source is not configured on this node.
/// </summary>
public interface ISupplementalSubscriptionUsageSource
{
    string Provider { get; }

    Task<ProviderSubscriptionUsageMessage?> ReadAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken);
}
