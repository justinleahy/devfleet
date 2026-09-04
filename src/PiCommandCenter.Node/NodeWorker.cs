using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node;

/// <summary>
/// Node background service: connects outbound to the Control Plane, registers on
/// every connection, replays locally spooled unacknowledged events, heartbeats,
/// and claims work when idle. Each claimed assignment starts exactly one
/// restricted Pi root session via <see cref="PiRootSessionSupervisor"/>; active
/// claims are renewed before expiry; session completion events are only ever
/// produced by the real runtime session.
/// </summary>
public sealed class NodeWorker : BackgroundService
{
    private const int ReplayBatchSize = 100;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly NodeOptions _options;
    private readonly NodeTransportClient _transport;
    private readonly INodeEventSpool _spool;
    private readonly PiRootSessionSupervisor _supervisor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<NodeWorker> _logger;
    private readonly object _claimLock = new();
    private RequestClaimMessage? _activeClaim;

    public NodeWorker(
        IOptions<NodeOptions> options,
        NodeTransportClient transport,
        INodeEventSpool spool,
        PiRootSessionSupervisor supervisor,
        TimeProvider timeProvider,
        ILogger<NodeWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(supervisor);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _transport = transport;
        _spool = spool;
        _supervisor = supervisor;
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

                // A disconnected transport no longer owns the claim; stop the session it ran.
                await DropActiveClaimAsync("transport_disconnected").ConfigureAwait(false);
                await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await SafeDelayAsync(ReconnectBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transport.Connected -= OnConnectedAsync;
            await DropActiveClaimAsync("node_shutdown").ConfigureAwait(false);
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _supervisor.DisposeAsync().ConfigureAwait(false);
            await _spool.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Node {NodeId} stopped.", _options.Id);
        }
    }

    private async Task OnConnectedAsync()
    {
        // A fresh (re)connection resets claim ownership and replays anything spooled.
        await DropActiveClaimAsync("reconnect").ConfigureAwait(false);
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
                // Report the real locally-active runtime session ids.
                var activeSessionIds = _supervisor.ActiveSessionIds;
                await _transport.HeartbeatAsync(activeSessionIds, CancellationToken.None)
                    .ConfigureAwait(false);
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
            // Renew before expiry: renew at half of the remaining lease window.
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
                    await DropActiveClaimAsync("claim_lost").ConfigureAwait(false);
                }
            }

            return;
        }

        var newClaim = await _transport.ClaimNextAsync(_options.ClaimLeaseSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (newClaim is null)
        {
            return;
        }

        _logger.LogInformation(
            "Claimed request {RequestId} (project {ProjectId}) until {LeaseExpiresAt}.",
            newClaim.RequestId,
            newClaim.ProjectId,
            newClaim.LeaseExpiresAt);
        SetActiveClaim(newClaim);

        try
        {
            // One restricted Pi root session per claimed assignment; its lifecycle events are
            // appended durably by the supervisor.
            await _supervisor.StartForClaimAsync(newClaim, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to start the root session for request {RequestId}; releasing the claim.",
                newClaim.RequestId);
            await DropActiveClaimAsync("root_session_start_failed").ConfigureAwait(false);
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

    private async Task DropActiveClaimAsync(string reason)
    {
        RequestClaimMessage? claim;
        lock (_claimLock)
        {
            claim = _activeClaim;
            _activeClaim = null;
        }

        if (claim is not null)
        {
            await _supervisor.StopForRequestAsync(claim.RequestId, reason).ConfigureAwait(false);
        }
    }

    private void SetActiveClaim(RequestClaimMessage claim)
    {
        lock (_claimLock)
        {
            _activeClaim = claim;
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
