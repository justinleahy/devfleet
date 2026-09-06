using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Node.Projects;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node;

/// <summary>
/// Outbound SignalR transport to the Control Plane <c>/nodeHub</c>. Reconnects
/// automatically with bounded exponential backoff and raises <see cref="Connected"/>
/// after registration succeeds on every (re)connection.
/// </summary>
public sealed class NodeTransportClient : INodeHubOps
{
    private readonly NodeOptions _options;
    private readonly NodeCredentialLoader _credentials;
    private readonly ILogger<NodeTransportClient> _logger;
    private readonly INodeRuntimeRoutingStore _routing;
    private readonly IRuntimeModelDiscovery _models;
    private readonly ISubscriptionUsageCache _usage;
    private readonly IWorkspaceBindingValidator _workspaceBindingValidator;
    private readonly IWorkspaceDirectoryBrowser _workspaceDirectoryBrowser;
    private HubConnection? _connection;
    private NodeCredential? _credential;

    /// <summary>Raised after the hub is connected and this node is registered.</summary>
    public event Func<Task>? Connected;

    /// <summary>
    /// Raised when the Control Plane commands this node to cancel an agent session (SPEC §23.2).
    /// The subscriber (session supervisor) stops the runtime and reports the outcome by
    /// publishing a real <c>session.cancelled</c> event through <see cref="PublishEventsAsync"/>.
    /// </summary>
    public event Func<CancelSessionCommand, Task>? CancelSessionReceived;

    /// <summary>Raised when the Control Plane cancels an assignment owned by this node.</summary>
    public event Func<CancelAssignmentCommand, Task>? CancelAssignmentReceived;

    public NodeTransportClient(
        IOptions<NodeOptions> options,
        NodeCredentialLoader credentials,
        INodeRuntimeRoutingStore routing,
        IRuntimeModelDiscovery models,
        ISubscriptionUsageCache usage,
        IWorkspaceBindingValidator workspaceBindings,
        IWorkspaceDirectoryBrowser workspaceDirectoryBrowser,
        ILogger<NodeTransportClient> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(usage);
        ArgumentNullException.ThrowIfNull(workspaceBindings);
        ArgumentNullException.ThrowIfNull(workspaceDirectoryBrowser);
        _options = options.Value;
        _credentials = credentials;
        _routing = routing;
        _models = models;
        _usage = usage;
        _workspaceBindingValidator = workspaceBindings;
        _workspaceDirectoryBrowser = workspaceDirectoryBrowser;
        _logger = logger;
    }

