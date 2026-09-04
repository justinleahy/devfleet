using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node;

/// <summary>
/// Node background service: connects outbound to the Control Plane, registers on
/// every connection, replays locally spooled unacknowledged events, heartbeats,
/// and claims work when idle. Active claims are renewed before expiry; session
/// completion events are only ever produced by a real runtime session.
/// </summary>
public sealed class NodeWorker : BackgroundService
{
    private const int ReplayBatchSize = 100;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly NodeOptions _options;
    private readonly NodeTransportClient _transport;
    private readonly INodeEventSpool _spool;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NodeWorker> _logger;
    private readonly object _claimLock = new();
    private RequestClaimMessage? _activeClaim;

    public NodeWorker(
        IOptions<NodeOptions> options,
        NodeTransportClient transport,
        INodeEventSpool spool,
        TimeProvider timeProvider,
        ILogger<NodeWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _transport = transport;
        _spool = spool;
        _timeProvider = timeProvider;
        _logger = logger;

        _transport.Connected += OnConnectedAsync;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _transport.StartAsync(stoppingToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Node {NodeId} connected to control plane {ControlPlaneUrl}.",
                        _options.Id,
                        _options.ControlPlaneUrl);

                    await RunConnectedLoopAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Node loop failed; retrying in {Backoff}.", ReconnectBackoff);
                }

                ClearActiveClaim();
                await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await SafeDelayAsync(ReconnectBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transport.Connected -= OnConnectedAsync;
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _spool.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Node {NodeId} stopped.", _options.Id);
        }
    }

    private async Task OnConnectedAsync()
    {
        // A fresh (re)connection resets claim ownership and replays anything spooled.
        ClearActiveClaim();
        await ReplayPendingEventsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task RunConnectedLoopAsync(CancellationToken stoppingToken)
    {
        var lastHeartbeat = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested
               && _transport.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            var now = _timeProvider.GetUtcNow();

            if (now - lastHeartbeat >= TimeSpan.FromSeconds(_options.HeartbeatSeconds))
            {
                // Only real locally-active runtime sessions are reported; none exist yet.
                await _transport.HeartbeatAsync([], CancellationToken.None).ConfigureAwait(false);
                lastHeartbeat = now;
            }

            await RenewOrClaimAsync(now, CancellationToken.None).ConfigureAwait(false);

            await SafeDelayAsync(TickInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RenewOrClaimAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var claim = _activeClaim;
        if (claim is not null)
        {
            // Renew before expiry: renew at two-thirds of the remaining lease window.
            var remaining = claim.LeaseExpiresAt - now;
            var halfLife = remaining >= TimeSpan.Zero ? remaining / 2 : TimeSpan.Zero;
            if (remaining <= halfLife)
            {
                var newExpiry = await _transport.RenewClaimAsync(claim, cancellationToken).ConfigureAwait(false);
                if (newExpiry is DateTimeOffset expiry)
                {
                    SetActiveClaim(claim with { LeaseExpiresAt = expiry });
                }
                else
                {
                    _logger.LogInformation(
                        "Claim for request {RequestId} was not renewed; it was lost or expired.",
                        claim.RequestId);
                    ClearActiveClaim();
                }
            }

            return;
        }

        var newClaim = await _transport.ClaimNextAsync(_options.ClaimLeaseSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (newClaim is not null)
        {
            _logger.LogInformation(
                "Claimed request {RequestId} (project {ProjectId}) until {LeaseExpiresAt}.",
                newClaim.RequestId,
                newClaim.ProjectId,
                newClaim.LeaseExpiresAt);
            SetActiveClaim(newClaim);
        }
    }

    private async Task ReplayPendingEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
               && _transport.State == Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Connected)
        {
            var pending = await _spool.PeekPendingAsync(ReplayBatchSize, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                break;
            }

            _logger.LogInformation("Replaying {Count} pending node event(s).", pending.Count);
            var acknowledgement = await _transport.PublishEventsAsync(pending, cancellationToken)
                .ConfigureAwait(false);
            await _spool.DeleteAsync(acknowledgement.EventIds, cancellationToken).ConfigureAwait(false);

            if (acknowledgement.EventIds.Count == 0)
            {
                // Nothing was accepted; avoid spinning on the same batch.
                break;
            }
        }
    }

    private void SetActiveClaim(RequestClaimMessage claim)
    {
        lock (_claimLock)
        {
            _activeClaim = claim;
        }
    }

    private void ClearActiveClaim()
    {
        lock (_claimLock)
        {
            _activeClaim = null;
        }
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
