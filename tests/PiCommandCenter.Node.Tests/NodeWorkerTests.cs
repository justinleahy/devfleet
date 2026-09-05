using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Tests;

/// <summary>Deterministic in-memory <see cref="INodeHubOps"/> fake.</summary>
internal sealed class FakeNodeHub : INodeHubOps
{
    public HubConnectionState State { get; set; } = HubConnectionState.Connected;

    public event Func<Task>? Connected;

    public event Func<CancelSessionCommand, Task>? CancelSessionReceived;

    private readonly Queue<RequestClaimMessage> _claimsToReturn = new();
    private readonly Dictionary<Guid, DateTimeOffset> _renewals = new();
    public List<IReadOnlyList<NodeEventMessage>> PublishedBatches { get; } = [];
    public List<NodeEventAcknowledgementMessage> AcknowledgementsToReturn { get; } = [];
    public int ClaimCalls { get; private set; }
    public int HeartbeatCalls { get; private set; }
    public IReadOnlyList<string> LastHeartbeatSessionIds { get; private set; } = [];

    public void EnqueueClaim(RequestClaimMessage claim) => _claimsToReturn.Enqueue(claim);

    public void SetRenewal(Guid requestId, DateTimeOffset newExpiry) => _renewals[requestId] = newExpiry;

    public Task RaiseConnectedAsync()
        => Connected?.Invoke() ?? Task.CompletedTask;

    public Task RaiseCancelAsync(CancelSessionCommand command)
        => CancelSessionReceived?.Invoke(command) ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        State = HubConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        State = HubConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task HeartbeatAsync(IReadOnlyList<string> activeSessionIds, CancellationToken cancellationToken)
    {
        HeartbeatCalls++;
        LastHeartbeatSessionIds = activeSessionIds;
        return Task.CompletedTask;
    }

    public Task<RequestClaimMessage?> ClaimNextAsync(int leaseSeconds, CancellationToken cancellationToken)
    {
        ClaimCalls++;
        return Task.FromResult(_claimsToReturn.Count > 0 ? _claimsToReturn.Dequeue() : null);
    }

    public Task<DateTimeOffset?> RenewClaimAsync(RequestClaimMessage claim, CancellationToken cancellationToken)
        => Task.FromResult<DateTimeOffset?>(_renewals.TryGetValue(claim.RequestId, out var expiry) ? expiry : null);

