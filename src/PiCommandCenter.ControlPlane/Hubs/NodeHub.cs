using Microsoft.AspNetCore.SignalR;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;

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
}

/// <summary>
/// Server-only SignalR hub for the node fleet. Public methods adapt primitive
/// transport messages onto application services; they never trust node-supplied
/// bounds (lease seconds, batch sizes, payload sizes) verbatim.
/// </summary>
public sealed class NodeHub(
    INodeRegistry registry,
    INodeEventSink eventSink,
    IRequestClaimService claimService,
    TimeProvider timeProvider,
    ILogger<NodeHub> logger) : Hub
{
    public async Task<NodeDto> Register(NodeRegistrationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return await registry.RegisterAsync(
            new RegisterNodeCommand(
                new NodeId(message.NodeId),
                message.DisplayName,
                message.AgentVersion,
                message.CapabilitiesJson),
            timeProvider.GetUtcNow(),
            Context.ConnectionAborted).ConfigureAwait(false);
    }

    public async Task<NodeDto> Heartbeat(NodeHeartbeatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var sessionIds = (message.ActiveSessionIds ?? [])
            .Take(NodeTransportLimits.MaxActiveSessionIds)
            .ToArray();
        return await registry.HeartbeatAsync(
            new NodeHeartbeatCommand(new NodeId(message.NodeId), sessionIds),
            timeProvider.GetUtcNow(),
            Context.ConnectionAborted).ConfigureAwait(false);
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
    /// Renews a claim's lease and returns the new expiry. The renewal protocol message carries
    /// no project id, so no full claim is reconstructed here.
    /// </summary>
    public async Task<DateTimeOffset> RenewClaim(ClaimRenewalMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return await claimService.RenewAsync(
            new WorkRequestId(message.RequestId),
            new NodeId(message.NodeId),
            message.ClaimToken,
            ClampLease(message.LeaseSeconds),
            Context.ConnectionAborted).ConfigureAwait(false);
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
        claim.LeaseExpiresAt);

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
}
