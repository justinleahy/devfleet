using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Projects;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SystemResources;

namespace PiCommandCenter.Node.Tests;

/// <summary>Deterministic in-memory <see cref="INodeHubOps"/> fake.</summary>
internal sealed class FakeNodeHub : INodeHubOps
{
    public HubConnectionState State { get; set; } = HubConnectionState.Connected;

    public event Func<Task>? Connected;

    public event Func<CancelSessionCommand, Task>? CancelSessionReceived;

    public event Func<CancelAssignmentCommand, Task>? CancelAssignmentReceived;

    private readonly Queue<ExecutionAssignmentMessage> _assignmentsToReturn = new();
    private readonly Dictionary<Guid, DateTimeOffset> _renewals = new();
    public List<IReadOnlyList<NodeEventMessage>> PublishedBatches { get; } = [];
    public List<NodeEventAcknowledgementMessage> AcknowledgementsToReturn { get; } = [];
    public int ClaimNextCalls { get; private set; }
    public int HeartbeatCalls { get; private set; }
    public IReadOnlyList<string> LastHeartbeatSessionIds { get; private set; } = [];
    public NodeResourceSnapshotMessage? LastHeartbeatResources { get; private set; }
    public List<NodeResourceSnapshotMessage?> HeartbeatResources { get; } = [];
    public NodeExecutionStatusMessage? LastHeartbeatExecutionStatus { get; private set; }
    public List<NodeExecutionStatusMessage> HeartbeatExecutionStatuses { get; } = [];
    public Action<ExecutionAssignmentMessage>? Renewing { get; set; }
    public int StartCalls { get; private set; }
    public List<IReadOnlyList<ExecutionAssignmentInventoryItemMessage>> Inventories { get; } = [];
    public List<string> Operations { get; set; } = [];
    public Dictionary<Guid, AssignmentReconciliationResultMessage> ReconciliationResults { get; } = [];

    public void EnqueueAssignment(ExecutionAssignmentMessage assignment)
        => _assignmentsToReturn.Enqueue(assignment);

    public void SetRenewal(Guid requestId, DateTimeOffset newExpiry) => _renewals[requestId] = newExpiry;

    public Task RaiseConnectedAsync()
        => Connected?.Invoke() ?? Task.CompletedTask;

    public Task RaiseCancelAsync(CancelSessionCommand command)
        => CancelSessionReceived?.Invoke(command) ?? Task.CompletedTask;

