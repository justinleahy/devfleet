using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node;

/// <summary>
/// Outbound SignalR transport to the Control Plane <c>/nodeHub</c>. Reconnects
/// automatically with bounded exponential backoff and raises <see cref="Connected"/>
/// after registration succeeds on every (re)connection.
/// </summary>
public sealed class NodeTransportClient : IAsyncDisposable
{
    private readonly NodeOptions _options;
    private readonly ILogger<NodeTransportClient> _logger;
    private HubConnection? _connection;

    /// <summary>Raised after the hub is connected and this node is registered.</summary>
    public event Func<Task>? Connected;

    public NodeTransportClient(IOptions<NodeOptions> options, ILogger<NodeTransportClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;
    }

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        var hubUrl = new Uri(new Uri(_options.ControlPlaneUrl), "/nodeHub");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new BoundedExponentialRetryPolicy(_logger))
            .Build();

        connection.Reconnected += OnReconnectedAsync;
        connection.Closed += OnClosedAsync;

        await connection.StartAsync(cancellationToken).ConfigureAwait(false);
        _connection = connection;

        await RegisterAsync(CancellationToken.None).ConfigureAwait(false);
        await RaiseConnectedAsync().ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_connection is not HubConnection connection)
        {
            return;
        }

        _connection = null;
        connection.Reconnected -= OnReconnectedAsync;
        connection.Closed -= OnClosedAsync;
        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disposing node hub connection.");
        }
    }

    public Task RegisterAsync(CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return connection.InvokeAsync(
            "Register",
            new NodeRegistrationMessage(
                _options.Id,
                _options.DisplayName,
                _options.AgentVersion,
                _options.CapabilitiesJson),
            cancellationToken);
    }

    public async Task HeartbeatAsync(IReadOnlyList<string> activeSessionIds, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        await connection.InvokeAsync(
            "Heartbeat",
            new NodeHeartbeatMessage(_options.Id, activeSessionIds),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RequestClaimMessage?> ClaimNextAsync(int leaseSeconds, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<RequestClaimMessage?>(
            "ClaimNext",
            new ClaimRequestMessage(_options.Id, leaseSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renews an active claim. Returns the new lease expiry, or null when the claim
    /// is unknown, expired, or held by another node.
    /// </summary>
    public async Task<DateTimeOffset?> RenewClaimAsync(
        RequestClaimMessage claim,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim",
            new ClaimRenewalMessage(claim.RequestId, claim.NodeId, claim.ClaimToken, _options.ClaimLeaseSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<NodeEventAcknowledgementMessage> PublishEventsAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
            "PublishEvents",
            new NodeEventBatchMessage(events),
            cancellationToken).ConfigureAwait(false);
    }

    private Task OnReconnectedAsync(string? connectionId)
    {
        _logger.LogInformation("Node hub connection re-established (connection {ConnectionId}).", connectionId);
        return RegisterThenRaiseAsync();
    }

    private async Task RegisterThenRaiseAsync()
    {
        try
        {
            await RegisterAsync(CancellationToken.None).ConfigureAwait(false);
            await RaiseConnectedAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to re-register after reconnect; the outer loop will retry.");
        }
    }

    private Task OnClosedAsync(Exception? error)
    {
        if (error is not null)
        {
            _logger.LogWarning(error, "Node hub connection closed with an error.");
        }
        else
        {
            _logger.LogInformation("Node hub connection closed.");
        }

        return Task.CompletedTask;
    }

    private async Task RaiseConnectedAsync()
    {
        var handler = Connected;
        if (handler is not null)
        {
            await handler().ConfigureAwait(false);
        }
    }

    private HubConnection RequireConnection()
    {
        return _connection ?? throw new InvalidOperationException(
            "The node transport is not connected; call StartAsync first.");
    }

    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);
}

/// <summary>
/// Infinite reconnect retry policy with exponential backoff capped at 30 seconds.
/// </summary>
public sealed class BoundedExponentialRetryPolicy : IRetryPolicy
{
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);
    private readonly ILogger _logger;

    public BoundedExponentialRetryPolicy(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        ArgumentNullException.ThrowIfNull(retryContext);
        var delay = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, retryContext.PreviousRetryCount), MaxDelay.TotalSeconds));
        _logger.LogDebug("Node hub reconnect attempt {Attempt} after {Delay}.", retryContext.PreviousRetryCount + 1, delay);
        return delay;
    }
}
