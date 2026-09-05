using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.ControlPlane.RuntimeRouting;

namespace PiCommandCenter.ControlPlane.Hubs;

/// <summary>
/// Protocol bounds applied to inbound node messages before they reach application
/// services. Values are deliberately conservative: a misbehaving node must not be
/// able to exhaust control-plane memory or lease bookkeeping.
/// </summary>
public static class NodeTransportLimits
{
    public const int MinLeaseSeconds = 10;
    public const int MaxLeaseSeconds = 300;
    public const int MaxEventBatchCount = 500;
    public const int MaxPayloadBytes = 256 * 1024;
    public const int MaxActiveSessionIds = 200;
    public const int MaxMailPayloadBytes = 64 * 1024;
    public const int MaxMailInboxCount = 200;
    public const int MaxSessionIdLength = 128;
    public const int MaxVerificationIdLength = 128;
    public const int MaxVerificationOutputBytes = 16_384;
    public const int MaxArtifactPathBytes = 1024;
    public const int MaxCompletionSummaryBytes = 64 * 1024;
    public const int MaxChangedFiles = 500;
    public const int MaxReviewFindings = 200;
}

/// <summary>
/// Server-only SignalR hub for the node fleet. Public methods adapt primitive
/// transport messages onto application services; they never trust node-supplied
/// bounds (lease seconds, batch sizes, payload sizes) verbatim.
/// </summary>
public sealed class NodeHub(
    INodeRegistry registry,
    INodeEventSink eventSink,
    IReservationService reservationService,
    IMessageService messageService,
    IAgentIdentityRegistry identityRegistry,
    IRequestClaimService claimService,
    IVerificationRunStore verificationRuns,
    ICompletionGateService completionGate,
    NodeConnectionDirectory nodeConnections,
    TimeProvider timeProvider,
    ILogger<NodeHub> logger) : Hub
{
    public async Task<NodeDto> Register(NodeRegistrationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var registered = await registry.RegisterAsync(
            new RegisterNodeCommand(
                new NodeId(message.NodeId),
                message.DisplayName,
                message.AgentVersion,
                message.CapabilitiesJson),
            timeProvider.GetUtcNow(),
            Context.ConnectionAborted).ConfigureAwait(false);
        nodeConnections.Bind(message.NodeId, Context.ConnectionId);
        return registered;
    }

    public async Task<NodeDto> Heartbeat(NodeHeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sessionIds = (message.ActiveSessionIds ?? [])
            .Take(NodeTransportLimits.MaxActiveSessionIds)
            .ToArray();
        await SyncSessionGroupsAsync(sessionIds).ConfigureAwait(false);
        return await registry.HeartbeatAsync(
            new NodeHeartbeatCommand(new NodeId(message.NodeId), sessionIds),
            timeProvider.GetUtcNow(),
            Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task<AgentIdentityMessage> AllocateAgentIdentity(AllocateAgentIdentityMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var identity = await identityRegistry.AllocateAsync(
            new AllocateAgentIdentityCommand(
                new ProjectId(message.ProjectId),
                message.SessionId,
                message.RequestedName,
                message.Role,
                message.Runtime),
            Context.ConnectionAborted).ConfigureAwait(false);
        return new AgentIdentityMessage(
            identity.ProjectId.Value,
            identity.SessionId,
            identity.AgentName,
            identity.Role,
            identity.Runtime,
            identity.AllocatedAtUtc);
    }

    public Task ReleaseAgentIdentity(ReleaseAgentIdentityMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return identityRegistry.ReleaseAsync(message.SessionId, Context.ConnectionAborted);
    }

    public async Task<AgentIdentityMessage?> FindAgentIdentity(FindAgentIdentityMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var identity = await identityRegistry.FindByNameAsync(
            new ProjectId(message.ProjectId),
            message.AgentName,
            Context.ConnectionAborted).ConfigureAwait(false);
        return identity is null
            ? null
            : new AgentIdentityMessage(
                identity.ProjectId.Value,
                identity.SessionId,
                identity.AgentName,
                identity.Role,
                identity.Runtime,
                identity.AllocatedAtUtc);
    }

    /// <summary>
    /// Sends a mail message on behalf of one of the node's agent sessions (SPEC §16.3) and
    /// live-routes the delivered message to every recipient session's group when a node has
    /// that session active.
    /// </summary>
    public async Task<MailDeliveryMessage> SendMail(SendMailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMailSize(message.Subject, message.BodyMarkdown);
        var delivered = await messageService.SendAsync(ToCommand(message), Context.ConnectionAborted).ConfigureAwait(false);
        await RouteLiveAsync(delivered).ConfigureAwait(false);
        return new MailDeliveryMessage(
            delivered.Id,
            delivered.ThreadId,
            delivered.Recipients.Select(recipient => recipient.SessionId).ToArray());
    }

    /// <summary>Fetches the unread inbox for one recipient session, oldest first.</summary>
    public async Task<MailInboxMessage> FetchInbox(FetchMailInboxMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var unread = await messageService.GetUnreadAsync(
            new ProjectId(message.ProjectId),
            message.RecipientSessionId,
            Context.ConnectionAborted).ConfigureAwait(false);
        var limited = unread.Take(Math.Clamp(message.MaxCount, 1, NodeTransportLimits.MaxMailInboxCount));
        return new MailInboxMessage(limited.Select(m => ToTransportForSession(m, message.RecipientSessionId)).ToArray());
    }

    /// <summary>Fetches one thread's messages in creation order.</summary>
    public async Task<MailInboxMessage> FetchThread(FetchMailThreadMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var thread = await messageService.GetThreadAsync(
            new ProjectId(message.ProjectId),
            message.ThreadId,
            Context.ConnectionAborted).ConfigureAwait(false);
        return new MailInboxMessage(thread.Select(m => ToTransportForSession(m, message.RecipientSessionId)).ToArray());
    }

    /// <summary>Marks one delivered message read for one recipient session. Idempotent.</summary>
    public async Task<MailReceiptMessage> MarkMailRead(MarkMailReadMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var delivered = await messageService.MarkReadAsync(
            message.MessageId,
            message.RecipientSessionId,
            Context.ConnectionAborted).ConfigureAwait(false);
        return ToReceipt(delivered, message.RecipientSessionId);
    }

    /// <summary>Acknowledges one delivered message for one recipient session. Requires prior read.</summary>
    public async Task<MailReceiptMessage> AcknowledgeMail(AcknowledgeMailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var delivered = await messageService.AcknowledgeAsync(
            message.MessageId,
            message.RecipientSessionId,
            Context.ConnectionAborted).ConfigureAwait(false);
        return ToReceipt(delivered, message.RecipientSessionId);
    }

    public async Task<RequestClaimMessage?> ClaimNext(ClaimRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var claim = await claimService.ClaimNextAsync(
            new NodeId(message.NodeId),
            ClampLease(message.LeaseSeconds),
            Context.ConnectionAborted).ConfigureAwait(false);
        return claim is null ? null : ToMessage(claim);
    }


    /// <summary>
    /// Replies in an existing thread on behalf of one of the node's agent sessions; recipients
    /// are derived from the thread's participants, excluding the replying session (SPEC §16.3).
    /// </summary>
    public async Task<MailDeliveryMessage> ReplyMail(ReplyMailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        ValidateMailSize("reply", message.BodyMarkdown);
        var delivered = await messageService.ReplyAsync(new ReplyAgentMessageCommand(
            new ProjectId(message.ProjectId),
            message.ThreadId,
            message.SenderSessionId,
            message.BodyMarkdown,
            ParseImportance(message.Importance),
            message.AckRequired), Context.ConnectionAborted).ConfigureAwait(false);
        await RouteLiveAsync(delivered).ConfigureAwait(false);
        return new MailDeliveryMessage(
            delivered.Id,
            delivered.ThreadId,
            delivered.Recipients.Select(recipient => recipient.SessionId).ToArray());
    }
    /// <summary>
    /// Renews a claim's lease and returns the new expiry. The renewal protocol message carries
    /// no project id, so no full claim is reconstructed here.
    /// </summary>

    public async Task<DateTimeOffset?> RenewClaim(ClaimRenewalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            return await claimService.RenewAsync(
                new WorkRequestId(message.RequestId),
                new NodeId(message.NodeId),
                message.ClaimToken,
                ClampLease(message.LeaseSeconds),
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (ClaimRenewalRejectedException)
        {
            return null;
        }
    }

    public async Task<NodeEventAcknowledgementMessage> PublishEvents(NodeEventBatchMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var events = message.Events ?? [];
        if (events.Count > NodeTransportLimits.MaxEventBatchCount)
        {
            throw new HubException(
                $"Event batch of {events.Count} exceeds the limit of {NodeTransportLimits.MaxEventBatchCount}.");
        }

        foreach (var @event in events)
        {
            if (@event?.PayloadJson is null || @event.PayloadJson.Length > NodeTransportLimits.MaxPayloadBytes)
            {
                throw new HubException(
                    $"Event payload exceeds the limit of {NodeTransportLimits.MaxPayloadBytes} bytes.");
            }
        }

        var batch = new EventBatch(events
            .Select(ToDto)
            .ToArray());
        var ack = await eventSink.AppendAsync(batch, Context.ConnectionAborted).ConfigureAwait(false);
        return new NodeEventAcknowledgementMessage(ack.EventIds);
    }

    /// <summary>Acquires a reservation lease for a node session, or reports the conflict.</summary>
    public Task<ReservationOperationResultMessage> AcquireReservation(AcquireReservationMessage message) =>
        InvokeReservationAsync(
            message,
            (service, token) => service.AcquireAsync(
                new AcquireReservationCommand(
                    message.ProjectId,
                    message.RequestId,
                    message.OwnerSessionId,
                    message.Scopes.Select(ToScopeDto).ToArray(),
                    message.Reason),
                token));

    /// <summary>Extends a lease's expiry; fails when the fencing token is stale.</summary>
    public Task<ReservationOperationResultMessage> RenewReservation(ReservationMutationMessage message) =>
        InvokeReservationAsync(
            message,
            (service, token) => service.RenewAsync(
                new RenewReservationCommand(message.LeaseId, message.FencingToken, message.SessionId),
                token));

    /// <summary>Widens a lease with additional scopes; fails when the fencing token is stale.</summary>
    public Task<ReservationOperationResultMessage> ExpandReservation(ExpandReservationMessage message) =>
        InvokeReservationAsync(
            message,
            (service, token) => service.ExpandAsync(
                new ExpandReservationCommand(
                    message.LeaseId,
                    message.FencingToken,
                    message.SessionId,
                    message.Scopes.Select(ToScopeDto).ToArray()),
                token));
    /// <summary>Releases a lease owned by the calling session.</summary>
    public Task<ReservationOperationResultMessage> ReleaseReservation(ReleaseReservationMessage message) =>
        InvokeReservationAsync(
            message,
            (service, token) => service.ReleaseAsync(
                new ReleaseReservationCommand(message.LeaseId, message.SessionId),
                token));

    /// <summary>Moves a lease to a new owner session.</summary>
    public Task<ReservationOperationResultMessage> TransferReservation(TransferReservationMessage message) =>
        InvokeReservationAsync(
            message,
            (service, token) => service.TransferAsync(
                new TransferReservationCommand(message.LeaseId, message.FromSessionId, message.ToSessionId),
                token));

    /// <summary>Authorizes one mutation against a lease immediately before it is applied.</summary>
    public async Task<MutationAuthorizationResultMessage> AuthorizeMutation(MutationAuthorizationMessage message)
    {
        try
        {
            await reservationService.AuthorizeAsync(
                new MutationAuthorizationCommand(
                    message.LeaseId,
                    message.FencingToken,
                    message.SessionId,
                    message.TargetPath,
                    message.Operation),
                Context.ConnectionAborted).ConfigureAwait(false);
            return new MutationAuthorizationResultMessage(Authorized: true, Error: null);
        }
        catch (Exception error) when (error is not HubException and not OperationCanceledException)
        {
            logger.LogWarning(error, "Mutation authorization for lease {LeaseId} failed", message.LeaseId);
            return new MutationAuthorizationResultMessage(false, ToErrorMessage(error));
        }
    }

    /// <summary>Flags a lease as requiring recovery instead of releasing it outright.</summary>
    public Task<ReservationOperationResultMessage> MarkReservationRecovery(MarkRecoveryMessage message) =>
        InvokeReservationAsync(
            message,
            (service, token) => service.MarkRecoveryRequiredAsync(
                new MarkRecoveryRequiredCommand(message.LeaseId, message.Reason),
                token));

    /// <summary>Lists a project's leases with every lease fact the node needs to reason locally.</summary>
    public async Task<ReservationLeaseMessage[]> ListReservations(ListReservationsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var leases = await reservationService.ListAsync(
            message.ProjectId,
            message.IncludeReleased,
            Context.ConnectionAborted).ConfigureAwait(false);
        return leases.Select(ToLeaseMessage).ToArray();
    }

    /// <summary>Records one verification command run. Identifiers are required and bounded.</summary>
    public async Task<VerificationRunMessage> RecordVerification(VerificationRunMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireCorrelation(message.CorrelationId, message.ProjectId, message.RequestId, message.SessionId);
        if (string.IsNullOrWhiteSpace(message.ProfileId) || message.ProfileId.Length > NodeTransportLimits.MaxVerificationIdLength)
        {
            throw new HubException("Verification profile id is required and bounded.");
        }

        if (string.IsNullOrWhiteSpace(message.CommandId) || message.CommandId.Length > NodeTransportLimits.MaxVerificationIdLength)
        {
            throw new HubException("Verification command id is required and bounded.");
        }

        if (message.OutputSummary is { Length: > NodeTransportLimits.MaxVerificationOutputBytes })
        {
            throw new HubException(
                $"Verification output exceeds the limit of {NodeTransportLimits.MaxVerificationOutputBytes} bytes.");
        }

        if (message.OutputArtifactPath is { Length: > NodeTransportLimits.MaxArtifactPathBytes })
        {
            throw new HubException(
                $"Verification artifact path exceeds the limit of {NodeTransportLimits.MaxArtifactPathBytes} bytes.");
        }

        if (!Enum.IsDefined(typeof(VerificationRunStatus), message.Status))
        {
            throw new HubException($"Unknown verification status '{message.Status}'.");
        }

        try
        {
            var recorded = await verificationRuns.RecordAsync(
                new VerificationRunDto(
                    message.Id,
                    message.RequestId,
                    message.ProfileId.Trim(),
                    message.CommandId.Trim(),
                    (VerificationRunStatus)message.Status,
                    message.ExitCode,
                    message.StartedAt,
                    message.CompletedAt,
                    message.OutputSummary,
                    message.OutputArtifactPath,
                    message.Mandatory),
                Context.ConnectionAborted).ConfigureAwait(false);

            return ToMessage(message.CorrelationId, message.ProjectId, message.SessionId, recorded);
        }
        catch (Exception ex) when (ex is not HubException and not OperationCanceledException)
        {
            throw new HubException(ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Evaluates the objective completion gate. Rejection returns the complete missing-requirement list.
    /// </summary>
    public async Task<CompletionGateDecisionMessage> EvaluateCompletion(EvaluateCompletionMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        RequireCorrelation(message.CorrelationId, message.ProjectId, message.RequestId, message.RootSessionId);
        ArgumentNullException.ThrowIfNull(message.Evidence);

        if (message.Evidence.SummaryMarkdown is { Length: > NodeTransportLimits.MaxCompletionSummaryBytes })
        {
            throw new HubException(
                $"Completion summary exceeds the limit of {NodeTransportLimits.MaxCompletionSummaryBytes} bytes.");
        }

        if (message.Evidence.ChangedFiles is { Count: > NodeTransportLimits.MaxChangedFiles })
        {
            throw new HubException(
                $"Changed-file list exceeds the limit of {NodeTransportLimits.MaxChangedFiles}.");
        }

        var findings = message.Evidence.ReviewFindings ?? [];
        if (findings.Count > NodeTransportLimits.MaxReviewFindings)
        {
            throw new HubException(
                $"Review finding list exceeds the limit of {NodeTransportLimits.MaxReviewFindings}.");
        }

        try
        {
            var decision = await completionGate.EvaluateAsync(
                new ProjectId(message.ProjectId),
                new WorkRequestId(message.RequestId),
                message.RootSessionId.Trim(),
                new CompletionEvidence(
                    message.Evidence.SummaryMarkdown,
                    message.Evidence.ChangedFiles,
                    findings.Select(f => new ReviewFinding(f.Id, f.Summary, f.Blocking, f.Resolved, f.UserOverridden)).ToArray(),
                    message.Evidence.VerificationSummary,
                    message.Evidence.RequestBranch,
                    message.Evidence.CheckpointCommitId),
                Context.ConnectionAborted).ConfigureAwait(false);

            return new CompletionGateDecisionMessage(
                message.CorrelationId,
                decision.Accepted,
                decision.MissingRequirements,
                decision.Result is null ? null : ToResultMessage(decision.Result));
        }
        catch (RequestNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
    }


    /// <summary>
    /// Runs one reservation service call and folds typed failures into the transport
    /// result envelope instead of surfacing raw hub exception strings.
    /// </summary>
    private async Task<ReservationOperationResultMessage> InvokeReservationAsync<TMessage>(
        TMessage message,
        Func<IReservationService, CancellationToken, Task<ReservationLeaseDto>> invoke)
    {
        ArgumentNullException.ThrowIfNull(message);
        try
        {
            var lease = await invoke(reservationService, Context.ConnectionAborted).ConfigureAwait(false);
            return new ReservationOperationResultMessage(ToLeaseMessage(lease), Error: null);
        }
        catch (Exception error) when (error is not HubException and not OperationCanceledException)
        {
            logger.LogWarning(error, "Reservation operation for {MessageType} failed", typeof(TMessage).Name);
            return new ReservationOperationResultMessage(Lease: null, ToErrorMessage(error));
        }
    }

    private static ReservationErrorMessage ToErrorMessage(Exception error) => error switch
    {
        ReservationConflictException conflict => new(
            ReservationErrorCodes.Conflict,
            conflict.Message,
            conflict.Conflicts.Select(ToConflictMessage).ToArray()),
        ReservationNotFoundException => new(ReservationErrorCodes.NotFound, error.Message, []),
        InvalidFencingTokenException => new(ReservationErrorCodes.InvalidFencingToken, error.Message, []),
        InvalidLeaseStateException or ReservationStateException => new(ReservationErrorCodes.InvalidState, error.Message, []),
        ReservationValidationException => new(ReservationErrorCodes.Validation, error.Message, []),
        _ => new(ReservationErrorCodes.Unknown, error.Message, []),
    };

    private static ReservationScopeDto ToScopeDto(ReservationScopeMessage scope) => new(scope.Kind, scope.KindName, scope.Path);

    private static ReservationConflictMessage ToConflictMessage(ReservationConflictDto conflict) => new(
        conflict.LeaseId,
        conflict.OwnerSessionId,
        conflict.ScopeKind,
        conflict.ScopeKindName,
        conflict.ScopePath);

    private static ReservationLeaseMessage ToLeaseMessage(ReservationLeaseDto lease) => new(
        lease.LeaseId,
        lease.ProjectId,
        lease.RequestId,
        lease.OwnerSessionId,
        lease.FencingToken,
        lease.State,
        lease.StateName,
        lease.Reason,
        lease.AcquiredAt,
        lease.ExpiresAt,
        lease.ReleasedAt,
        lease.Scopes.Select(scope => new ReservationScopeMessage(scope.Kind, scope.KindName, scope.Path)).ToArray());

    public override async Task OnConnectedAsync()
    {
        // Never log payloads or capability blobs; the connection id is enough for
        // operators to correlate fleet sessions.
        logger.LogInformation("Node transport connection {ConnectionId} established", Context.ConnectionId);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception is null)
        {
            logger.LogInformation("Node transport connection {ConnectionId} closed", Context.ConnectionId);
        }
        else
        {
            logger.LogWarning(
                exception,
                "Node transport connection {ConnectionId} closed with error",
                Context.ConnectionId);
        }
        nodeConnections.Unbind(Context.ConnectionId);

        await base.OnDisconnectedAsync(exception).ConfigureAwait(false);
    }

    private static TimeSpan ClampLease(int leaseSeconds) => TimeSpan.FromSeconds(
        Math.Clamp(leaseSeconds, NodeTransportLimits.MinLeaseSeconds, NodeTransportLimits.MaxLeaseSeconds));

    private static RequestClaimMessage ToMessage(RequestClaimDto claim) => new(
        claim.RequestId,
        claim.ProjectId,
        claim.NodeId,
        claim.ClaimToken,
        claim.ClaimedAt,
        claim.LeaseExpiresAt,
        claim.RepositoryPath,
        claim.DefaultBranch,
        claim.Title,
        claim.Prompt,
        claim.Kind,
        claim.RiskLevel,
        claim.CreateRequestBranch,
        claim.CreateRequestCommit);

    private static NodeEventDto ToDto(NodeEventMessage message) => new(
        message.EventId,
        message.NodeId,
        message.ProjectId,
        message.RequestId,
        message.SessionId,
        message.Sequence,
        message.Type,
        message.OccurredAt,
        message.PayloadJson);

    /// <summary>Keeps the connection joined to one group per active session for live mail routing.</summary>
    private async Task SyncSessionGroupsAsync(IReadOnlyList<string> sessionIds)
    {
        if (Context.Items.TryGetValue(SessionGroupsKey, out var previousValue) && previousValue is HashSet<string> previous)
        {
            foreach (var group in previous)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group).ConfigureAwait(false);
            }
        }

        var current = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
        {
            var group = SessionGroup(sessionId);
            current.Add(group);
            await Groups.AddToGroupAsync(Context.ConnectionId, group).ConfigureAwait(false);
        }

        Context.Items[SessionGroupsKey] = current;
    }

    internal static string SessionGroup(string sessionId) => $"session:{sessionId}";

    /// <summary>
    /// Live routing: pushes a delivered message to the per-session groups of any node that has
    /// a recipient session active. Sessions without a live node fall back to inbox polling.
    /// </summary>
    private async Task RouteLiveAsync(AgentMessageDto delivered)
    {
        foreach (var recipient in delivered.Recipients)
        {
            await Clients.Group(SessionGroup(recipient.SessionId))
                .SendAsync("ReceiveMail", ToTransport(delivered, recipient))
                .ConfigureAwait(false);
        }
    }

    private static void ValidateMailSize(string subject, string bodyMarkdown)
    {
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(bodyMarkdown))
        {
            throw new HubException("Mail subject and body are required.");
        }

        if (subject.Length + bodyMarkdown.Length > NodeTransportLimits.MaxMailPayloadBytes)
        {
            throw new HubException(
                $"Mail message exceeds the limit of {NodeTransportLimits.MaxMailPayloadBytes} bytes.");
        }
    }

    private SendAgentMessageCommand ToCommand(SendMailMessage message) => new(
        new ProjectId(message.ProjectId),
        new WorkRequestId(message.RequestId),
        message.ThreadId,
        message.SenderSessionId,
        message.Recipients,
        message.Subject,
        message.BodyMarkdown,
        ParseImportance(message.Importance),
        message.AckRequired);

    private static MessageImportance ParseImportance(string? importance) =>
        importance?.Trim().ToLowerInvariant() switch
        {
            MailImportance.High => MessageImportance.High,
            null or "" or MailImportance.Normal => MessageImportance.Normal,
            _ => throw new HubException($"Unknown mail importance '{importance}'."),
        };

    /// <summary>
    /// Transport projection addressed to <paramref name="sessionId"/> when that session is one
    /// of the message's recipients (inbox view); otherwise the first recipient (sender's thread
    /// view of its own outbound message).
    /// </summary>
    private static AgentMailMessage ToTransportForSession(AgentMessageDto delivered, string sessionId) =>
        ToTransport(
            delivered,
            delivered.Recipients.FirstOrDefault(r => string.Equals(r.SessionId, sessionId, StringComparison.Ordinal))
            ?? delivered.Recipients[0]);

    internal static AgentMailMessage ToTransport(AgentMessageDto delivered, AgentMessageRecipientDto recipient) => new(
        delivered.Id,
        delivered.ProjectId.Value,
        delivered.RequestId.Value,
        delivered.ThreadId,
        delivered.SenderSessionId,
        delivered.IsFromHuman,
        recipient.SessionId,
        delivered.Subject,
        delivered.BodyMarkdown,
        delivered.Importance == MessageImportance.High ? MailImportance.High : MailImportance.Normal,
        delivered.AcknowledgementRequired,
        delivered.CreatedAtUtc,
        recipient.ReadAtUtc,
        recipient.AcknowledgedAtUtc);

    private static MailReceiptMessage ToReceipt(AgentMessageDto delivered, string recipientSessionId)
    {
        var recipient = delivered.Recipients.Single(r =>
            string.Equals(r.SessionId, recipientSessionId, StringComparison.Ordinal));
        return new MailReceiptMessage(delivered.Id, recipientSessionId, recipient.ReadAtUtc, recipient.AcknowledgedAtUtc);
    }

    private static void RequireCorrelation(Guid correlationId, Guid projectId, Guid requestId, string sessionId)
    {
        if (correlationId == Guid.Empty)
        {
            throw new HubException("Correlation id is required.");
        }

        if (projectId == Guid.Empty)
        {
            throw new HubException("Project id is required.");
        }

        if (requestId == Guid.Empty)
        {
            throw new HubException("Request id is required.");
        }

        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > NodeTransportLimits.MaxSessionIdLength)
        {
            throw new HubException("Session id is required and bounded.");
        }
    }

    private static VerificationRunMessage ToMessage(
        Guid correlationId,
        Guid projectId,
        string sessionId,
        VerificationRunDto run) => new(
        correlationId,
        projectId,
        run.RequestId,
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

    private static RequestResultMessage ToResultMessage(RequestResultDto result) => new(
        result.RequestId,
        result.SummaryMarkdown,
        result.ChangedFiles,
        result.ReviewFindings.Select(f => new ReviewFindingMessage(
            f.Id, f.Summary, f.Blocking, f.Resolved, f.UserOverridden)).ToArray(),
        result.VerificationSummary,
        result.CreatedAt,
        result.RequestBranch,
        result.CheckpointCommitId);

    private const string SessionGroupsKey = "mail:sessionGroups";
}