    public Task RaiseAssignmentCancelAsync(CancelAssignmentCommand command)
        => CancelAssignmentReceived?.Invoke(command) ?? Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCalls++;
        State = HubConnectionState.Connected;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        State = HubConnectionState.Disconnected;
        return Task.CompletedTask;
    }

    public Task HeartbeatAsync(
        IReadOnlyList<string> activeSessionIds,
        NodeResourceSnapshotMessage resources,
        NodeExecutionStatusMessage executionStatus,
        CancellationToken cancellationToken)
    {
        HeartbeatCalls++;
        LastHeartbeatSessionIds = activeSessionIds;
        LastHeartbeatResources = resources;
        HeartbeatResources.Add(resources);
        LastHeartbeatExecutionStatus = executionStatus;
        HeartbeatExecutionStatuses.Add(executionStatus);
        return Task.CompletedTask;
    }

    public Task<ExecutionAssignmentMessage?> ClaimNextAsync(
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        Operations.Add("claim");
        ClaimNextCalls++;
        return Task.FromResult(
            _assignmentsToReturn.Count > 0 ? _assignmentsToReturn.Dequeue() : null);
    }

    public Task<DateTimeOffset?> RenewAssignmentAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken)
    {
        Renewing?.Invoke(assignment);
        return Task.FromResult<DateTimeOffset?>(
            _renewals.TryGetValue(assignment.RequestId, out var expiry) ? expiry : null);
    }
    public Task<ReconcileAssignmentsResultMessage> ReconcileAssignmentsAsync(
        IReadOnlyList<ExecutionAssignmentInventoryItemMessage> assignments,
        CancellationToken cancellationToken)
    {
        Operations.Add("inventory");
        Inventories.Add(assignments);
        return Task.FromResult(new ReconcileAssignmentsResultMessage(
            assignments.Select(item =>
            {
                if (ReconciliationResults.TryGetValue(item.Assignment.RequestId, out var result))
                {
                    return result;
                }

                return _renewals.TryGetValue(item.Assignment.RequestId, out var expiry)
                    ? new AssignmentReconciliationResultMessage(
                        item.Assignment.RequestId,
                        AssignmentReconciliationDisposition.Resume,
                        item.Assignment with { LeaseExpiresAt = expiry })
                    : new AssignmentReconciliationResultMessage(
                        item.Assignment.RequestId,
                        AssignmentReconciliationDisposition.RecoveryRequired,
                        null);
            }).ToArray()));
    }

    public Task<NodeEventAcknowledgementMessage> PublishEventsAsync(
        IReadOnlyList<NodeEventMessage> events,
        CancellationToken cancellationToken)
    {
        Operations.Add("replay");
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

    public Task<int> CountPendingForRequestAsync(Guid requestId, CancellationToken cancellationToken)
        => Task.FromResult(Pending.Count(p => p.RequestId == requestId));


    public Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
    {
        Deleted.Add(eventIds);
        Pending.RemoveAll(p => eventIds.Contains(p.EventId));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeAssignmentJournal : INodeAssignmentJournal
{
    public Dictionary<Guid, NodeAssignmentJournalEntry> Entries { get; } = [];
    public List<NodeAssignmentJournalEntry> Upserts { get; } = [];
    public List<Guid> Deleted { get; } = [];
    public List<string> Operations { get; set; } = [];
    public Exception? LoadException { get; set; }
    public Action<NodeAssignmentJournalEntry>? Upserting { get; set; }
    public Action<Guid>? Deleting { get; set; }

    public Task<IReadOnlyList<NodeAssignmentJournalEntry>> LoadAsync(
        CancellationToken cancellationToken)
    {
        Operations.Add("load");
        if (LoadException is not null)
        {
            return Task.FromException<IReadOnlyList<NodeAssignmentJournalEntry>>(LoadException);
        }

        return Task.FromResult<IReadOnlyList<NodeAssignmentJournalEntry>>([.. Entries.Values]);
    }

    public Task UpsertAsync(NodeAssignmentJournalEntry entry, CancellationToken cancellationToken)
    {
        Operations.Add("journal");
        Upserting?.Invoke(entry);
        Entries[entry.Assignment.RequestId] = entry;
        Upserts.Add(entry);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid requestId, CancellationToken cancellationToken)
    {
        Deleting?.Invoke(requestId);
        Operations.Add("journal-delete");
        Entries.Remove(requestId);
        Deleted.Add(requestId);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Records child-only cancellation requests routed by the worker.</summary>
internal sealed class FakeSessionCanceller : ISessionCanceller
{
    public List<(string SessionId, string Reason)> Requests { get; } = [];
    public IReadOnlyList<string> ActiveSessionIds { get; set; } = [];

    public Task<bool> CancelChildSessionAsync(string sessionId, string reason)
    {
        Requests.Add((sessionId, reason));
        return Task.FromResult(true);
    }
}

internal sealed class FakeRootSessionTerminalizer : IRootSessionTerminalizer
{
    public RootTerminalizationOutcome Outcome { get; set; } = RootTerminalizationOutcome.Accepted;
    public TaskCompletionSource<RootTerminalizationOutcome>? Completion { get; set; }
    public Func<Task>? Cancelling { get; set; }
    public TaskCompletionSource Invoked { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource FailureInvoked { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Action? Failing { get; set; }
    public List<(ExecutionAssignmentMessage Assignment, string SessionId, string Reason)> Requests { get; } = [];
    public List<(ExecutionAssignmentMessage Assignment, string SessionId, string Reason)> Failures { get; } = [];

    public async Task<RootTerminalizationOutcome> CancelAsync(
        ExecutionAssignmentMessage assignment,
        string rootSessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        Requests.Add((assignment, rootSessionId, reason));
        if (Cancelling is not null)
        {
            await Cancelling();
        }

        Invoked.TrySetResult();
        return Completion is null
            ? Outcome
            : await Completion.Task.WaitAsync(cancellationToken);
    }

    public Task<RootTerminalizationOutcome> FailAsync(
        ExecutionAssignmentMessage assignment,
        string rootSessionId,
        string reason,
        CancellationToken cancellationToken)
    {
        Failures.Add((assignment, rootSessionId, reason));
        Failing?.Invoke();
        FailureInvoked.TrySetResult();
        return Task.FromResult(Outcome);
    }
}

internal sealed class FakeRootSessionSupervisor : IRootSessionSupervisor
{
    public List<ExecutionAssignmentMessage> StartedAssignments { get; } = [];
    public List<(string SessionId, string Reason)> Cancelled { get; } = [];
    public IReadOnlyList<string> ActiveSessionIds => [.. _requestIdsBySession.Keys];
    public Exception? StartException { get; set; }
    public Action<ExecutionAssignmentMessage>? Starting { get; set; }
    private readonly Dictionary<string, Guid> _requestIdsBySession = new(StringComparer.Ordinal);

    public Task<string> StartForAssignmentAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken)
    {
        Starting?.Invoke(assignment);
        if (StartException is not null)
        {
            throw StartException;
        }

        StartedAssignments.Add(assignment);
        var sessionId = $"root-{assignment.RequestId:N}";
        _requestIdsBySession[sessionId] = assignment.RequestId;
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

internal sealed class FakeNodeSystemResourceMonitor : INodeSystemResourceMonitor
{
    public int CaptureCalls { get; private set; }
    public NodeResourceSnapshotMessage Snapshot { get; set; } = new(
        NodeWorkerTestHarness.StartTime,
        CpuUsagePercent: 12.5,
        MemoryUsedBytes: 100,
        MemoryTotalBytes: 200,
        DiskUsedBytes: 10,
        DiskTotalBytes: 20,
        LoadAverageOneMinute: 0.5,
        UptimeSeconds: 9);

    public NodeResourceSnapshotMessage Capture()
    {
        CaptureCalls++;
        return Snapshot;
    }
}

internal sealed class FakeRuntimeReadinessProvider(int maxConcurrentRequests = 2)
    : IRuntimeReadinessProvider
{
    public int CaptureCalls { get; private set; }
    public NodeExecutionStatusMessage? LastStatus { get; private set; }

    public NodeExecutionStatusMessage Capture(IReadOnlyList<Guid> activeAssignmentIds)
    {
        CaptureCalls++;
        var assignmentIds = activeAssignmentIds.ToArray();
        var status = new NodeExecutionStatusMessage(
            NodeWorkerTestHarness.StartTime,
            Math.Max(0, maxConcurrentRequests - assignmentIds.Length),
            assignmentIds,
            "test-routing-revision",
            []);
        LastStatus = status;
        return status;
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Messages.Add(formatter(state, exception));
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
        FakeRootSessionSupervisor? roots = null,
        FakeRootSessionTerminalizer? rootTerminalizer = null,
        FakeNodeSystemResourceMonitor? resources = null,
        FakeRuntimeReadinessProvider? readiness = null,
        NodeAssignmentCredentialSource? assignmentCredentials = null,
        FakeAssignmentJournal? journal = null,
        ILogger<NodeWorker>? logger = null)
    {
        var effectiveOptions = options ?? CreateOptions();
        var hub = new FakeNodeHub();
        var effectiveSpool = spool ?? new FakeSpool();
        var effectiveCanceller = canceller ?? new FakeSessionCanceller();
        var effectiveRoots = roots ?? new FakeRootSessionSupervisor();
        var effectiveRootTerminalizer = rootTerminalizer ?? new FakeRootSessionTerminalizer();
        var clock = new MutableTimeProvider(StartTime);
        var effectiveResources = resources ?? new FakeNodeSystemResourceMonitor();
        var effectiveReadiness = readiness
            ?? new FakeRuntimeReadinessProvider(effectiveOptions.MaxConcurrentRequests);
        var effectiveAssignmentCredentials = assignmentCredentials
            ?? new NodeAssignmentCredentialSource();
        var effectiveJournal = journal ?? new FakeAssignmentJournal();
        var worker = new NodeWorker(
            Options.Create(effectiveOptions),
            hub,
            effectiveSpool,
            effectiveJournal,
            clock,
            effectiveResources,
            effectiveReadiness,
            effectiveCanceller,
            effectiveRootTerminalizer,
            effectiveAssignmentCredentials,
            effectiveRoots,
            logger ?? NullLogger<NodeWorker>.Instance);
        return (worker, hub, effectiveSpool, clock);
    }

    public static ExecutionAssignmentMessage Assignment(
        Guid requestId,
        DateTimeOffset expiresAt,
        Guid? projectId = null,
        string state = "Starting") => new(
            RequestId: requestId,
            ProjectId: projectId ?? Guid.NewGuid(),
            WorkspaceBindingId: Guid.NewGuid(),
            NodeIdSnapshot: Guid.NewGuid(),
            CanonicalRepositoryPathSnapshot: "/tmp/repo",
            DefaultBranchSnapshot: "main",
            BindingValidationRevisionSnapshot: 1,
            State: state,
            ClaimToken: $"token-{requestId}",
            AssignedAt: expiresAt - TimeSpan.FromSeconds(60),
            LeaseExpiresAt: expiresAt,
            RequestTitle: "title",
            RequestPrompt: "prompt",
            RequestKind: "Development",
            RequestRiskLevel: "Low",
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

public class NodeAssignmentCredentialSourceTests
{
    [Fact]
    public void Lookup_returns_the_credential_by_request_or_project()
    {
        var source = new NodeAssignmentCredentialSource();
        var credential = new NodeAssignmentCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "opaque-claim");
        source.Track(credential);

        Assert.True(source.TryGetByRequest(credential.RequestId, out var byRequest));
        Assert.Same(credential, byRequest);
        Assert.True(source.TryGetByProject(credential.ProjectId, out var byProject));
        Assert.Same(credential, byProject);
        Assert.False(source.TryGetByRequest(Guid.NewGuid(), out byRequest));
        Assert.Null(byRequest);
        Assert.False(source.TryGetByProject(Guid.NewGuid(), out byProject));
        Assert.Null(byProject);
    }

    [Fact]
    public void Tracking_replacements_keeps_request_and_project_indexes_consistent()
    {
        var source = new NodeAssignmentCredentialSource();
        var initial = new NodeAssignmentCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "initial-claim");
        var requestReplacement = new NodeAssignmentCredential(
            initial.RequestId,
            Guid.NewGuid(),
            "request-replacement-claim");
        var projectReplacement = new NodeAssignmentCredential(
            Guid.NewGuid(),
            requestReplacement.ProjectId,
            "project-replacement-claim");

        source.Track(initial);
        source.Track(requestReplacement);

        Assert.False(source.TryGetByProject(initial.ProjectId, out _));
        Assert.True(source.TryGetByRequest(initial.RequestId, out var byRequest));
        Assert.Same(requestReplacement, byRequest);

        source.Track(projectReplacement);

        Assert.False(source.TryGetByRequest(requestReplacement.RequestId, out _));
        Assert.True(source.TryGetByProject(requestReplacement.ProjectId, out var byProject));
        Assert.Same(projectReplacement, byProject);
        Assert.True(source.TryGetByRequest(projectReplacement.RequestId, out byRequest));
        Assert.Same(projectReplacement, byRequest);
    }

    [Fact]
    public void Removal_only_removes_the_exact_current_credential()
    {
        var source = new NodeAssignmentCredentialSource();
        var current = new NodeAssignmentCredential(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "current-claim");
        var stale = new NodeAssignmentCredential(
            current.RequestId,
            current.ProjectId,
            "stale-claim");
        source.Track(current);

        source.Remove(stale);

        Assert.True(source.TryGetByRequest(current.RequestId, out _));
        Assert.True(source.TryGetByProject(current.ProjectId, out _));

        source.Remove(current);

        Assert.False(source.TryGetByRequest(current.RequestId, out _));
        Assert.False(source.TryGetByProject(current.ProjectId, out _));
    }

    [Fact]
    public void Credential_rejects_missing_or_oversized_claim_tokens()
    {
        var requestId = Guid.NewGuid();
        var projectId = Guid.NewGuid();

        Assert.Throws<ArgumentNullException>(
            () => new NodeAssignmentCredential(requestId, projectId, null!));
        Assert.Throws<ArgumentException>(
            () => new NodeAssignmentCredential(requestId, projectId, ""));
        Assert.Throws<ArgumentException>(
            () => new NodeAssignmentCredential(requestId, projectId, " "));
        Assert.Throws<ArgumentException>(() => new NodeAssignmentCredential(
            requestId,
            projectId,
            new string('x', NodeAssignmentCredential.MaxClaimTokenLength + 1)));
    }
}

public class NodeWorkerAssignmentTests
{
    private static readonly Guid RequestId = Guid.NewGuid();

    [Fact]
    public async Task Tick_starts_assignments_when_capacity_is_available_and_stops_at_max_concurrent()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(RequestId, clock.Now.AddSeconds(60)));
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddSeconds(60)));

        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(2, hub.ClaimNextCalls);
        Assert.Equal(2, worker.ActiveAssignmentsSnapshot().Count);

        // Third tick: at capacity, so no further assignment request is made.
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Equal(2, hub.ClaimNextCalls);
    }

    [Fact]
    public async Task ClaimNext_returning_null_does_not_start_or_track_an_assignment()
    {
        var roots = new FakeRootSessionSupervisor();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(roots: roots);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(1, hub.ClaimNextCalls);
        Assert.Empty(roots.StartedAssignments);
        Assert.Empty(worker.ActiveAssignmentsSnapshot());
    }

    [Fact]
    public async Task Starting_assignment_starts_exactly_one_root_session()
    {
        var roots = new FakeRootSessionSupervisor();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(roots: roots);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueAssignment(assignment);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(assignment, Assert.Single(roots.StartedAssignments));
    }


    [Fact]
    public async Task Assignment_is_journaled_before_root_start_and_running_state_is_persisted_afterward()
    {
        var journal = new FakeAssignmentJournal();
        var roots = new FakeRootSessionSupervisor();
        var journalPresentAtStart = false;
        roots.Starting = assignment =>
            journalPresentAtStart = journal.Entries.ContainsKey(assignment.RequestId);
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            journal: journal);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueAssignment(assignment);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.True(journalPresentAtStart);
        Assert.Equal(AssignmentSupervisorState.Unknown, journal.Upserts[0].SupervisorState);
        var persisted = journal.Entries[assignment.RequestId];
        Assert.Equal(AssignmentSupervisorState.Running, persisted.SupervisorState);
        Assert.True(persisted.RepositoryKnown);
    }

    [Theory]
    [InlineData(TerminalizationIntent.Complete)]
    [InlineData(TerminalizationIntent.Fail)]
    public async Task Accepted_begin_persists_finalizing_before_periodic_reconciliation(
        TerminalizationIntent intent)
    {
        var trace = new List<string>();
        var journal = new FakeAssignmentJournal { Operations = trace };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(journal: journal);
        hub.Operations = trace;
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        trace.Clear();
        hub.ReconciliationResults[assignment.RequestId] =
            new AssignmentReconciliationResultMessage(
                assignment.RequestId,
                AssignmentReconciliationDisposition.Resume,
                assignment with { State = "Finalizing" });
        Task? reconciliation = null;

        var decision = await worker.BeginTerminalizationAsync(
            assignment.RequestId,
            intent,
            _ =>
            {
                trace.Add("begin-accepted");
                clock.Advance(TimeSpan.FromSeconds(10));
                reconciliation = worker.RunTickAsync(clock.Now, CancellationToken.None);
                Assert.False(reconciliation.IsCompleted);
                return Task.FromResult(new CompletionGateDecision(true, [], Result: null));
            },
            CancellationToken.None);
        await reconciliation!;

        Assert.True(decision.Accepted);
        Assert.Equal("begin-accepted", trace[0]);
        Assert.Equal("journal", trace[1]);
        var inventory = Assert.Single(hub.Inventories[^1]);
        Assert.Equal("Finalizing", inventory.Assignment.State);
        Assert.Equal(
            "Finalizing",
            journal.Entries[assignment.RequestId].Assignment.State);
        Assert.Equal(
            "Finalizing",
            worker.ActiveAssignmentsSnapshot()[assignment.RequestId].State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unaccepted_begin_does_not_change_local_assignment_state(bool throws)
    {
        var journal = new FakeAssignmentJournal();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(journal: journal);
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Task<CompletionGateDecision> Begin(CancellationToken _)
            => throws
                ? Task.FromException<CompletionGateDecision>(
                    new InvalidOperationException("begin outcome unknown"))
                : Task.FromResult(new CompletionGateDecision(false, ["rejected"], Result: null));

        if (throws)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                worker.BeginTerminalizationAsync(
                    assignment.RequestId,
                    TerminalizationIntent.Complete,
                    Begin,
                    CancellationToken.None));
        }
        else
        {
            var decision = await worker.BeginTerminalizationAsync(
                assignment.RequestId,
                TerminalizationIntent.Complete,
                Begin,
                CancellationToken.None);
            Assert.False(decision.Accepted);
        }

        Assert.Equal(
            assignment.State,
            journal.Entries[assignment.RequestId].Assignment.State);
        Assert.Equal(
            assignment.State,
            worker.ActiveAssignmentsSnapshot()[assignment.RequestId].State);
    }

    [Fact]
    public async Task Accepted_cancellation_begin_persists_cancelling_not_finalizing()
    {
        var journal = new FakeAssignmentJournal();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(journal: journal);
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        await worker.BeginTerminalizationAsync(
            assignment.RequestId,
            TerminalizationIntent.Cancel,
            _ => Task.FromResult(new CompletionGateDecision(true, [], Result: null)),
            CancellationToken.None);

        Assert.Equal(
            "Cancelling",
            journal.Entries[assignment.RequestId].Assignment.State);
        Assert.Equal(
            "Cancelling",
            worker.ActiveAssignmentsSnapshot()[assignment.RequestId].State);
    }

    [Fact]
    public async Task Worker_exposes_credentials_before_launch_and_retains_them_after_rejected_renewal()
    {
        const string claimToken = "secret-claim-token-that-must-not-be-logged";
        var credentials = new NodeAssignmentCredentialSource();
        var logger = new RecordingLogger<NodeWorker>();
        var roots = new FakeRootSessionSupervisor();
        var credentialAvailableAtLaunch = false;
        roots.Starting = assignment =>
        {
            credentialAvailableAtLaunch = credentials.TryGetByRequest(
                assignment.RequestId,
                out var resolved)
                && resolved.ProjectId == assignment.ProjectId
                && resolved.ClaimToken == claimToken;
        };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            assignmentCredentials: credentials,
            logger: logger);
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddSeconds(60)) with
        {
            ClaimToken = claimToken,
        };
        var credentialAvailableAtRenewal = false;
        hub.Renewing = renewing =>
        {
            credentialAvailableAtRenewal = credentials.TryGetByProject(
                renewing.ProjectId,
                out var resolved)
                && resolved.RequestId == renewing.RequestId
                && resolved.ClaimToken == claimToken;
        };
        hub.EnqueueAssignment(assignment);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.True(credentialAvailableAtLaunch);
        Assert.True(credentials.TryGetByRequest(
            assignment.RequestId,
            out var activeCredential));
        Assert.Equal(assignment.ProjectId, activeCredential.ProjectId);
        Assert.Equal(claimToken, activeCredential.ClaimToken);

        clock.Advance(TimeSpan.FromSeconds(45));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.True(credentialAvailableAtRenewal);
        Assert.True(credentials.TryGetByRequest(assignment.RequestId, out _));
        Assert.True(credentials.TryGetByProject(assignment.ProjectId, out _));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains(claimToken, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Startup_precondition_failure_projects_request_blocked()
    {
        var roots = new FakeRootSessionSupervisor
        {
            StartException = new InvalidOperationException("BLOCKED — repository is dirty"),
        };
        var (worker, hub, spool, clock) = NodeWorkerTestHarness.Create(roots: roots);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueAssignment(assignment);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Contains(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
        var blocked = Assert.Single(spool.Pending);
        Assert.Equal("request.blocked", blocked.Type);
        Assert.Equal(assignment.RequestId, blocked.RequestId);
        Assert.Equal(assignment.ClaimToken, blocked.ClaimToken);
        Assert.Contains("root_start", blocked.PayloadJson);
    }

    [Fact]
    public async Task Startup_blocked_without_an_exact_credential_is_not_spooled_or_dropped()
    {
        var credentials = new NodeAssignmentCredentialSource();
        var roots = new FakeRootSessionSupervisor
        {
            StartException = new InvalidOperationException("BLOCKED — repository is dirty"),
            Starting = assignment => credentials.Remove(
                new NodeAssignmentCredential(
                    assignment.RequestId,
                    assignment.ProjectId,
                    assignment.ClaimToken)),
        };
        var (worker, hub, spool, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            assignmentCredentials: credentials);
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddSeconds(60));
        hub.EnqueueAssignment(assignment);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => worker.RunTickAsync(clock.Now, CancellationToken.None));

        Assert.Empty(spool.Pending);
        Assert.Contains(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
    }

    [Fact]
    public async Task Root_cancellation_retains_assignment_until_terminalization_is_accepted()
    {
        var roots = new FakeRootSessionSupervisor();
        var canceller = new FakeSessionCanceller();
        var completion = new TaskCompletionSource<RootTerminalizationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var rootTerminalizer = new FakeRootSessionTerminalizer { Completion = completion };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            canceller: canceller,
            roots: roots,
            rootTerminalizer: rootTerminalizer);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        var sessionId = Assert.Single(roots.ActiveSessionIds);

        var cancel = hub.RaiseCancelAsync(new CancelSessionCommand(sessionId, "operator_cancel"));
        await rootTerminalizer.Invoked.Task;

        Assert.Contains(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
        Assert.Empty(canceller.Requests);

        completion.SetResult(RootTerminalizationOutcome.Accepted);
        await cancel;

        Assert.DoesNotContain(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
        var request = Assert.Single(rootTerminalizer.Requests);
        Assert.Equal(assignment, request.Assignment);
        Assert.Equal(sessionId, request.SessionId);
        Assert.Equal("operator_cancel", request.Reason);
    }

    [Fact]
    public async Task Assignment_cancellation_is_journaled_before_terminalization_finishes()
    {
        var journal = new FakeAssignmentJournal();
        var roots = new FakeRootSessionSupervisor();
        var completion = new TaskCompletionSource<RootTerminalizationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalizer = new FakeRootSessionTerminalizer { Completion = completion };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            journal: journal,
            roots: roots,
            rootTerminalizer: terminalizer);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        var cancellation = hub.RaiseAssignmentCancelAsync(
            new CancelAssignmentCommand(assignment.RequestId, "operator_cancel"));
        await terminalizer.Invoked.Task;

        Assert.Equal("Cancelling", journal.Entries[assignment.RequestId].Assignment.State);
        Assert.Contains(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);

        completion.SetResult(RootTerminalizationOutcome.Accepted);
        await cancellation;
        Assert.DoesNotContain(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
    }

    [Fact]
    public async Task Concurrent_assignment_cancellations_share_one_terminalization_attempt()
    {
        var roots = new FakeRootSessionSupervisor();
        var completion = new TaskCompletionSource<RootTerminalizationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalizer = new FakeRootSessionTerminalizer { Completion = completion };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            rootTerminalizer: terminalizer);
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        var command = new CancelAssignmentCommand(assignment.RequestId, "operator_cancel");

        var first = hub.RaiseAssignmentCancelAsync(command);
        await terminalizer.Invoked.Task;
        var second = hub.RaiseAssignmentCancelAsync(command);

        Assert.False(second.IsCompleted);
        Assert.Single(terminalizer.Requests);

        completion.SetResult(RootTerminalizationOutcome.Accepted);
        await Task.WhenAll(first, second);
        Assert.Single(terminalizer.Requests);
    }

    [Fact]
    public async Task Failed_assignment_cancellation_stays_pending_and_retries_on_the_next_request()
    {
        var roots = new FakeRootSessionSupervisor();
        var attempts = 0;
        var terminalizer = new FakeRootSessionTerminalizer
        {
            Cancelling = () => ++attempts == 1
                ? Task.FromException(new InvalidOperationException("terminalizer unavailable"))
                : Task.CompletedTask,
        };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            rootTerminalizer: terminalizer);
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.EnqueueAssignment(
            NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddMinutes(1)));
        var command = new CancelAssignmentCommand(assignment.RequestId, "operator_cancel");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hub.RaiseAssignmentCancelAsync(command));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal("Cancelling", worker.ActiveAssignmentsSnapshot()[assignment.RequestId].State);
        Assert.Equal(1, hub.ClaimNextCalls);

        await hub.RaiseAssignmentCancelAsync(command);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(2, terminalizer.Requests.Count);
        Assert.Equal(2, hub.ClaimNextCalls);
    }

    [Fact]
    public async Task Worker_shutdown_cancels_and_awaits_pending_assignment_terminalization()
    {
        var journal = new FakeAssignmentJournal();
        var roots = new FakeRootSessionSupervisor();
        var completion = new TaskCompletionSource<RootTerminalizationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalizer = new FakeRootSessionTerminalizer { Completion = completion };
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            NodeWorkerTestHarness.StartTime.AddMinutes(1));
        journal.Entries.Add(
            assignment.RequestId,
            new NodeAssignmentJournalEntry(
                assignment,
                AssignmentSupervisorState.Running,
                RepositoryKnown: true,
                PendingEventCount: 0));
        await roots.StartForAssignmentAsync(assignment, CancellationToken.None);
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(
            journal: journal,
            roots: roots,
            rootTerminalizer: terminalizer);
        hub.SetRenewal(assignment.RequestId, assignment.LeaseExpiresAt.AddMinutes(1));
        await worker.LoadJournalAsync(CancellationToken.None);
        await worker.StartAsync(CancellationToken.None);

        var cancellation = hub.RaiseAssignmentCancelAsync(
            new CancelAssignmentCommand(assignment.RequestId, "operator_cancel"));
        await terminalizer.Invoked.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancellation);
    }

    [Fact]
    public async Task Uncertain_root_cancellation_retains_assignment_ownership()
    {
        var roots = new FakeRootSessionSupervisor();
        var rootTerminalizer = new FakeRootSessionTerminalizer
        {
            Outcome = RootTerminalizationOutcome.Uncertain,
        };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            rootTerminalizer: rootTerminalizer);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddSeconds(60));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        await hub.RaiseCancelAsync(new CancelSessionCommand(
            Assert.Single(roots.ActiveSessionIds),
            "operator_cancel"));

        Assert.Contains(assignment.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
    }

    [Fact]
    public async Task Tick_renews_before_expiry_at_two_thirds_of_lease()
    {
        var options = NodeWorkerTestHarness.CreateOptions();
        options.HeartbeatSeconds = 120;
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(options);
        var expiry = clock.Now.AddSeconds(60);
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(RequestId, expiry));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.SetRenewal(RequestId, expiry.AddSeconds(60));

        // 30 seconds in: half the lease elapsed — not yet at the two-thirds threshold.
        clock.Advance(TimeSpan.FromSeconds(30));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.True(worker.ActiveAssignmentsSnapshot()[RequestId].LeaseExpiresAt == expiry,
            "assignment must not be renewed before the threshold");

        // 45 seconds in: 15s remain, threshold is 40s elapsed (20s remaining) — renew now.
        clock.Advance(TimeSpan.FromSeconds(15));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.True(worker.ActiveAssignmentsSnapshot()[RequestId].LeaseExpiresAt > expiry,
            "assignment must be renewed before expiry");
    }

    [Fact]
    public async Task Cancellation_replays_its_terminal_event_while_blocking_another_claim()
    {
        var roots = new FakeRootSessionSupervisor();
        var spool = new FakeSpool();
        var completion = new TaskCompletionSource<RootTerminalizationOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalizer = new FakeRootSessionTerminalizer { Completion = completion };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            spool: spool,
            roots: roots,
            rootTerminalizer: terminalizer);
        var assignment = NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddMinutes(1)));
        var cancelled = new NodeEventMessage(
            "cancelled-event",
            assignment.NodeIdSnapshot,
            assignment.ProjectId,
            assignment.RequestId,
            assignment.ClaimToken,
            Assert.Single(roots.ActiveSessionIds),
            1,
            "session.cancelled",
            clock.Now,
            "{}");
        terminalizer.Cancelling = () => spool.AppendAsync(cancelled, CancellationToken.None);
        hub.AcknowledgementsToReturn.Add(
            new NodeEventAcknowledgementMessage([cancelled.EventId]));

        var cancellation = hub.RaiseCancelAsync(new CancelSessionCommand(
            cancelled.SessionId!,
            "operator_cancel"));
        await terminalizer.Invoked.Task;
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(1, hub.ClaimNextCalls);
        Assert.Contains(hub.PublishedBatches, batch => batch.Contains(cancelled));
        Assert.Empty(spool.Pending);

        completion.SetResult(RootTerminalizationOutcome.Accepted);
        await cancellation;
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(2, hub.ClaimNextCalls);
    }

    [Fact]
    public async Task Rejected_renewal_retains_assignment_ownership()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(RequestId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Contains(RequestId, worker.ActiveAssignmentsSnapshot().Keys);

        clock.Advance(TimeSpan.FromSeconds(45));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Contains(RequestId, worker.ActiveAssignmentsSnapshot().Keys);
    }

    [Fact]
    public async Task Duplicate_request_id_is_tracked_once()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(RequestId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(RequestId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Single(worker.ActiveAssignmentsSnapshot());
    }
}

