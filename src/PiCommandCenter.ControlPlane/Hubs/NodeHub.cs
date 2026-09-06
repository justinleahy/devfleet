using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.ControlPlane.RuntimeRouting;
using PiCommandCenter.Domain.Projects;

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
    public const int MaxEventIdLength = AssignmentOperationLimits.MaxEventIdLength;
    public const int MaxPayloadBytes = 256 * 1024;
    public const int MaxActiveSessionIds = 200;
    public const int MaxMailPayloadBytes = 64 * 1024;
    public const int MaxMailInboxCount = 200;
    public const int MaxSessionIdLength = 128;
    public const int MaxVerificationIdLength = 128;
    public const int MaxVerificationFingerprintLength = VerificationRun.MaxFingerprintLength;
    public const int MaxVerificationPolicyRevisionLength = VerificationRun.MaxPolicyRevisionLength;
    public const int MaxVerificationOutputBytes = 16_384;
    public const int MaxArtifactPathBytes = 1024;
    public const int MaxVerificationReplayRuns = VerificationReplayLimits.MaxRuns;
    public const int MaxCompletionSummaryBytes = 64 * 1024;
    public const int MaxChangedFiles = 500;
    public const int MaxReviewFindings = 200;
    public const int MaxRecoveryClaimTokenLength = 128;
    public const int MaxRecoveryStageLength = 128;
    public const int MaxRecoveryReasonCodes = 16;
    public const int MaxRecoveryReasonCodeLength = 64;
    public const int MaxRecoveryProcessIdentities = 32;
    public const int MaxRecoveryReservationDispositions = 32;
    public const int MaxRecoveryInterruptedIndicators = 16;
    public const int MaxRecoverySummaryLength = 256;
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
    IExecutionAssignmentService executionAssignmentService,
    IVerificationRunStore verificationRuns,
    IAssignmentTerminalizationService terminalization,
    IAssignmentOperationAuthorizer assignmentAuthorizer,
    IRecoveryAttemptCoordinator recoveryAttempts,
    IRecoveryAttemptDispatcher recoveryDispatcher,
    NodeConnectionDirectory nodeConnections,
    TimeProvider timeProvider,
    ILogger<NodeHub> logger) : Hub

