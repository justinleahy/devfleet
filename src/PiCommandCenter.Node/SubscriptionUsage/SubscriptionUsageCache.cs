using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.SubscriptionUsage;

public interface ISubscriptionUsageCache
{
    Task<NodeSubscriptionUsageMessage> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps the latest subscription-usage observation in memory. Collection starts once at node
/// startup and then runs on a non-overlapping five-minute cadence; readers never invoke providers.
/// </summary>
public sealed partial class SubscriptionUsageCache : BackgroundService, ISubscriptionUsageCache
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    private readonly IRuntimeSubscriptionUsageProbe _probe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubscriptionUsageCache> _logger;
    private readonly TaskCompletionSource<NodeSubscriptionUsageMessage> _initial =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private NodeSubscriptionUsageMessage? _current;

    public SubscriptionUsageCache(
        IRuntimeSubscriptionUsageProbe probe,
        TimeProvider timeProvider,
        ILogger<SubscriptionUsageCache> logger)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _probe = probe;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task<NodeSubscriptionUsageMessage> GetAsync(CancellationToken cancellationToken = default)
    {
        var current = Volatile.Read(ref _current);
        return current is null
            ? _initial.Task.WaitAsync(cancellationToken)
            : Task.FromResult(current);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _timeProvider);

        await RefreshAsync(stoppingToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            var snapshot = await _probe.GetAsync(stoppingToken).ConfigureAwait(false);
            Volatile.Write(ref _current, snapshot);
            _initial.TrySetResult(snapshot);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRefreshFailure(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Subscription usage cache refresh failed with {ExceptionType}.")]
    private static partial void LogRefreshFailure(ILogger logger, string exceptionType);
}