    public Task<NodeEventAcknowledgementMessage> PublishEventsAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken)
    {
        PublishedBatches.Add(events);
        NodeEventAcknowledgementMessage acknowledgement;
        if (AcknowledgementsToReturn.Count > 0)
        {
            acknowledgement = AcknowledgementsToReturn[0];
            AcknowledgementsToReturn.RemoveAt(0);
        }
        else
        {
            // Default: nothing acknowledged, so callers stop replaying.
            acknowledgement = new NodeEventAcknowledgementMessage([]);
        }

        return Task.FromResult(acknowledgement);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>In-memory spool fake backed by a list, preserving insertion order.</summary>
internal sealed class FakeSpool : INodeEventSpool
{
    public List<NodeEventMessage> Pending { get; } = [];
    public List<IReadOnlyCollection<string>> Deleted { get; } = [];

    public Task AppendAsync(NodeEventMessage message, CancellationToken cancellationToken)
    {
        if (Pending.All(p => p.EventId != message.EventId))
        {
            Pending.Add(message);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NodeEventMessage>> PeekPendingAsync(int max, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<NodeEventMessage>>(Pending.Take(max).ToArray());

    public Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        Deleted.Add(eventIds);
        Pending.RemoveAll(p => eventIds.Contains(p.EventId));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records cancellation requests routed by the worker.</summary>
internal sealed class FakeSessionCanceller : ISessionCanceller
{
    public List<(string SessionId, string Reason)> Requests { get; } = [];
    public IReadOnlyList<string> ActiveSessionIds { get; set; } = [];


    public Task<bool> CancelSessionAsync(string sessionId, string reason)
    {
        Requests.Add((sessionId, reason));
        return Task.FromResult(true);
    }
}

internal sealed class FakeRootSessionSupervisor : IRootSessionSupervisor
{
    public List<RequestClaimMessage> StartedClaims { get; } = [];
    public List<(string SessionId, string Reason)> Cancelled { get; } = [];
    public IReadOnlyList<string> ActiveSessionIds => [.. _requestIdsBySession.Keys];
    public Exception? StartException { get; set; }
    private readonly Dictionary<string, Guid> _requestIdsBySession = new(StringComparer.Ordinal);

    public Task<string> StartForClaimAsync(RequestClaimMessage claim, CancellationToken cancellationToken)
    {
        if (StartException is not null)
        {
            throw StartException;
        }

        StartedClaims.Add(claim);
        var sessionId = $"root-{claim.RequestId:N}";
        _requestIdsBySession[sessionId] = claim.RequestId;
        return Task.FromResult(sessionId);
    }

    public Task<bool> CancelSessionAsync(string sessionId, string reason)
    {
        Cancelled.Add((sessionId, reason));
        return Task.FromResult(true);
    }
    public Guid? FindRequestId(string sessionId)
        => _requestIdsBySession.TryGetValue(sessionId, out var requestId) ? requestId : null;
}

internal static class NodeWorkerTestHarness
{
    public static readonly DateTimeOffset StartTime = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    public static NodeOptions CreateOptions() => new()
    {
        Id = Guid.NewGuid(),
        DisplayName = "test-node",
        AgentVersion = "1.0.0",
        HeartbeatSeconds = 10,
        ClaimLeaseSeconds = 60,
        MaxConcurrentRequests = 2,
    };

    public static (NodeWorker Worker, FakeNodeHub Hub, FakeSpool Spool, MutableTimeProvider Clock) Create(
        NodeOptions? options = null,
        FakeSpool? spool = null,
        FakeSessionCanceller? canceller = null,
        FakeRootSessionSupervisor? roots = null)
    {
        var hub = new FakeNodeHub();
        var effectiveSpool = spool ?? new FakeSpool();
        var effectiveCanceller = canceller ?? new FakeSessionCanceller();
        var effectiveRoots = roots ?? new FakeRootSessionSupervisor();
        var clock = new MutableTimeProvider(StartTime);
        var worker = new NodeWorker(
            Options.Create(options ?? CreateOptions()),
            hub,
            effectiveSpool,
            clock,
            effectiveCanceller,
            effectiveRoots,
            NullLogger<NodeWorker>.Instance);
        return (worker, hub, effectiveSpool, clock);
    }

    public static RequestClaimMessage Claim(Guid requestId, DateTimeOffset expiresAt, Guid? projectId = null) => new(
        requestId,
        projectId ?? Guid.NewGuid(),
        Guid.NewGuid(),
        $"token-{requestId}",
        expiresAt - TimeSpan.FromSeconds(60),
        expiresAt,
        "/tmp/repo",
        "main",
        "title",
        "prompt",
        "Development",
        "Low",
        CreateRequestBranch: false,
        CreateRequestCommit: false);
}

/// <summary>Manual <see cref="TimeProvider"/> for deterministic clock control.</summary>
internal sealed class MutableTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public DateTimeOffset Now => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override DateTimeOffset GetUtcNow() => _now;
}

public class NodeWorkerClaimTests
{
    private static readonly Guid RequestId = Guid.NewGuid();

    [Fact]
    public async Task Tick_claims_when_capacity_is_available_and_stops_at_max_concurrent()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(RequestId, clock.Now.AddSeconds(60)));
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(Guid.NewGuid(), clock.Now.AddSeconds(60)));

        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(2, hub.ClaimCalls);
        Assert.Equal(2, worker.ActiveClaimsSnapshot().Count);

        // Third tick: at capacity, so no further claim attempts.
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Equal(2, hub.ClaimCalls);
    }

    [Fact]
    public async Task Accepted_claim_starts_exactly_one_root_session()
    {
        var roots = new FakeRootSessionSupervisor();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(roots: roots);
        var claim = NodeWorkerTestHarness.Claim(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueClaim(claim);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(claim, Assert.Single(roots.StartedClaims));
    }

    [Fact]
    public async Task Startup_precondition_failure_projects_request_blocked()
    {
        var roots = new FakeRootSessionSupervisor
        {
            StartException = new InvalidOperationException("BLOCKED — repository is dirty"),
        };
        var (worker, hub, spool, clock) = NodeWorkerTestHarness.Create(roots: roots);
        var claim = NodeWorkerTestHarness.Claim(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueClaim(claim);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.DoesNotContain(claim.RequestId, worker.ActiveClaimsSnapshot().Keys);
        var blocked = Assert.Single(spool.Pending);
        Assert.Equal("request.blocked", blocked.Type);
        Assert.Equal(claim.RequestId, blocked.RequestId);
        Assert.Contains("root_start", blocked.PayloadJson);
    }

    [Fact]
    public async Task Cancelling_a_root_session_releases_its_claim_capacity()
    {
        var roots = new FakeRootSessionSupervisor();
        var canceller = new FakeSessionCanceller();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            canceller: canceller,
            roots: roots);
        var claim = NodeWorkerTestHarness.Claim(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueClaim(claim);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        var sessionId = Assert.Single(roots.ActiveSessionIds.DefaultIfEmpty($"root-{claim.RequestId:N}"));

        await hub.RaiseCancelAsync(new CancelSessionCommand(sessionId, "operator_cancel"));

        Assert.DoesNotContain(claim.RequestId, worker.ActiveClaimsSnapshot().Keys);
    }

    [Fact]
    public async Task Tick_renews_before_expiry_at_two_thirds_of_lease()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        var expiry = clock.Now.AddSeconds(60);
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(RequestId, expiry));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        var callsAtClaim = hub.ClaimCalls;
        hub.SetRenewal(RequestId, expiry.AddSeconds(60));

        // 30 seconds in: half the lease elapsed — not yet at the two-thirds threshold.
        clock.Advance(TimeSpan.FromSeconds(30));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.True(worker.ActiveClaimsSnapshot()[RequestId].LeaseExpiresAt == expiry,
            "claim must not be renewed before the threshold");

        // 45 seconds in: 15s remain, threshold is 40s elapsed (20s remaining) — renew now.
        clock.Advance(TimeSpan.FromSeconds(15));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.True(worker.ActiveClaimsSnapshot()[RequestId].LeaseExpiresAt > expiry,
            "claim must be renewed before expiry");
    }

    [Fact]
    public async Task Lost_claim_is_dropped_when_renewal_returns_null()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(RequestId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Contains(RequestId, worker.ActiveClaimsSnapshot().Keys);

        // No renewal registered → server rejects; claim must be dropped after the threshold.
        clock.Advance(TimeSpan.FromSeconds(45));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.DoesNotContain(RequestId, worker.ActiveClaimsSnapshot().Keys);
    }

    [Fact]
    public async Task Duplicate_request_id_is_tracked_once()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(RequestId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(RequestId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Single(worker.ActiveClaimsSnapshot());
    }
}

public class NodeWorkerReconnectTests
{
    private static readonly Guid RequestId = Guid.NewGuid();

    [Fact]
    public async Task Reconnect_keeps_running_claims_and_reconciles_expiry()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        var expiry = clock.Now.AddSeconds(60);
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(RequestId, expiry));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Contains(RequestId, worker.ActiveClaimsSnapshot().Keys);

        // Simulate reconnect: server renews the claim.
        hub.SetRenewal(RequestId, expiry.AddSeconds(120));
        await worker.HandleConnectedAsync();

        var claim = Assert.Single(worker.ActiveClaimsSnapshot());
        Assert.Equal(RequestId, claim.Key);
        Assert.Equal(expiry.AddSeconds(120), claim.Value.LeaseExpiresAt);
    }

    [Fact]
    public async Task Reconnect_drops_only_rejected_claims()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        var keptId = RequestId;
        var rejectedId = Guid.NewGuid();
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(keptId, clock.Now.AddSeconds(60)));
        hub.EnqueueClaim(NodeWorkerTestHarness.Claim(rejectedId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.SetRenewal(keptId, clock.Now.AddSeconds(90));
        // rejectedId has no renewal → rejected.

        await worker.HandleConnectedAsync();

        var claim = Assert.Single(worker.ActiveClaimsSnapshot());
        Assert.Equal(keptId, claim.Key);
        Assert.DoesNotContain(rejectedId, worker.ActiveClaimsSnapshot().Keys);
    }

    [Fact]
    public async Task Spooled_events_replay_and_only_acked_ids_are_deleted()
    {
        var spool = new FakeSpool();
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(spool: spool);
        var acked = new NodeEventMessage(Guid.NewGuid().ToString(), Guid.NewGuid(), Guid.NewGuid(), null, null, 1, "e", NodeWorkerTestHarness.StartTime, "{}");
        var unacked = new NodeEventMessage(Guid.NewGuid().ToString(), Guid.NewGuid(), Guid.NewGuid(), null, null, 2, "e", NodeWorkerTestHarness.StartTime, "{}");
        await spool.AppendAsync(acked, CancellationToken.None);
        await spool.AppendAsync(unacked, CancellationToken.None);
        hub.AcknowledgementsToReturn.Add(new NodeEventAcknowledgementMessage([acked.EventId]));

        await worker.HandleConnectedAsync();
        Assert.Equal(2, hub.PublishedBatches.Count);
        Assert.Equal(new[] { acked, unacked }, hub.PublishedBatches[0]);
        Assert.Equal(new[] { unacked }, hub.PublishedBatches[1]);
        var deletion = Assert.Single(spool.Deleted);
        Assert.Equal(new[] { acked.EventId }, deletion);
        Assert.Equal(new[] { unacked }, spool.Pending);
    }

    [Fact]
    public async Task Cancel_commands_are_routed_to_the_session_canceller()
    {
        var canceller = new FakeSessionCanceller();
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(canceller: canceller);

        await hub.RaiseCancelAsync(new CancelSessionCommand("session-1", "user requested"));

        var request = Assert.Single(canceller.Requests);
        Assert.Equal("session-1", request.SessionId);
        Assert.Equal("user requested", request.Reason);
        Assert.Empty(hub.PublishedBatches);
    }

    [Fact]
    public async Task Connected_tick_publishes_new_events_and_heartbeats_active_sessions()
    {
        var spool = new FakeSpool();
        var canceller = new FakeSessionCanceller { ActiveSessionIds = ["root-1", "child-1"] };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(spool: spool, canceller: canceller);
        var nodeEvent = new NodeEventMessage(
            "event-1", Guid.NewGuid(), Guid.NewGuid(), null, "root-1", 1, "turn.started", clock.Now, "{}");
        await spool.AppendAsync(nodeEvent, CancellationToken.None);
        hub.AcknowledgementsToReturn.Add(new NodeEventAcknowledgementMessage([nodeEvent.EventId]));

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Contains(hub.PublishedBatches, batch => batch.Contains(nodeEvent));
        Assert.Equal(["root-1", "child-1"], hub.LastHeartbeatSessionIds);
        Assert.Empty(spool.Pending);
    }
}

public class AddPiNodeTests
{
    [Fact]
    public void AddPiNode_registers_the_node_worker_as_a_hosted_service()
    {
        var services = new ServiceCollection().AddPiNode();

        Assert.Contains(services, d => d.ServiceType == typeof(NodeWorker));
        Assert.Contains(services, d => d.ServiceType == typeof(INodeHubOps));
        Assert.Contains(services, d => d.ServiceType == typeof(INodeEventSpool));
        Assert.Contains(services, d => d.ServiceType == typeof(ISessionCanceller));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(PiCommandCenter.Application.Git.ITrustedGitService)
            && d.ImplementationType == typeof(PiCommandCenter.Node.Git.RestrictedGitService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Protocol_version_is_current()
    {
        Assert.Equal(1, PiCommandCenter.Contracts.ProtocolVersion.Current);
    }
}