{
    public async Task<NodeDto> Register(NodeRegistrationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireAuthenticatedNodeId();
        RequireMatchingNodeId(message.NodeId, nodeId);
        var registered = await registry.RegisterAsync(
            new RegisterNodeCommand(
                new NodeId(nodeId),
                message.DisplayName,
                message.AgentVersion,
                message.CapabilitiesJson),
            timeProvider.GetUtcNow(),
            Context.ConnectionAborted).ConfigureAwait(false);
        nodeConnections.Bind(nodeId, Context.ConnectionId);
        await recoveryDispatcher.DispatchForNodeAsync(new NodeId(nodeId), Context.ConnectionAborted)
            .ConfigureAwait(false);
        return registered;

    }

    public async Task<NodeDto> Heartbeat(NodeHeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId(message.NodeId);
        var executionStatus = ToExecutionStatusDto(message.ExecutionStatus);
        var requestedSessionIds = (message.ActiveSessionIds ?? [])
            .Take(NodeTransportLimits.MaxActiveSessionIds)
            .ToArray();
        var sessionIds = await FilterHeartbeatSessionsAsync(nodeId, requestedSessionIds).ConfigureAwait(false);
        await SyncSessionGroupsAsync(sessionIds).ConfigureAwait(false);
        return await registry.HeartbeatAsync(
            new NodeHeartbeatCommand(
                new NodeId(nodeId),
                sessionIds,
                message.Resources is null
                    ? null
                    : new NodeResourceSnapshotDto(
                        message.Resources.ObservedAt,
                        message.Resources.CpuUsagePercent,
                        message.Resources.MemoryUsedBytes,
                        message.Resources.MemoryTotalBytes,
                        message.Resources.DiskUsedBytes,
                        message.Resources.DiskTotalBytes,
                        message.Resources.LoadAverageOneMinute,
                        message.Resources.UptimeSeconds),
                executionStatus),
            timeProvider.GetUtcNow(),
            Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task<AgentIdentityMessage> AllocateAgentIdentity(AllocateAgentIdentityMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken).ConfigureAwait(false);
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

    public async Task ReleaseAgentIdentity(ReleaseAgentIdentityMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.SessionId).ConfigureAwait(false);
        await identityRegistry.ReleaseAsync(message.SessionId, Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task<AgentIdentityMessage?> FindAgentIdentity(FindAgentIdentityMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken).ConfigureAwait(false);
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
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.SenderSessionId).ConfigureAwait(false);
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
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.RecipientSessionId).ConfigureAwait(false);
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
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.RecipientSessionId).ConfigureAwait(false);
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
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.RecipientSessionId).ConfigureAwait(false);
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
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.RecipientSessionId).ConfigureAwait(false);
        var delivered = await messageService.AcknowledgeAsync(
            message.MessageId,
            message.RecipientSessionId,
            Context.ConnectionAborted).ConfigureAwait(false);
        return ToReceipt(delivered, message.RecipientSessionId);
    }

    public async Task<ReconcileAssignmentsResultMessage> ReconcileAssignments(
        ReconcileAssignmentsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId(message.NodeId);
        var inventory = (message.Assignments ?? [])
            .Select(ToDto)
            .ToArray();
        var results = await executionAssignmentService.ReconcileAsync(
            new NodeId(nodeId),
            inventory,
            ClampLease(message.LeaseSeconds),
            Context.ConnectionAborted).ConfigureAwait(false);
        Context.Items[AssignmentInventoryReconciledKey] = true;
        return new ReconcileAssignmentsResultMessage(results
            .Select(result => new AssignmentReconciliationResultMessage(
                result.RequestId.Value,
                result.Disposition,
                result.Assignment is null ? null : ToMessage(result.Assignment)))
            .ToArray());
    }

    public async Task<ExecutionAssignmentMessage?> ClaimNext(ClaimRequestMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId(message.NodeId);
        if (!Context.Items.TryGetValue(AssignmentInventoryReconciledKey, out var reconciled)
            || reconciled is not true)
        {
            throw new HubException(
                "Assignment inventory reconciliation must succeed before this connection can claim work.");
        }

        var assignment = await executionAssignmentService.ClaimNextAsync(
            new NodeId(nodeId),
            ClampLease(message.LeaseSeconds),
            Context.ConnectionAborted).ConfigureAwait(false);
        return assignment is null ? null : ToMessage(assignment);
    }


    /// <summary>
    /// Replies in an existing thread on behalf of one of the node's agent sessions; recipients
    /// are derived from the thread's participants, excluding the replying session (SPEC §16.3).
    /// </summary>
    public async Task<MailDeliveryMessage> ReplyMail(ReplyMailMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.SenderSessionId).ConfigureAwait(false);
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
    /// Renews an execution assignment's lease and returns the new expiry. The renewal protocol
    /// message carries no project id, so no full assignment is reconstructed here.
    /// </summary>

    public async Task<DateTimeOffset?> RenewClaim(ClaimRenewalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId(message.NodeId);
        try
        {
            return await executionAssignmentService.RenewAsync(
                new WorkRequestId(message.RequestId),
                new NodeId(nodeId),
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
        var nodeId = RequireRegisteredNodeId();
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

            RequireMatchingNodeId(@event.NodeId, nodeId);
        }

        await RequireHistoricalEventsAsync(nodeId, events).ConfigureAwait(false);

        var batch = new EventBatch(events
            .Select(@event => ToDto(@event, nodeId))
            .ToArray());
        var ack = await eventSink.AppendAsync(batch, Context.ConnectionAborted).ConfigureAwait(false);
        return new NodeEventAcknowledgementMessage(ack.EventIds);
    }

    /// <summary>Acquires a reservation lease for a node session, or reports the conflict.</summary>
    public Task<ReservationOperationResultMessage> AcquireReservation(AcquireReservationMessage message) =>
        InvokeReservationAsync(
            message,
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            message.OwnerSessionId,
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
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            message.SessionId,
            (service, token) => service.RenewAsync(
                new RenewReservationCommand(message.LeaseId, message.FencingToken, message.SessionId),
                token));

    /// <summary>Widens a lease with additional scopes; fails when the fencing token is stale.</summary>
    public Task<ReservationOperationResultMessage> ExpandReservation(ExpandReservationMessage message) =>
        InvokeReservationAsync(
            message,
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            message.SessionId,
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
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            message.SessionId,
            (service, token) => service.ReleaseAsync(
                new ReleaseReservationCommand(message.LeaseId, message.SessionId),
                token));

    /// <summary>Moves a lease to a new owner session.</summary>
    public async Task<ReservationOperationResultMessage> TransferReservation(TransferReservationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.ToSessionId).ConfigureAwait(false);
        return await InvokeReservationAsync(
            message,
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            message.FromSessionId,
            (service, token) => service.TransferAsync(
                new TransferReservationCommand(message.LeaseId, message.FromSessionId, message.ToSessionId),
                token)).ConfigureAwait(false);
    }

    /// <summary>Authorizes one mutation against a lease immediately before it is applied.</summary>
    public async Task<MutationAuthorizationResultMessage> AuthorizeMutation(MutationAuthorizationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.SessionId).ConfigureAwait(false);
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
            message.ProjectId,
            message.RequestId,
            message.ClaimToken,
            sessionId: null,
            (service, token) => service.MarkRecoveryRequiredAsync(
                new MarkRecoveryRequiredCommand(message.LeaseId, message.Reason),
                token));

    /// <summary>Lists a project's leases with every lease fact the node needs to reason locally.</summary>
    public async Task<ReservationLeaseMessage[]> ListReservations(ListReservationsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken).ConfigureAwait(false);
        var leases = await reservationService.ListAsync(
            message.ProjectId,
            message.IncludeReleased,
            Context.ConnectionAborted).ConfigureAwait(false);
        return leases.Select(ToLeaseMessage).ToArray();
    }

    /// <summary>Records one verification command run. Identifiers are required and bounded.</summary>
    public async Task<VerificationRunResultMessage> RecordVerification(VerificationRunMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        RequireCorrelation(message.CorrelationId, message.ProjectId, message.RequestId, message.SessionId);
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.SessionId).ConfigureAwait(false);
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

        if (string.IsNullOrWhiteSpace(message.Fingerprint)
            || message.Fingerprint.Length > NodeTransportLimits.MaxVerificationFingerprintLength)
        {
            throw new HubException("Verification fingerprint is required and bounded.");
        }

        if (string.IsNullOrWhiteSpace(message.PolicyRevision)
            || message.PolicyRevision.Length > NodeTransportLimits.MaxVerificationPolicyRevisionLength)
        {
            throw new HubException("Verification policy revision is required and bounded.");
        }

        if (!Enum.IsDefined(typeof(VerificationRunKind), message.RunKind))
        {
            throw new HubException($"Unknown verification run kind '{message.RunKind}'.");
        }

        if (message.AttemptId == Guid.Empty)
        {
            throw new HubException("Verification attempt id is required.");
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
                    message.Mandatory,
                    message.Fingerprint.Trim(),
                    message.PolicyRevision.Trim(),
                    (VerificationRunKind)message.RunKind,
                    message.AttemptId),
                Context.ConnectionAborted).ConfigureAwait(false);

            return ToMessage(message.CorrelationId, message.ProjectId, message.SessionId, recorded);
        }
        catch (Exception ex) when (ex is not HubException and not OperationCanceledException)
        {
            throw new HubException(ex.InnerException?.Message ?? ex.Message);
        }
    }

    /// <summary>
    /// Bounded newest-first replay of persisted final and intermediate runs for the
    /// authenticated assignment and session. Artifact paths are never returned.
    /// </summary>
    public async Task<VerificationRunReplayListMessage> ListVerificationRuns(ListVerificationRunsMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        RequireCorrelation(message.CorrelationId, message.ProjectId, message.RequestId, message.SessionId);
        await RequireActiveAsync(
            nodeId,
            message.RequestId,
            message.ProjectId,
            message.ClaimToken,
            message.SessionId).ConfigureAwait(false);

        var runs = await verificationRuns.ListRecentAsync(
            new WorkRequestId(message.RequestId),
            NodeTransportLimits.MaxVerificationReplayRuns,
            Context.ConnectionAborted).ConfigureAwait(false);

        return new VerificationRunReplayListMessage(
            message.CorrelationId,
            message.ProjectId,
            message.RequestId,
            message.SessionId,
            NodeTransportLimits.MaxVerificationReplayRuns,
            [.. runs.Select(run => ToReplayMessage(
                message.CorrelationId, message.ProjectId, message.SessionId, run))]);
    }

    /// <summary>
    /// First terminalization step: validates the fence and intent payload, then closes
    /// admission by moving the assignment into Finalizing (Complete/Fail) or Cancelling
    /// (Cancel). Complete runs the objective completion preflight before the state move.
    /// Rejection returns the complete missing-requirement list and changes nothing.
    /// </summary>
    public async Task<CompletionGateDecisionMessage> BeginTerminalization(BeginTerminalizationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        RequireTerminalizationCorrelation(
            message.CorrelationId,
            message.ProjectId,
            message.RequestId,
            message.RootSessionId,
            message.Intent);
        RequireEvidenceBounds(message.Evidence);

        try
        {
            var decision = await terminalization.BeginAsync(
                new NodeId(nodeId),
                new ProjectId(message.ProjectId),
                new WorkRequestId(message.RequestId),
                message.ClaimToken,
                message.RootSessionId?.Trim(),
                message.Intent,
                ToEvidence(message.Evidence),
                message.Reason,
                Context.ConnectionAborted).ConfigureAwait(false);

            return ToDecisionMessage(message.CorrelationId, decision);
        }
        catch (RequestNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (AssignmentAuthorizationException error)
        {
            throw ToHubException(error);
        }
    }

    /// <summary>
    /// Second terminalization step: requires the matching Finalizing/Cancelling state and an
    /// exact all-zero/true quiescence proof, then atomically persists the result (Complete
    /// only), the request terminal status, and the assignment terminal status. Exact retries
    /// return the persisted outcome without reopening.
    /// </summary>
    public async Task<CompletionGateDecisionMessage> ConfirmTerminalization(ConfirmTerminalizationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        RequireTerminalizationCorrelation(
            message.CorrelationId,
            message.ProjectId,
            message.RequestId,
            message.RootSessionId,
            message.Intent);
        RequireEvidenceBounds(message.Evidence);
        ArgumentNullException.ThrowIfNull(message.Proof);

        try
        {
            var decision = await terminalization.ConfirmAsync(
                new NodeId(nodeId),
                new ProjectId(message.ProjectId),
                new WorkRequestId(message.RequestId),
                message.ClaimToken,
                message.RootSessionId?.Trim(),
                message.Intent,
                ToEvidence(message.Evidence),
                message.Reason,
                new AssignmentQuiescenceProof(
                    message.Proof.AdmissionClosed,
                    message.Proof.ActiveChildren,
                    message.Proof.ActiveOperations,
                    message.Proof.ActiveProcesses,
                    message.Proof.PendingEvents,
                    message.Proof.ActiveReservations,
                    message.Proof.RepositoryInspected,
                    message.Proof.ObservedAt),
                Context.ConnectionAborted).ConfigureAwait(false);

            return ToDecisionMessage(message.CorrelationId, decision);
        }
        catch (RequestNotFoundException ex)
        {
            throw new HubException(ex.Message);
        }
        catch (AssignmentAuthorizationException error)
        {
            throw ToHubException(error);
        }
    }

    /// <summary>
    /// Node-attested recovery progress. Identity comes from the authenticated
    /// connection, never from the payload.
    /// </summary>
    public async Task ReportRecoveryProgress(AssignmentRecoveryProgressMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = GetAuthenticatedNodeId();
        RequireRecoveryProgressBounds(message);
        await recoveryAttempts.AcceptProgressAsync(nodeId, message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Node-attested recovery proof. Identity comes from the authenticated
    /// connection, never from the payload.
    /// </summary>
    public async Task<RecoveryProofDecisionMessage> ReportRecoveryProof(AssignmentRecoveryProofMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = GetAuthenticatedNodeId();
        RequireRecoveryProofBounds(message);
        return await recoveryAttempts.AcceptProofAsync(nodeId, message, Context.ConnectionAborted)
            .ConfigureAwait(false);
    }

    private static void RequireEvidenceBounds(CompletionEvidenceMessage? evidence)
    {
        if (evidence is null)
        {
            return;
        }

        if (evidence.SummaryMarkdown is { Length: > NodeTransportLimits.MaxCompletionSummaryBytes })
        {
            throw new HubException(
                $"Completion summary exceeds the limit of {NodeTransportLimits.MaxCompletionSummaryBytes} bytes.");
        }

        if (evidence.ChangedFiles is { Count: > NodeTransportLimits.MaxChangedFiles })
        {
            throw new HubException(
                $"Changed-file list exceeds the limit of {NodeTransportLimits.MaxChangedFiles}.");
        }

        if ((evidence.ReviewFindings ?? []).Count > NodeTransportLimits.MaxReviewFindings)
        {
            throw new HubException(
                $"Review finding list exceeds the limit of {NodeTransportLimits.MaxReviewFindings}.");
        }

        if (evidence.VerificationFingerprint is { Length: > NodeTransportLimits.MaxVerificationFingerprintLength })
        {
            throw new HubException(
                $"Verification fingerprint exceeds the limit of {NodeTransportLimits.MaxVerificationFingerprintLength} characters.");
        }

        if (evidence.VerificationPolicyRevision is { Length: > NodeTransportLimits.MaxVerificationPolicyRevisionLength })
        {
            throw new HubException(
                $"Verification policy revision exceeds the limit of {NodeTransportLimits.MaxVerificationPolicyRevisionLength} characters.");
        }
    }

    private static CompletionEvidence? ToEvidence(CompletionEvidenceMessage? evidence) =>
        evidence is null
            ? null
            : new CompletionEvidence(
                evidence.SummaryMarkdown,
                evidence.ChangedFiles,
                (evidence.ReviewFindings ?? [])
                    .Select(f => new ReviewFinding(f.Id, f.Summary, f.Blocking, f.Resolved, f.UserOverridden))
                    .ToArray(),
                evidence.VerificationSummary,
                evidence.RequestBranch,
                evidence.CheckpointCommitId,
                evidence.VerificationFingerprint,
                evidence.VerificationPolicyRevision);

    private static CompletionGateDecisionMessage ToDecisionMessage(
        Guid correlationId,
        CompletionGateDecision decision) =>
        new(
            correlationId,
            decision.Accepted,
            decision.MissingRequirements,
            decision.Result is null ? null : ToResultMessage(decision.Result));


    /// <summary>
    /// Runs one reservation service call and folds typed failures into the transport
    /// result envelope instead of surfacing raw hub exception strings.
    /// </summary>
    private async Task<ReservationOperationResultMessage> InvokeReservationAsync<TMessage>(
        TMessage message,
        Guid projectId,
        Guid requestId,
        string claimToken,
        string? sessionId,
        Func<IReservationService, CancellationToken, Task<ReservationLeaseDto>> invoke)
    {
        ArgumentNullException.ThrowIfNull(message);
        var nodeId = RequireRegisteredNodeId();
        await RequireActiveAsync(nodeId, requestId, projectId, claimToken, sessionId).ConfigureAwait(false);
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

    private Guid RequireAuthenticatedNodeId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!Guid.TryParse(claim, out var nodeId) || nodeId == Guid.Empty)
        {
            throw new HubException("Authenticated node identity is missing or invalid.");
        }

        return nodeId;
    }

    private NodeId GetAuthenticatedNodeId() => new(RequireAuthenticatedNodeId());

    private Guid RequireRegisteredNodeId()
    {
        var nodeId = RequireAuthenticatedNodeId();
        if (!nodeConnections.IsBound(nodeId, Context.ConnectionId))
        {
            throw new HubException("Node connection is not registered.");
        }

        return nodeId;
    }

    private Guid RequireRegisteredNodeId(Guid assertedNodeId)
    {
        var nodeId = RequireRegisteredNodeId();
        RequireMatchingNodeId(assertedNodeId, nodeId);
        return nodeId;
    }

    private static void RequireMatchingNodeId(Guid assertedNodeId, Guid authenticatedNodeId)
    {
        if (assertedNodeId != authenticatedNodeId)
        {
            throw new HubException("Message node identity does not match the authenticated node.");
        }
    }

    private async Task RequireActiveAsync(
        Guid nodeId,
        Guid requestId,
        Guid projectId,
        string claimToken,
        string? sessionId = null)
    {
        try
        {
            await assignmentAuthorizer.RequireActiveAsync(
                new NodeId(nodeId),
                new WorkRequestId(requestId),
                new ProjectId(projectId),
                claimToken,
                sessionId,
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (AssignmentAuthorizationException error)
        {
            throw ToHubException(error);
        }
    }

    private async Task RequireHistoricalEventsAsync(
        Guid nodeId,
        IReadOnlyList<NodeEventMessage> events)
    {
        var requests = new AssignmentEventAuthorizationRequest[events.Count];
        for (var index = 0; index < events.Count; index++)
        {
            var @event = events[index];
            if (@event.RequestId is not Guid requestId)
            {
                throw new HubException(
                    $"Assignment authorization denied ({AssignmentAuthorizationCodes.InvalidInput}).");
            }

            requests[index] = new AssignmentEventAuthorizationRequest(
                new WorkRequestId(requestId),
                new ProjectId(@event.ProjectId),
                @event.ClaimToken,
                @event.SessionId,
                @event.EventId,
                @event.Type);
        }

        try
        {
            await assignmentAuthorizer.RequireHistoricalEventsAsync(
                new NodeId(nodeId),
                requests,
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (AssignmentAuthorizationException error)
        {
            throw ToHubException(error);
        }
    }

    private async Task<IReadOnlyList<string>> FilterHeartbeatSessionsAsync(
        Guid nodeId,
        IReadOnlyCollection<string> sessionIds)
    {
        try
        {
            return await assignmentAuthorizer.FilterHeartbeatSessionsAsync(
                new NodeId(nodeId),
                sessionIds,
                Context.ConnectionAborted).ConfigureAwait(false);
        }
        catch (AssignmentAuthorizationException error)
        {
            throw ToHubException(error);
        }
    }

    private static HubException ToHubException(AssignmentAuthorizationException error) =>
        new($"Assignment authorization denied ({error.Code}).");

    private static TimeSpan ClampLease(int leaseSeconds) => TimeSpan.FromSeconds(
        Math.Clamp(leaseSeconds, NodeTransportLimits.MinLeaseSeconds, NodeTransportLimits.MaxLeaseSeconds));

    private static NodeExecutionStatusDto? ToExecutionStatusDto(NodeExecutionStatusMessage? status)
    {
        if (status is null)
        {
            return null;
        }

        if (status.ActiveAssignmentIds is null || status.Routes is null)
        {
            throw new HubException("Execution status assignments and routes are required.");
        }

        return new NodeExecutionStatusDto(
            status.ObservedAt,
            status.AvailableRequestSlots,
            status.ActiveAssignmentIds,
            status.RoutingRevision,
            status.Routes.Select(route => new RuntimeRouteReadinessDto(
                route.Role,
                route.CanonicalModel,
                route.Readiness,
                route.EvidenceSource,
                route.ObservedAt,
                route.RoutingRevision)).ToArray(),
            ToVerificationPolicyDto(status.VerificationPolicy));
    }

    private static VerificationPolicyCatalogMessage? ToVerificationPolicyDto(
        VerificationPolicyCatalogMessage? policy)
    {
        if (policy is null)
        {
            return null;
        }

        return new VerificationPolicyCatalogMessage(
            policy.ObservedAt,
            policy.BaselineAvailable,
            policy.BaselineVersion,
            (policy.Profiles ?? []).Select(profile => new VerificationPolicyProfileMessage(
                profile.Id,
                profile.Revision,
                profile.DisplayLabel,
                (profile.Commands ?? []).Select(command => new VerificationPolicyCommandMessage(
                    command.Id,
                    command.DisplayLabel,
                    command.WorkingDirectoryLabel,
                    command.Mandatory,
                    command.TimeoutSeconds)).ToArray())).ToArray());
    }

    private static ExecutionAssignmentInventoryDto ToDto(
        ExecutionAssignmentInventoryItemMessage item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(item.Assignment);
        var assignment = item.Assignment;
        return new ExecutionAssignmentInventoryDto(
            new WorkRequestId(assignment.RequestId),
            new ProjectId(assignment.ProjectId),
            new WorkspaceBindingId(assignment.WorkspaceBindingId),
            new NodeId(assignment.NodeIdSnapshot),
            assignment.CanonicalRepositoryPathSnapshot,
            assignment.DefaultBranchSnapshot,
            assignment.BindingValidationRevisionSnapshot,
            Enum.TryParse<ExecutionAssignmentState>(
                assignment.State,
                ignoreCase: false,
                out var state)
                && Enum.IsDefined(state)
                    ? state
                    : null,
            assignment.ClaimToken,
            assignment.AssignedAt,
            item.SupervisorState,
            item.RepositoryKnown,
            item.PendingEventCount);
    }

    private static ExecutionAssignmentMessage ToMessage(ExecutionAssignmentDto assignment) => new(
        assignment.RequestId.Value,
        assignment.ProjectId.Value,
        assignment.WorkspaceBindingId.Value,
        assignment.NodeIdSnapshot.Value,
        assignment.CanonicalRepositoryPathSnapshot,
        assignment.DefaultBranchSnapshot,
        assignment.BindingValidationRevisionSnapshot,
        assignment.State.ToString(),
        assignment.ClaimToken,
        assignment.AssignedAt,
        assignment.LeaseExpiresAt,
        assignment.RequestTitle,
        assignment.RequestPrompt,
        assignment.RequestKind.ToString(),
        assignment.RequestRiskLevel.ToString(),
        assignment.CreateRequestBranch,
        assignment.CreateRequestCommit,
        assignment.VerificationPolicyRevision,
        assignment.BaselineVersion,
        assignment.TrustedVerificationProfileId,
        assignment.TrustedVerificationProfileRevision,
        assignment.MandatoryCommandIdsJson);

    private static NodeEventDto ToDto(NodeEventMessage message, Guid nodeId) => new(
        message.EventId,
        nodeId,
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

    private static void RequireTerminalizationCorrelation(
        Guid correlationId,
        Guid projectId,
        Guid requestId,
        string? rootSessionId,
        TerminalizationIntent intent)
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

        if (intent != TerminalizationIntent.Cancel
            && string.IsNullOrWhiteSpace(rootSessionId))
        {
            throw new HubException("Root session id is required outside pre-session cancellation.");
        }

        if (rootSessionId?.Length > NodeTransportLimits.MaxSessionIdLength)
        {
            throw new HubException("Root session id is too long.");
        }
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

    private static void RequireRecoveryProgressBounds(AssignmentRecoveryProgressMessage message)
    {
        RequireRecoveryCorrelation(message.RecoveryId, message.Attempt, message.ProjectId, message.RequestId, message.ClaimToken, message.ObservedAt);
        if (message.Stage is { Length: > NodeTransportLimits.MaxRecoveryStageLength })
        {
            throw new HubException(
                $"Recovery stage exceeds the limit of {NodeTransportLimits.MaxRecoveryStageLength} characters.");
        }

        RequireKnownCount(message.Children, "children");
        RequireKnownCount(message.Operations, "operations");
        RequireKnownCount(message.Processes, "processes");
        RequireKnownCount(message.PendingEvents, "pending events");
        RequireKnownCount(message.Reservations, "reservations");
        RequireReasonCodes(message.ReasonCodes);
    }

    private static void RequireRecoveryProofBounds(AssignmentRecoveryProofMessage message)
    {
        RequireRecoveryCorrelation(message.RecoveryId, message.Attempt, message.ProjectId, message.RequestId, message.ClaimToken, message.ObservedAt);
        RequireKnownCount(message.Children, "children");
        RequireKnownCount(message.Operations, "operations");
        RequireKnownCount(message.Processes, "processes");
        RequireKnownCount(message.PendingEvents, "pending events");
        RequireKnownCount(message.Reservations, "reservations");

        if (message.EventAcknowledgementUnknownReasonCode is { Length: > NodeTransportLimits.MaxRecoveryReasonCodeLength })
        {
            throw new HubException(
                $"Recovery acknowledgement reason exceeds the limit of {NodeTransportLimits.MaxRecoveryReasonCodeLength} characters.");
        }

        var identities = message.ProcessIdentities ?? [];
        if (identities.Count > NodeTransportLimits.MaxRecoveryProcessIdentities)
        {
            throw new HubException(
                $"Recovery process identity list exceeds the limit of {NodeTransportLimits.MaxRecoveryProcessIdentities}.");
        }

        foreach (var identity in identities)
        {
            if (identity is null)
            {
                throw new HubException("Recovery process identity is required.");
            }

            if (identity.GroupOrScopeId is { Length: > NodeTransportLimits.MaxRecoverySummaryLength })
            {
                throw new HubException(
                    $"Recovery process group exceeds the limit of {NodeTransportLimits.MaxRecoverySummaryLength} characters.");
            }
        }

        var dispositions = message.ReservationDispositions ?? [];
        if (dispositions.Count > NodeTransportLimits.MaxRecoveryReservationDispositions)
        {
            throw new HubException(
                $"Recovery reservation disposition list exceeds the limit of {NodeTransportLimits.MaxRecoveryReservationDispositions}.");
        }

        foreach (var disposition in dispositions)
        {
            if (disposition is null
                || string.IsNullOrWhiteSpace(disposition.Disposition)
                || disposition.Disposition.Length > NodeTransportLimits.MaxRecoveryReasonCodeLength)
            {
                throw new HubException("Recovery reservation disposition is required and bounded.");
            }

            if (disposition.ReasonCode is { Length: > NodeTransportLimits.MaxRecoveryReasonCodeLength })
            {
                throw new HubException(
                    $"Recovery reservation reason exceeds the limit of {NodeTransportLimits.MaxRecoveryReasonCodeLength} characters.");
            }
        }

        if (message.Repository is { } repository)
        {
            RequireBoundedSummary(repository.Head, "repository head");
            RequireBoundedSummary(repository.Branch, "repository branch");
            RequireBoundedSummary(repository.IndexSummary, "repository index summary");
            RequireBoundedSummary(repository.WorktreeSummary, "repository worktree summary");
            RequireKnownCount(repository.UntrackedCount, "untracked files");
            var indicators = repository.InterruptedOperationIndicators ?? [];
            if (indicators.Count > NodeTransportLimits.MaxRecoveryInterruptedIndicators)
            {
                throw new HubException(
                    $"Recovery interrupted-operation list exceeds the limit of {NodeTransportLimits.MaxRecoveryInterruptedIndicators}.");
            }

            foreach (var indicator in indicators)
            {
                if (string.IsNullOrWhiteSpace(indicator)
                    || indicator.Length > NodeTransportLimits.MaxRecoveryReasonCodeLength)
                {
                    throw new HubException("Recovery interrupted-operation indicator is required and bounded.");
                }
            }
        }
    }

    private static void RequireRecoveryCorrelation(
        Guid recoveryId,
        int attempt,
        Guid projectId,
        Guid requestId,
        string claimToken,
        DateTimeOffset observedAt)
    {
        if (recoveryId == Guid.Empty)
        {
            throw new HubException("Recovery id is required.");
        }

        if (attempt < 1)
        {
            throw new HubException("Recovery attempt is required.");
        }

        if (projectId == Guid.Empty)
        {
            throw new HubException("Project id is required.");
        }

        if (requestId == Guid.Empty)
        {
            throw new HubException("Request id is required.");
        }

        if (string.IsNullOrWhiteSpace(claimToken)
            || claimToken.Length > NodeTransportLimits.MaxRecoveryClaimTokenLength)
        {
            throw new HubException("Recovery claim token is required and bounded.");
        }

        if (observedAt == default)
        {
            throw new HubException("Recovery observation time is required.");
        }
    }

    private static void RequireKnownCount(RecoveryKnownCountMessage? count, string name)
    {
        if (count is null || !count.IsValid)
        {
            throw new HubException($"Recovery {name} inventory is required and must be known or explicitly unknown.");
        }

        if (count.UnknownReasonCode is { Length: > NodeTransportLimits.MaxRecoveryReasonCodeLength })
        {
            throw new HubException(
                $"Recovery {name} unknown reason exceeds the limit of {NodeTransportLimits.MaxRecoveryReasonCodeLength} characters.");
        }
    }

    private static void RequireReasonCodes(IReadOnlyList<string>? reasonCodes)
    {
        var codes = reasonCodes ?? [];
        if (codes.Count > NodeTransportLimits.MaxRecoveryReasonCodes)
        {
            throw new HubException(
                $"Recovery reason-code list exceeds the limit of {NodeTransportLimits.MaxRecoveryReasonCodes}.");
        }

        foreach (var code in codes)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > NodeTransportLimits.MaxRecoveryReasonCodeLength)
            {
                throw new HubException("Recovery reason code is required and bounded.");
            }
        }
    }

    private static void RequireBoundedSummary(string? value, string name)
    {
        if (value is { Length: > NodeTransportLimits.MaxRecoverySummaryLength })
        {
            throw new HubException(
                $"Recovery {name} exceeds the limit of {NodeTransportLimits.MaxRecoverySummaryLength} characters.");
        }
    }

    private static VerificationRunResultMessage ToMessage(
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
        run.Mandatory,
        run.Fingerprint,
        run.PolicyRevision,
        (int)run.RunKind,
        run.RunKind.ToString(),
        run.AttemptId);

    private static VerificationRunReplayMessage ToReplayMessage(
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
        run.Mandatory,
        run.Fingerprint,
        run.PolicyRevision,
        (int)run.RunKind,
        run.RunKind.ToString(),
        run.AttemptId);

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

    private const string AssignmentInventoryReconciledKey = "assignments:reconciled";
    private const string SessionGroupsKey = "mail:sessionGroups";
}