public class NodeWorkerReconnectTests
{
    private static readonly Guid RequestId = Guid.NewGuid();

    [Fact]
    public async Task Reconnect_keeps_running_assignments_and_reconciles_expiry()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        var expiry = clock.Now.AddSeconds(60);
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(RequestId, expiry));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Contains(RequestId, worker.ActiveAssignmentsSnapshot().Keys);

        // Simulate reconnect: server renews the assignment.
        hub.SetRenewal(RequestId, expiry.AddSeconds(120));
        await worker.HandleConnectedAsync();

        var assignment = Assert.Single(worker.ActiveAssignmentsSnapshot());
        Assert.Equal(RequestId, assignment.Key);
        Assert.Equal(expiry.AddSeconds(120), assignment.Value.LeaseExpiresAt);
    }

    [Fact]
    public async Task Reconnect_cancels_durable_offline_work_instead_of_resuming_it()
    {
        var roots = new FakeRootSessionSupervisor();
        var journal = new FakeAssignmentJournal();
        var assignmentRemoved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalizer = new FakeRootSessionTerminalizer();
        journal.Deleting = _ => assignmentRemoved.SetResult();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            rootTerminalizer: terminalizer,
            journal: journal);
        var assignment = NodeWorkerTestHarness.Assignment(RequestId, clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(assignment);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.ReconciliationResults[RequestId] = new AssignmentReconciliationResultMessage(
            RequestId,
            AssignmentReconciliationDisposition.Cancel,
            assignment with { State = "Cancelling" });

        await worker.HandleConnectedAsync();
        await assignmentRemoved.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var request = Assert.Single(terminalizer.Requests);
        Assert.Equal(RequestId, request.Assignment.RequestId);
        Assert.Equal("control-plane-reconciliation", request.Reason);
        Assert.DoesNotContain(RequestId, worker.ActiveAssignmentsSnapshot().Keys);
    }

    [Fact]
    public async Task Reconnect_replays_shutdown_events_before_cancellation_terminalizes()
    {
        var journal = new FakeAssignmentJournal();
        var roots = new FakeRootSessionSupervisor();
        var spool = new FakeSpool();
        var running = NodeWorkerTestHarness.Assignment(
            RequestId,
            NodeWorkerTestHarness.StartTime.AddMinutes(1));
        var terminalizationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var replayFinished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var assignmentRemoved = new TaskCompletionSource<Guid>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var terminalizer = new FakeRootSessionTerminalizer
        {
            Cancelling = async () =>
            {
                var assignment = running;
                await spool.AppendAsync(
                    new NodeEventMessage(
                        "cancelled-event",
                        assignment.NodeIdSnapshot,
                        assignment.ProjectId,
                        assignment.RequestId,
                        assignment.ClaimToken,
                        $"root-{RequestId:N}",
                        1,
                        "session.cancelled",
                        NodeWorkerTestHarness.StartTime,
                        "{}"),
                    CancellationToken.None);
                terminalizationStarted.SetResult();
                await replayFinished.Task;
            },
        };
        journal.Deleting = requestId => assignmentRemoved.TrySetResult(requestId);
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            spool: spool,
            roots: roots,
            rootTerminalizer: terminalizer,
            journal: journal);
        hub.EnqueueAssignment(running);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.EnqueueAssignment(
            NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddMinutes(1)));
        hub.ReconciliationResults[RequestId] = new AssignmentReconciliationResultMessage(
            RequestId,
            AssignmentReconciliationDisposition.Cancel,
            running with { State = "Cancelling" });
        hub.AcknowledgementsToReturn.Add(
            new NodeEventAcknowledgementMessage(["cancelled-event"]));

        var reconnect = worker.HandleConnectedAsync();
        await terminalizationStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await reconnect.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("Cancelling", journal.Entries[RequestId].Assignment.State);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Empty(spool.Pending);
        Assert.Equal(1, hub.ClaimNextCalls);

        replayFinished.SetResult();
        Assert.Equal(
            RequestId,
            await assignmentRemoved.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.DoesNotContain(RequestId, worker.ActiveAssignmentsSnapshot().Keys);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        Assert.Equal(2, hub.ClaimNextCalls);
    }

    [Fact]
    public async Task Restart_inventory_is_unknown_and_resume_never_starts_a_duplicate_root()
    {
        var journal = new FakeAssignmentJournal();
        var roots = new FakeRootSessionSupervisor();
        var credentials = new NodeAssignmentCredentialSource();
        var assignment = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            NodeWorkerTestHarness.StartTime.AddSeconds(60));
        journal.Entries.Add(
            assignment.RequestId,
            new NodeAssignmentJournalEntry(
                assignment,
                AssignmentSupervisorState.Unknown,
                RepositoryKnown: true,
                PendingEventCount: 0));
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            roots: roots,
            assignmentCredentials: credentials,
            journal: journal);
        var resumed = assignment with { LeaseExpiresAt = clock.Now.AddMinutes(2) };
        hub.ReconciliationResults[assignment.RequestId] = new AssignmentReconciliationResultMessage(
            assignment.RequestId,
            AssignmentReconciliationDisposition.Resume,
            resumed);

        await worker.LoadJournalAsync(CancellationToken.None);
        await worker.HandleConnectedAsync();
        hub.EnqueueAssignment(resumed);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        var inventory = Assert.Single(Assert.Single(hub.Inventories));
        Assert.Equal(AssignmentSupervisorState.Unknown, inventory.SupervisorState);
        Assert.Empty(roots.StartedAssignments);
        Assert.Equal(resumed, worker.ActiveAssignmentsSnapshot()[assignment.RequestId]);
        Assert.True(credentials.TryGetByRequest(assignment.RequestId, out _));
    }

    [Fact]
    public async Task Connection_orders_inventory_before_replay_and_claim()
    {
        var operations = new List<string>();
        var spool = new FakeSpool();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(spool: spool);
        hub.Operations = operations;
        var pending = new NodeEventMessage(
            "event-1",
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "claim-token",
            null,
            1,
            "turn.started",
            clock.Now,
            "{}");
        await spool.AppendAsync(pending, CancellationToken.None);
        hub.AcknowledgementsToReturn.Add(new NodeEventAcknowledgementMessage([pending.EventId]));
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(Guid.NewGuid(), clock.Now.AddMinutes(1)));

        await worker.HandleConnectedAsync();
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(["inventory", "replay", "claim"], operations);
    }

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    public async Task Periodic_terminal_reconciliation_releases_credentials_and_capacity(
        string terminalState)
    {
        var options = NodeWorkerTestHarness.CreateOptions();
        options.MaxConcurrentRequests = 1;
        var journal = new FakeAssignmentJournal();
        var credentials = new NodeAssignmentCredentialSource();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            options: options,
            assignmentCredentials: credentials,
            journal: journal);
        var finished = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        var replacement = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            clock.Now.AddMinutes(1));
        hub.EnqueueAssignment(finished);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.EnqueueAssignment(replacement);
        hub.ReconciliationResults[finished.RequestId] =
            new AssignmentReconciliationResultMessage(
                finished.RequestId,
                AssignmentReconciliationDisposition.Terminal,
                finished with { State = terminalState });

        clock.Advance(TimeSpan.FromSeconds(options.HeartbeatSeconds));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Contains(finished.RequestId, journal.Deleted);
        Assert.False(credentials.TryGetByRequest(finished.RequestId, out _));
        Assert.False(credentials.TryGetByProject(finished.ProjectId, out _));
        var active = Assert.Single(worker.ActiveAssignmentsSnapshot());
        Assert.Equal(replacement.RequestId, active.Key);
        Assert.Equal(2, hub.ClaimNextCalls);
    }

    [Fact]
    public async Task Terminal_reconciliation_deletes_while_recovery_required_retains()
    {
        var journal = new FakeAssignmentJournal();
        var terminal = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            NodeWorkerTestHarness.StartTime.AddMinutes(1));
        var recovery = NodeWorkerTestHarness.Assignment(
            Guid.NewGuid(),
            NodeWorkerTestHarness.StartTime.AddMinutes(1));
        journal.Entries[terminal.RequestId] = new NodeAssignmentJournalEntry(
            terminal,
            AssignmentSupervisorState.Unknown,
            RepositoryKnown: true,
            PendingEventCount: 0);
        journal.Entries[recovery.RequestId] = new NodeAssignmentJournalEntry(
            recovery,
            AssignmentSupervisorState.Unknown,
            RepositoryKnown: true,
            PendingEventCount: 0);
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(journal: journal);
        hub.ReconciliationResults[terminal.RequestId] = new AssignmentReconciliationResultMessage(
            terminal.RequestId,
            AssignmentReconciliationDisposition.Terminal,
            null);
        hub.ReconciliationResults[recovery.RequestId] = new AssignmentReconciliationResultMessage(
            recovery.RequestId,
            AssignmentReconciliationDisposition.RecoveryRequired,
            null);

        await worker.LoadJournalAsync(CancellationToken.None);
        await worker.HandleConnectedAsync();

        Assert.DoesNotContain(terminal.RequestId, journal.Entries.Keys);
        Assert.Contains(terminal.RequestId, journal.Deleted);
        Assert.Contains(recovery.RequestId, journal.Entries.Keys);
        Assert.Contains(recovery.RequestId, worker.ActiveAssignmentsSnapshot().Keys);
    }


    [Fact]
    public async Task Reconnect_retains_recovery_required_assignments()
    {
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create();
        var keptId = RequestId;
        var rejectedId = Guid.NewGuid();
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(keptId, clock.Now.AddSeconds(60)));
        hub.EnqueueAssignment(NodeWorkerTestHarness.Assignment(rejectedId, clock.Now.AddSeconds(60)));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        await worker.RunTickAsync(clock.Now, CancellationToken.None);
        hub.SetRenewal(keptId, clock.Now.AddSeconds(90));
        await worker.HandleConnectedAsync();

        Assert.Equal(2, worker.ActiveAssignmentsSnapshot().Count);
        Assert.Equal(
            clock.Now.AddSeconds(90),
            worker.ActiveAssignmentsSnapshot()[keptId].LeaseExpiresAt);
        Assert.Contains(rejectedId, worker.ActiveAssignmentsSnapshot().Keys);
    }

    [Fact]
    public async Task Spooled_events_replay_and_only_acked_ids_are_deleted()
    {
        var spool = new FakeSpool();
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(spool: spool);
        var acked = new NodeEventMessage(Guid.NewGuid().ToString(), Guid.NewGuid(), Guid.NewGuid(), null, "claim-token", null, 1, "e", NodeWorkerTestHarness.StartTime, "{}");
        var unacked = new NodeEventMessage(Guid.NewGuid().ToString(), Guid.NewGuid(), Guid.NewGuid(), null, "claim-token", null, 2, "e", NodeWorkerTestHarness.StartTime, "{}");
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
    public async Task Child_cancel_commands_are_routed_only_to_the_child_canceller()
    {
        var canceller = new FakeSessionCanceller();
        var rootTerminalizer = new FakeRootSessionTerminalizer();
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(
            canceller: canceller,
            rootTerminalizer: rootTerminalizer);

        await hub.RaiseCancelAsync(new CancelSessionCommand("session-1", "user requested"));

        var request = Assert.Single(canceller.Requests);
        Assert.Equal("session-1", request.SessionId);
        Assert.Equal("user requested", request.Reason);
        Assert.Empty(rootTerminalizer.Requests);
        Assert.Empty(hub.PublishedBatches);
    }

    [Fact]
    public async Task Connected_tick_publishes_new_events_and_heartbeats_active_sessions()
    {
        var spool = new FakeSpool();
        var canceller = new FakeSessionCanceller { ActiveSessionIds = ["root-1", "child-1"] };
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(spool: spool, canceller: canceller);
        var nodeEvent = new NodeEventMessage(
            "event-1", Guid.NewGuid(), Guid.NewGuid(), null, "claim-token", "root-1", 1, "turn.started", clock.Now, "{}");
        await spool.AppendAsync(nodeEvent, CancellationToken.None);
        hub.AcknowledgementsToReturn.Add(new NodeEventAcknowledgementMessage([nodeEvent.EventId]));

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Contains(hub.PublishedBatches, batch => batch.Contains(nodeEvent));
        Assert.Equal(["root-1", "child-1"], hub.LastHeartbeatSessionIds);
        Assert.Empty(spool.Pending);
    }
}

