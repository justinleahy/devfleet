using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;


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

    /// <summary>Sends a mail message on behalf of one of this node's agent sessions.</summary>
    public async Task<MailDeliveryMessage> SendMailAsync(SendMailMessage message, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<MailDeliveryMessage>(
            "SendMail",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Replies in an existing thread; recipients are the thread's other participants.</summary>
    public async Task<MailDeliveryMessage> ReplyMailAsync(ReplyMailMessage message, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<MailDeliveryMessage>(
            "ReplyMail",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches the unread inbox for one of this node's agent sessions.</summary>
    public async Task<MailInboxMessage> FetchInboxAsync(FetchMailInboxMessage message, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<MailInboxMessage>(
            "FetchInbox",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches one thread for one of this node's agent sessions.</summary>
    public async Task<MailInboxMessage> FetchThreadAsync(FetchMailThreadMessage message, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<MailInboxMessage>(
            "FetchThread",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Marks one delivered message read for one of this node's agent sessions.</summary>
    public async Task<MailReceiptMessage> MarkMailReadAsync(MarkMailReadMessage message, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<MailReceiptMessage>(
            "MarkMailRead",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acknowledges one delivered message for one of this node's agent sessions.</summary>
    public async Task<MailReceiptMessage> AcknowledgeMailAsync(AcknowledgeMailMessage message, CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<MailReceiptMessage>(
            "AcknowledgeMail",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires a reservation lease, or returns the typed conflict/error.</summary>
    public Task<ReservationOperationResultMessage> AcquireReservationAsync(
        AcquireReservationMessage message,
        CancellationToken cancellationToken)
        => InvokeReservationAsync("AcquireReservation", message, cancellationToken);

    /// <summary>Extends a lease's expiry.</summary>
    public Task<ReservationOperationResultMessage> RenewReservationAsync(
        ReservationMutationMessage message,
        CancellationToken cancellationToken)
        => InvokeReservationAsync("RenewReservation", message, cancellationToken);

    /// <summary>Widens a lease with additional scopes.</summary>
    public Task<ReservationOperationResultMessage> ExpandReservationAsync(
        ExpandReservationMessage message,
        CancellationToken cancellationToken)
        => InvokeReservationAsync("ExpandReservation", message, cancellationToken);

    /// <summary>Releases a lease owned by one of this node's sessions.</summary>
    public Task<ReservationOperationResultMessage> ReleaseReservationAsync(
        ReleaseReservationMessage message,
        CancellationToken cancellationToken)
        => InvokeReservationAsync("ReleaseReservation", message, cancellationToken);

    /// <summary>Moves a lease to a new owner session.</summary>
    public Task<ReservationOperationResultMessage> TransferReservationAsync(
        TransferReservationMessage message,
        CancellationToken cancellationToken)
        => InvokeReservationAsync("TransferReservation", message, cancellationToken);

    /// <summary>
    /// Authorizes one mutation against a lease immediately before it is applied; the
    /// caller MUST invoke this for every reserved filesystem mutation (both source and
    /// destination for moves).
    /// </summary>
    public Task<MutationAuthorizationResultMessage> AuthorizeMutationAsync(
        MutationAuthorizationMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return connection.InvokeAsync<MutationAuthorizationResultMessage>(
            "AuthorizeMutation",
            message,
            cancellationToken);
    }

    /// <summary>Flags a lease as requiring recovery.</summary>
    public Task<ReservationOperationResultMessage> MarkRecoveryRequiredAsync(
        MarkRecoveryMessage message,
        CancellationToken cancellationToken)
        => InvokeReservationAsync("MarkReservationRecovery", message, cancellationToken);

    /// <summary>Lists a project's leases with all transport-visible lease facts.</summary>
    public async Task<ReservationLeaseMessage[]> ListReservationsAsync(
        ListReservationsMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<ReservationLeaseMessage[]>(
            "ListReservations",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one verification command run (one-argument hub method).</summary>
    public async Task<VerificationRunMessage> RecordVerificationAsync(
        VerificationRunMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<VerificationRunMessage>(
            "RecordVerification",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps an application run DTO onto <see cref="RecordVerificationAsync"/>.</summary>
    public Task<VerificationRunMessage> RecordVerificationRunAsync(
        VerificationRunDto run,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        return RecordVerificationAsync(
            new VerificationRunMessage(
                Guid.NewGuid(),
                Guid.Empty,
                run.RequestId,
                "node",
                run.Id,
                run.ProfileId,
                run.CommandId,
                (int)run.Status,
                run.Status.ToString(),
                run.ExitCode,
                run.StartedAt,
                run.CompletedAt,
                run.OutputSummary,
                run.OutputArtifactPath,
                run.Mandatory),
            cancellationToken);
    }

    /// <summary>Evaluates the objective completion gate (one-argument hub method).</summary>
    public async Task<CompletionGateDecisionMessage> EvaluateCompletionAsync(
        EvaluateCompletionMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<CompletionGateDecisionMessage>(
            "EvaluateCompletion",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps application evidence onto <see cref="EvaluateCompletionAsync"/>.</summary>
    public async Task<CompletionGateDecision> EvaluateCompletionAsync(
        Guid projectId,
        Guid requestId,
        string rootSessionId,
        CompletionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var decision = await EvaluateCompletionAsync(
            new EvaluateCompletionMessage(
                Guid.NewGuid(),
                projectId,
                requestId,
                rootSessionId,
                new CompletionEvidenceMessage(
                    evidence.SummaryMarkdown,
                    evidence.ChangedFiles,
                    (evidence.ReviewFindings ?? []).Select(f => new ReviewFindingMessage(
                        f.Id, f.Summary, f.Blocking, f.Resolved, f.UserOverridden)).ToArray(),
                    evidence.VerificationSummary)),
            cancellationToken).ConfigureAwait(false);

        return new CompletionGateDecision(
            decision.Accepted,
            decision.MissingRequirements,
            decision.Result is null
                ? null
                : new RequestResultDto(
                    decision.Result.RequestId,
                    decision.Result.SummaryMarkdown,
                    decision.Result.ChangedFiles,
                    decision.Result.ReviewFindings.Select(f => new ReviewFinding(
                        f.Id, f.Summary, f.Blocking, f.Resolved, f.UserOverridden)).ToArray(),
                    decision.Result.VerificationSummary,
                    decision.Result.CreatedAt));
    }


    private async Task<ReservationOperationResultMessage> InvokeReservationAsync<TMessage>(
        string methodName,
        TMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<ReservationOperationResultMessage>(
            methodName,
            message,
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
