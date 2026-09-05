using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.SystemResources;

namespace PiCommandCenter.Node;

/// <summary>
/// The subset of the Control Plane transport the worker loop needs. Implemented by
/// <see cref="NodeTransportClient"/>; faked in tests.
/// </summary>
public interface INodeHubOps : IAsyncDisposable
{
    /// <summary>Raised after the hub is connected and the node is registered.</summary>
    event Func<Task>? Connected;

    /// <summary>Raised when the Control Plane commands this node to cancel a session.</summary>
    event Func<CancelSessionCommand, Task>? CancelSessionReceived;

    HubConnectionState State { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);

    Task HeartbeatAsync(
        IReadOnlyList<string> activeSessionIds,
        NodeResourceSnapshotMessage resources,
        CancellationToken cancellationToken);

    Task<RequestClaimMessage?> ClaimNextAsync(int leaseSeconds, CancellationToken cancellationToken);

    /// <summary>Returns the new lease expiry, or null when the claim was lost.</summary>
    Task<DateTimeOffset?> RenewClaimAsync(RequestClaimMessage claim, CancellationToken cancellationToken);

    Task<NodeEventAcknowledgementMessage> PublishEventsAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken);
}

/// <summary>Starts and tracks root Pi sessions for claimed requests.</summary>
public interface IRootSessionSupervisor
{
    IReadOnlyList<string> ActiveSessionIds { get; }

    Task<string> StartForClaimAsync(RequestClaimMessage claim, CancellationToken cancellationToken);

    Task<bool> CancelSessionAsync(string sessionId, string reason);
    Guid? FindRequestId(string sessionId);
}


/// <summary>
/// Cancels one locally running agent session by id. Implemented by the child session
/// supervisor; abstracted so the worker loop stays testable.
/// </summary>
public interface ISessionCanceller
{
    Task<bool> CancelSessionAsync(string sessionId, string reason);

    IReadOnlyList<string> ActiveSessionIds { get; }
}

/// <summary>
/// Node background service: connects outbound to the Control Plane, registers on
/// every connection, replays locally spooled unacknowledged events, heartbeats, and
/// holds up to <see cref="NodeOptions.MaxConcurrentRequests"/> concurrent claims.
/// Claims are renewed before expiry and reconciled (never dropped silently) after a
/// reconnect so locally running sessions keep running across Control Plane restarts.
/// </summary>
public sealed class NodeWorker : BackgroundService
{
    internal const int ReplayBatchSize = 100;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ReconnectBackoff = TimeSpan.FromSeconds(5);

    private readonly NodeOptions _options;
    private readonly INodeHubOps _transport;
    private readonly INodeEventSpool _spool;
    private readonly ISessionCanceller _sessionCanceller;
    private readonly IRootSessionSupervisor _rootSessions;
    private readonly TimeProvider _timeProvider;
    private readonly INodeSystemResourceMonitor _resourceMonitor;
    private readonly ILogger<NodeWorker> _logger;
    private readonly object _claimsLock = new();
    private readonly Dictionary<Guid, RequestClaimMessage> _activeClaims = new();
    private DateTimeOffset _lastHeartbeat = DateTimeOffset.MinValue;
    private DateTimeOffset? _nextClaimEligibleAt;

    public NodeWorker(
        IOptions<NodeOptions> options,
        INodeHubOps transport,
        INodeEventSpool spool,
        TimeProvider timeProvider,
        INodeSystemResourceMonitor resourceMonitor,
        ISessionCanceller sessionCanceller,
        IRootSessionSupervisor rootSessions,
        ILogger<NodeWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(spool);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(resourceMonitor);
        ArgumentNullException.ThrowIfNull(sessionCanceller);
        ArgumentNullException.ThrowIfNull(rootSessions);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _transport = transport;
        _spool = spool;
        _timeProvider = timeProvider;
        _resourceMonitor = resourceMonitor;
        _sessionCanceller = sessionCanceller;
        _rootSessions = rootSessions;
        _logger = logger;

        _transport.Connected += HandleConnectedAsync;
        _transport.CancelSessionReceived += OnCancelSessionReceivedAsync;
    }