public class NodeWorkerHeartbeatTests
{
    [Fact]
    public async Task Due_heartbeat_captures_and_sends_exactly_one_snapshot()
    {
        var resources = new FakeNodeSystemResourceMonitor();
        var readiness = new FakeRuntimeReadinessProvider();
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(
            resources: resources,
            readiness: readiness);

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        Assert.Equal(1, resources.CaptureCalls);
        Assert.Equal(1, readiness.CaptureCalls);
        Assert.Equal(1, hub.HeartbeatCalls);
        Assert.Same(resources.Snapshot, hub.LastHeartbeatResources);
        Assert.Same(readiness.LastStatus, hub.LastHeartbeatExecutionStatus);

        await worker.RunTickAsync(clock.Now.AddSeconds(1), CancellationToken.None);
        Assert.Equal(1, resources.CaptureCalls);
        Assert.Equal(1, hub.HeartbeatCalls);
        Assert.Equal(1, readiness.CaptureCalls);

        await worker.RunTickAsync(clock.Now.AddSeconds(10), CancellationToken.None);
        Assert.Equal(2, resources.CaptureCalls);
        Assert.Equal(2, hub.HeartbeatCalls);
        Assert.Equal(2, readiness.CaptureCalls);
        Assert.Equal(2, hub.HeartbeatResources.Count);
        Assert.All(hub.HeartbeatResources, snapshot => Assert.Same(resources.Snapshot, snapshot));
        Assert.Equal(2, hub.HeartbeatExecutionStatuses.Count);
    }