    public HubConnectionState State => _connection?.State ?? HubConnectionState.Disconnected;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_connection is not null)
        {
            return;
        }

        var controlPlaneUri = NodeOptionsValidator.CreateControlPlaneUri(_options.ControlPlaneUrl);
        _credential = _credentials.Load();

        var hubUrl = new Uri(controlPlaneUri, "/nodeHub");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl, http =>
            {
                http.HttpMessageHandlerFactory = DisableAutomaticRedirects;
                ApplyCredential(http);
            })
            .WithAutomaticReconnect(new BoundedExponentialRetryPolicy(_logger))
            .Build();

        connection.Reconnected += OnReconnectedAsync;
        connection.Closed += OnClosedAsync;
        connection.On<CancelSessionCommand>("CancelSession", OnCancelSessionAsync);
        connection.On<CancelAssignmentCommand>("CancelAssignment", OnCancelAssignmentAsync);
        connection.On<NodeRuntimeConfigurationMessage>(
            "GetRuntimeConfiguration",
            () => Task.FromResult(_routing.Current));
        connection.On<IReadOnlyList<RuntimeModelCatalogMessage>>(
            "DiscoverRuntimeModels",
            () => _models.DiscoverAsync());
        connection.On<NodeSubscriptionUsageMessage>("GetSubscriptionUsage", () => _usage.GetAsync());
        connection.On<UpdateNodeRuntimeConfigurationMessage, NodeRuntimeConfigurationMessage>(
            "UpdateRuntimeConfiguration",
            update => _routing.UpdateAsync(update));

        connection.On<WorkspaceBindingValidationRequestMessage, WorkspaceBindingValidationResultMessage>(
            "ValidateWorkspaceBinding",
            request => _workspaceBindingValidator.ValidateAsync(request));

        connection.On<WorkspaceDirectoryBrowseRequestMessage, WorkspaceDirectoryBrowseResponseMessage>(
            WorkspaceDirectoryBrowseCallback.MethodName,
            request => _workspaceDirectoryBrowser.BrowseAsync(request));
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

    public async Task HeartbeatAsync(
        IReadOnlyList<string> activeSessionIds,
        NodeResourceSnapshotMessage resources,
        NodeExecutionStatusMessage executionStatus,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(executionStatus);
        var connection = RequireConnection();
        await connection.InvokeAsync(
            "Heartbeat",
            new NodeHeartbeatMessage(_options.Id, activeSessionIds, resources, executionStatus),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIdentityMessage> AllocateAgentIdentityAsync(
        AllocateAgentIdentityMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<AgentIdentityMessage>(
            "AllocateAgentIdentity",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseAgentIdentityAsync(
        ReleaseAgentIdentityMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        await connection.InvokeAsync(
            "ReleaseAgentIdentity",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentIdentityMessage?> FindAgentIdentityAsync(
        FindAgentIdentityMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<AgentIdentityMessage?>(
            "FindAgentIdentity",
            message,
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
    public async Task<VerificationRunResultMessage> RecordVerificationAsync(
        VerificationRunMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<VerificationRunResultMessage>(
            "RecordVerification",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps an application run DTO to and from the verification transport contract.</summary>
    public async Task<VerificationRunDto> RecordVerificationRunAsync(
        VerificationRunDto run,
        Guid projectId,
        string claimToken,
        string sessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var correlationId = Guid.NewGuid();
        var result = await RecordVerificationAsync(
            CreateVerificationRunMessage(run, projectId, claimToken, sessionId, correlationId),
            cancellationToken).ConfigureAwait(false);

        if (result.CorrelationId != correlationId
            || result.ProjectId != projectId
            || result.RequestId != run.RequestId
            || !string.Equals(result.SessionId, sessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The verification response does not match the submitted command.");
        }

        return new VerificationRunDto(
            result.Id,
            result.RequestId,
            result.ProfileId,
            result.CommandId,
            (VerificationRunStatus)result.Status,
            result.ExitCode,
            result.StartedAt,
            result.CompletedAt,
            result.OutputSummary,
            result.OutputArtifactPath,
            result.Mandatory);
    }

    internal static VerificationRunMessage CreateVerificationRunMessage(
        VerificationRunDto run,
        Guid projectId,
        string claimToken,
        string sessionId,
        Guid correlationId)
    {
        return new VerificationRunMessage(
            correlationId,
            projectId,
            run.RequestId,
            claimToken,
            sessionId,
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
            run.Mandatory);
    }

    /// <summary>First terminalization step (one-argument hub method).</summary>
    public async Task<CompletionGateDecisionMessage> BeginTerminalizationAsync(
        BeginTerminalizationMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<CompletionGateDecisionMessage>(
            "BeginTerminalization",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Second terminalization step (one-argument hub method).</summary>
    public async Task<CompletionGateDecisionMessage> ConfirmTerminalizationAsync(
        ConfirmTerminalizationMessage message,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<CompletionGateDecisionMessage>(
            "ConfirmTerminalization",
            message,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps application evidence onto <see cref="BeginTerminalizationAsync(BeginTerminalizationMessage, CancellationToken)"/>.</summary>
    public async Task<CompletionGateDecision> BeginTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string claimToken,
        string? rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        CancellationToken cancellationToken)
    {
        var correlationId = Guid.NewGuid();
        var decision = await BeginTerminalizationAsync(
            new BeginTerminalizationMessage(
                correlationId,
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                intent,
                MapEvidence(evidence),
                reason),
            cancellationToken).ConfigureAwait(false);
        return MapDecision(correlationId, decision);
    }

    /// <summary>Maps application evidence and the quiescence proof onto <see cref="ConfirmTerminalizationAsync(ConfirmTerminalizationMessage, CancellationToken)"/>.</summary>
    public async Task<CompletionGateDecision> ConfirmTerminalizationAsync(
        Guid projectId,
        Guid requestId,
        string claimToken,
        string? rootSessionId,
        TerminalizationIntent intent,
        CompletionEvidence? evidence,
        string? reason,
        AssignmentQuiescenceProof proof,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var correlationId = Guid.NewGuid();
        var decision = await ConfirmTerminalizationAsync(
            new ConfirmTerminalizationMessage(
                correlationId,
                projectId,
                requestId,
                claimToken,
                rootSessionId,
                intent,
                MapEvidence(evidence),
                reason,
                new AssignmentQuiescenceProofMessage(
                    proof.AdmissionClosed,
                    proof.ActiveChildren,
                    proof.ActiveOperations,
                    proof.ActiveProcesses,
                    proof.PendingEvents,
                    proof.ActiveReservations,
                    proof.RepositoryInspected,
                    proof.ObservedAt)),
            cancellationToken).ConfigureAwait(false);
        return MapDecision(correlationId, decision);
    }

    private static CompletionEvidenceMessage? MapEvidence(CompletionEvidence? evidence)
        => evidence is null
            ? null
            : new CompletionEvidenceMessage(
                evidence.SummaryMarkdown,
                evidence.ChangedFiles,
                (evidence.ReviewFindings ?? []).Select(f => new ReviewFindingMessage(
                    f.Id, f.Summary, f.Blocking, f.Resolved, f.UserOverridden)).ToArray(),
                evidence.VerificationSummary,
                evidence.RequestBranch,
                evidence.CheckpointCommitId);

    /// <summary>Fails closed when the authority's decision does not echo the submitted fence.</summary>
    private static CompletionGateDecision MapDecision(
        Guid correlationId,
        CompletionGateDecisionMessage decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.CorrelationId != correlationId)
        {
            throw new InvalidOperationException(
                "The terminalization decision does not match the submitted command.");
        }

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
                    decision.Result.CreatedAt,
                    decision.Result.RequestBranch,
                    decision.Result.CheckpointCommitId));
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

    public async Task<ReconcileAssignmentsResultMessage> ReconcileAssignmentsAsync(
        IReadOnlyList<ExecutionAssignmentInventoryItemMessage> assignments,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<ReconcileAssignmentsResultMessage>(
            "ReconcileAssignments",
            new ReconcileAssignmentsMessage(
                _options.Id,
                _options.ClaimLeaseSeconds,
                assignments),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionAssignmentMessage?> ClaimNextAsync(
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<ExecutionAssignmentMessage?>(
            "ClaimNext",
            new ClaimRequestMessage(_options.Id, leaseSeconds),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renews an active assignment. Returns the new lease expiry, or null when the assignment
    /// is unknown, expired, or held by another node.
    /// </summary>
    public async Task<DateTimeOffset?> RenewAssignmentAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken)
    {
        var connection = RequireConnection();
        return await connection.InvokeAsync<DateTimeOffset?>(
            "RenewClaim",
            new ClaimRenewalMessage(
                assignment.RequestId,
                assignment.NodeIdSnapshot,
                assignment.ClaimToken,
                _options.ClaimLeaseSeconds),
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

    private async Task OnCancelSessionAsync(CancelSessionCommand command)
    {
        if (CancelSessionReceived is not { } handler)
        {
            _logger.LogWarning(
                "CancelSession for {SessionId} received but no supervisor subscribed; session keeps running.",
                command.SessionId);
            return;
        }

        try
        {
            await handler(command).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CancelSession handler failed for {SessionId}.", command.SessionId);
        }
    }

    private async Task OnCancelAssignmentAsync(CancelAssignmentCommand command)
    {
        if (CancelAssignmentReceived is not { } handler)
        {
            _logger.LogWarning(
                "CancelAssignment for {RequestId} received but no worker subscribed; assignment remains active.",
                command.RequestId);
            return;
        }

        try
        {
            await handler(command).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "CancelAssignment handler failed for {RequestId}.",
                command.RequestId);
        }
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
    internal static HttpMessageHandler DisableAutomaticRedirects(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        var current = handler;

        while (true)
        {
            switch (current)
            {
                case HttpClientHandler httpClientHandler:
                    httpClientHandler.AllowAutoRedirect = false;
                    return handler;
                case SocketsHttpHandler socketsHttpHandler:
                    socketsHttpHandler.AllowAutoRedirect = false;
                    return handler;
                case DelegatingHandler { InnerHandler: { } innerHandler }:
                    current = innerHandler;
                    break;
                default:
                    throw new InvalidOperationException(
                        "The SignalR HTTP handler does not expose automatic redirect configuration.");
            }
        }
    }

    private void ApplyCredential(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions http)
    {
        var credential = _credential ?? throw new InvalidOperationException("Node credential was not loaded.");
        var token = credential.TokenHex;
        if (string.Equals(credential.Header, "Authorization", StringComparison.OrdinalIgnoreCase)
            && string.Equals(credential.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            http.AccessTokenProvider = () => Task.FromResult<string?>(token);
            return;
        }

        var value = string.IsNullOrEmpty(credential.Scheme)
            ? token
            : credential.Scheme + " " + token;
        http.Headers[credential.Header] = value;
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