    private async Task OnCancelSessionReceivedAsync(CancelSessionCommand command)
    {
        _logger.LogInformation(
            "Control Plane requested cancellation of session {SessionId}: {Reason}",
            command.SessionId,
            command.Reason);
        var rootRequestId = _rootSessions.FindRequestId(command.SessionId);
        var stopped = await _sessionCanceller
            .CancelSessionAsync(command.SessionId, command.Reason)
            .ConfigureAwait(false);
        if (stopped && rootRequestId is { } requestId)
        {
            RemoveClaim(requestId);
        }
        if (!stopped)
        {
            _logger.LogWarning(
                "Session {SessionId} could not be cancelled locally; it may have already stopped.",
                command.SessionId);
        }
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

                // Active local sessions are never stopped on disconnect; their events
                // keep spooling locally and claims are reconciled on the next connect.
                await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
                await SafeDelayAsync(ReconnectBackoff, stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _transport.Connected -= HandleConnectedAsync;
            _transport.CancelSessionReceived -= OnCancelSessionReceivedAsync;
            await _transport.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _spool.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Node {NodeId} stopped.", _options.Id);
        }
    }

    /// <summary>
    /// Reconnect handler: replay spooled events and reconcile every active claim by
    /// renewing it. Claims the server no longer recognises are dropped; the rest
    /// keep their (running) sessions alive untouched.
    /// </summary>
    internal async Task HandleConnectedAsync()
    {
        await ReconcileClaimsAsync(CancellationToken.None).ConfigureAwait(false);
        await ReplayPendingEventsAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task RunConnectedLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested
               && _transport.State == HubConnectionState.Connected)
        {
            await RunTickAsync(_timeProvider.GetUtcNow(), CancellationToken.None).ConfigureAwait(false);
            await SafeDelayAsync(TickInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>One deterministic unit of worker work at a point in time.</summary>
    internal async Task RunTickAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await RenewDueClaimsAsync(now, cancellationToken).ConfigureAwait(false);
        await ReplayPendingEventsAsync(cancellationToken).ConfigureAwait(false);
        await ClaimIfCapacityAsync(cancellationToken).ConfigureAwait(false);

        if (now - _lastHeartbeat >= TimeSpan.FromSeconds(_options.HeartbeatSeconds))
        {
            var resources = _resourceMonitor.Capture();
            await _transport
                .HeartbeatAsync(_sessionCanceller.ActiveSessionIds, resources, cancellationToken)
                .ConfigureAwait(false);
            _lastHeartbeat = now;
        }
    }

    internal async Task ReplayPendingEventsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested
               && _transport.State == HubConnectionState.Connected)
        {
            var pending = await _spool.PeekPendingAsync(ReplayBatchSize, cancellationToken).ConfigureAwait(false);
            if (pending.Count == 0)
            {
                break;
            }

            _logger.LogInformation("Replaying {Count} pending node event(s).", pending.Count);
            var acknowledgement = await _transport.PublishEventsAsync(pending, cancellationToken)
                .ConfigureAwait(false);
            if (acknowledgement.EventIds.Count > 0)
            {
                await _spool.DeleteAsync(acknowledgement.EventIds, cancellationToken).ConfigureAwait(false);
            }

            if (acknowledgement.EventIds.Count == 0)
            {
                // Nothing was accepted; avoid spinning on the same batch.
                break;
            }
        }
    }

    private async Task RenewDueClaimsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        List<RequestClaimMessage> due;
        lock (_claimsLock)
        {
            // Renew strictly before expiry: once one-third of the requested lease
            // remains (i.e. two-thirds elapsed).
            var renewalThreshold = TimeSpan.FromSeconds(_options.ClaimLeaseSeconds / 3.0);
            due = _activeClaims.Values
                .Where(claim => now >= claim.LeaseExpiresAt - renewalThreshold)
                .ToList();
        }

        foreach (var claim in due)
        {
            var newExpiry = await _transport.RenewClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            if (newExpiry is DateTimeOffset expiry && expiry > now)
            {
                _logger.LogDebug("Renewed claim for request {RequestId} until {LeaseExpiresAt}.", claim.RequestId, expiry);
                ReplaceClaim(claim with { LeaseExpiresAt = expiry });
            }
            else
            {
                _logger.LogInformation(
                    "Claim for request {RequestId} was not renewed; it was lost or expired.",
                    claim.RequestId);
                RemoveClaim(claim.RequestId);
            }
        }
    }

    private async Task ClaimIfCapacityAsync(CancellationToken cancellationToken)
    {
        int activeCount;
        lock (_claimsLock)
        {
            activeCount = _activeClaims.Count;
        }

        if (activeCount >= _options.MaxConcurrentRequests)
        {
            return;
        }

        // Back off briefly after a failed claim to avoid hammering the queue.
        if (_nextClaimEligibleAt is DateTimeOffset eligibleAt && _timeProvider.GetUtcNow() < eligibleAt)
        {
            return;
        }

        var newClaim = await _transport.ClaimNextAsync(_options.ClaimLeaseSeconds, cancellationToken)
            .ConfigureAwait(false);
        if (newClaim is not null)
        {
            if (TrackClaim(newClaim))
            {
                try
                {
                    await _rootSessions.StartForClaimAsync(newClaim, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation(
                        "Claimed request {RequestId} (project {ProjectId}) until {LeaseExpiresAt}.",
                        newClaim.RequestId,
                        newClaim.ProjectId,
                        newClaim.LeaseExpiresAt);
                }
                catch (InvalidOperationException ex)
                {
                    await AppendStartupBlockedAsync(newClaim, ex, cancellationToken).ConfigureAwait(false);
                    RemoveClaim(newClaim.RequestId);
                    _logger.LogWarning(
                        "Request {RequestId} was blocked during root startup: {Reason}",
                        newClaim.RequestId,
                        Security.DiagnosticSanitizer.Sanitize(ex.Message, 512));
                }
                catch
                {
                    RemoveClaim(newClaim.RequestId);
                    throw;
                }
            }

            _nextClaimEligibleAt = null;
        }
        else
        {
            _nextClaimEligibleAt = _timeProvider.GetUtcNow().AddSeconds(1);
        }
    }

    private Task AppendStartupBlockedAsync(
        RequestClaimMessage claim,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var sessionId = $"root-start-{claim.RequestId:N}";
        var reason = Security.DiagnosticSanitizer.Sanitize(exception.Message, 512);
        return _spool.AppendAsync(
            new NodeEventMessage(
                EventId: $"{sessionId}-1-request.blocked",
                NodeId: claim.NodeId,
                ProjectId: claim.ProjectId,
                RequestId: claim.RequestId,
                SessionId: sessionId,
                Sequence: 1,
                Type: "request.blocked",
                OccurredAt: _timeProvider.GetUtcNow(),
                PayloadJson: JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["status"] = "blocked",
                    ["reason"] = reason,
                    ["phase"] = "root_start",
                })),
            cancellationToken);
    }

    /// <summary>Renews every active claim after a reconnect; drops only rejected ones.</summary>
    private async Task ReconcileClaimsAsync(CancellationToken cancellationToken)
    {
        RequestClaimMessage[] claims;
        lock (_claimsLock)
        {
            claims = [.. _activeClaims.Values];
        }

        foreach (var claim in claims)
        {
            var newExpiry = await _transport.RenewClaimAsync(claim, cancellationToken).ConfigureAwait(false);
            if (newExpiry is DateTimeOffset expiry && expiry > _timeProvider.GetUtcNow())
            {
                ReplaceClaim(claim with { LeaseExpiresAt = expiry });
            }
            else
            {
                _logger.LogInformation(
                    "Claim for request {RequestId} was rejected on reconnect and released.",
                    claim.RequestId);
                RemoveClaim(claim.RequestId);
            }
        }
    }

    private bool TrackClaim(RequestClaimMessage claim)
    {
        lock (_claimsLock)
        {
            if (_activeClaims.ContainsKey(claim.RequestId))
            {
                return false;
            }

            _activeClaims[claim.RequestId] = claim;
            return true;
        }
    }

    private void ReplaceClaim(RequestClaimMessage claim)
    {
        lock (_claimsLock)
        {
            _activeClaims[claim.RequestId] = claim;
        }
    }

    private void RemoveClaim(Guid requestId)
    {
        lock (_claimsLock)
        {
            _activeClaims.Remove(requestId);
        }
    }

    internal IReadOnlyDictionary<Guid, RequestClaimMessage> ActiveClaimsSnapshot()
    {
        lock (_claimsLock)
        {
            return new Dictionary<Guid, RequestClaimMessage>(_activeClaims);
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