    [Fact]
    public async Task Heartbeat_execution_status_tracks_all_active_assignment_request_ids_and_slots()
    {
        var readiness = new FakeRuntimeReadinessProvider(maxConcurrentRequests: 2);
        var (worker, hub, _, clock) = NodeWorkerTestHarness.Create(readiness: readiness);
        var firstRequestId = Guid.NewGuid();
        var secondRequestId = Guid.NewGuid();
        hub.EnqueueAssignment(
            NodeWorkerTestHarness.Assignment(firstRequestId, clock.Now.AddSeconds(60)));
        hub.EnqueueAssignment(
            NodeWorkerTestHarness.Assignment(secondRequestId, clock.Now.AddSeconds(60)));

        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        var firstStatus = Assert.IsType<NodeExecutionStatusMessage>(hub.LastHeartbeatExecutionStatus);
        Assert.Equal(1, firstStatus.AvailableRequestSlots);
        Assert.Equal([firstRequestId], firstStatus.ActiveAssignmentIds);

        clock.Advance(TimeSpan.FromSeconds(10));
        await worker.RunTickAsync(clock.Now, CancellationToken.None);

        var secondStatus = Assert.IsType<NodeExecutionStatusMessage>(hub.LastHeartbeatExecutionStatus);
        Assert.Equal(0, secondStatus.AvailableRequestSlots);
        Assert.Equal(2, secondStatus.ActiveAssignmentIds.Count);
        Assert.Contains(firstRequestId, secondStatus.ActiveAssignmentIds);
        Assert.Contains(secondRequestId, secondStatus.ActiveAssignmentIds);
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
        Assert.Contains(services, d => d.ServiceType == typeof(INodeAssignmentJournal));
        Assert.Contains(services, d => d.ServiceType == typeof(ISessionCanceller));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(NodeAssignmentCredentialSource)
            && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d =>
            d.ServiceType == typeof(INodeAssignmentCredentialSource)
            && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IWorkspaceBindingValidator)
            && d.ImplementationType == typeof(WorkspaceBindingValidator));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IRuntimeReadinessProbe)
            && d.ImplementationType == typeof(RuntimeReadinessProbe));
        Assert.Contains(services, d =>
            d.ServiceType == typeof(RuntimeReadinessProvider)
            && d.Lifetime == ServiceLifetime.Singleton);
        Assert.Contains(services, d =>
            d.ServiceType == typeof(IRuntimeReadinessProvider)
            && d.ImplementationFactory is not null);
        Assert.Contains(services, d =>

            d.ServiceType == typeof(PiCommandCenter.Application.Git.ITrustedGitService)
            && d.ImplementationType == typeof(PiCommandCenter.Node.Git.RestrictedGitService));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void AddPiNode_binds_workspace_validation_options_from_projects()
    {
        var configuration = new ConfigurationManager();
        configuration["Projects:ApprovedRoots:0"] = "/srv/projects";

        var services = new ServiceCollection()
            .AddSingleton<IConfiguration>(configuration)
            .AddPiNode();
        using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptions<WorkspaceValidationOptions>>()
            .Value;

        Assert.Contains("/srv/projects", options.ApprovedRoots);
    }

    [Fact]
    public void Protocol_version_is_current()
    {
        Assert.Equal(1, PiCommandCenter.Contracts.ProtocolVersion.Current);
    }
}
public class NodeWorkerStartupTests
{
    [Fact]
    public async Task Corrupt_journal_prevents_transport_start()
    {
        var journal = new FakeAssignmentJournal
        {
            LoadException = new NodeAssignmentJournalCorruptionException("corrupt"),
        };
        var (worker, hub, _, _) = NodeWorkerTestHarness.Create(journal: journal);

        await Assert.ThrowsAsync<NodeAssignmentJournalCorruptionException>(
            () => worker.LoadJournalAsync(CancellationToken.None));

        Assert.Equal(0, hub.StartCalls);
    }
}
