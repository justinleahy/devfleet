using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Quiescence;
using PiCommandCenter.Node.Recovery;
using PiCommandCenter.Node.Repository;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeAssignmentRecoveryRuntimeTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Journals_process_identities_before_isolated_stop()
    {
        var world = CreateWorld();
        var isolation = new RecordingIsolation(new AssignmentProcessIdentity(11, 100, 11, 11, "root"));
        world.Registry.Register(world.Command.RequestId, isolation);
        world.Journal.Entries[world.Command.RequestId] = Entry(world.Assignment);

        IAssignmentRecoveryRuntime runtime = world.Runtime;
        await runtime.JournalRecoveryIntentAsync(world.Command, CancellationToken.None);

        var upsert = Assert.Single(world.Journal.Upserts);
        Assert.Equal(world.Command.RequestId, upsert.Assignment.RequestId);
        var identity = Assert.Single(upsert.ProcessIdentities!);
        Assert.Equal(11, identity.ProcessId);
        Assert.False(isolation.StopCalled);

        await runtime.StopIsolatedProcessesAsync(world.Command, CancellationToken.None);
        Assert.True(isolation.StopCalled);
        Assert.Equal(
            [
                "journal-load",
                "journal-upsert",
                "registry-stop",
            ],
            world.Sequence);
    }

    [Fact]
    public async Task Close_admission_cooperative_stop_and_drain_are_request_scoped()
    {
        var world = CreateWorld();
        IAssignmentRecoveryRuntime runtime = world.Runtime;
        await runtime.CloseAdmissionAsync(world.Command, CancellationToken.None);
        await runtime.RequestCooperativeStopAsync(world.Command, CancellationToken.None);
        await runtime.DrainSupervisedOperationsAsync(world.Command, CancellationToken.None);

        Assert.True(world.Admission.IsAdmissionClosed(world.Command.RequestId));
        Assert.Equal(world.Command.RequestId, Assert.Single(world.Sessions.Cancelled));
        Assert.Contains(world.Command.RequestId, world.Admission.Drained);
        Assert.Equal(10, world.Options.RecoveryCooperativeStopSeconds);
        Assert.Equal(20, world.Options.RecoveryTerminationSeconds);
    }

    [Fact]
    public async Task Isolated_stop_uses_node_option_budgets_and_propagates_unknown()
    {
        var world = CreateWorld();
        var isolation = new RecordingIsolation(
            new AssignmentProcessIdentity(42, 9, 42, 42, "child"),
            proven: false);
        world.Registry.Register(world.Command.RequestId, isolation);
        IAssignmentRecoveryRuntime runtime = world.Runtime;

        var snapshot = await runtime.StopIsolatedProcessesAsync(world.Command, CancellationToken.None);

        Assert.True(snapshot.Processes.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.ProcessStopUnproven, snapshot.Processes.UnknownReasonCode);
        Assert.Equal(42, Assert.Single(snapshot.Identities).Pid);
        Assert.Equal(
            TimeSpan.FromSeconds(10),
            world.Registry.LastCooperativeBudget);
        Assert.Equal(
            TimeSpan.FromSeconds(20),
            world.Registry.LastTerminationBudget);
    }

    [Fact]
    public async Task Flush_reports_ack_position_and_does_not_delete_unacknowledged_events()
    {
        var world = CreateWorld();
        var kept = Event(world.Assignment, "keep", 2);
        var acked = Event(world.Assignment, "ack", 1);
        var other = Event(world.OtherAssignment, "other", 3);
        world.Spool.Pending.AddRange([acked, kept, other]);
        world.Publisher.Ack = new AssignmentRecoveryEventAck(["ack"], 88, null);
        IAssignmentRecoveryRuntime runtime = world.Runtime;

        var flushed = await runtime.FlushAcknowledgedEventsAsync(world.Command, CancellationToken.None);

        Assert.Equal(88, flushed.EventAcknowledgementPosition);
        Assert.Null(flushed.EventAcknowledgementUnknownReasonCode);
        Assert.True(flushed.PendingEvents.IsKnown);
        Assert.Equal(1, flushed.PendingEvents.Value);
        Assert.DoesNotContain(world.Spool.Pending, evt => evt.EventId == "ack");
        Assert.Contains(world.Spool.Pending, evt => evt.EventId == "keep");
        Assert.Contains(world.Spool.Pending, evt => evt.EventId == "other");
        Assert.Equal(world.Command.RequestId, world.Publisher.LastRequestId);
    }

    [Fact]
    public async Task Flush_unknown_ack_keeps_spool_when_publisher_cannot_prove_position()
    {
        var world = CreateWorld();
        world.Spool.Pending.Add(Event(world.Assignment, "pending", 4));
        world.Publisher.Ack = new AssignmentRecoveryEventAck([], null, RecoveryReasonCodes.EventsUnacknowledged);
        IAssignmentRecoveryRuntime runtime = world.Runtime;

        var flushed = await runtime.FlushAcknowledgedEventsAsync(world.Command, CancellationToken.None);

        Assert.Null(flushed.EventAcknowledgementPosition);
        Assert.Equal(RecoveryReasonCodes.EventsUnacknowledged, flushed.EventAcknowledgementUnknownReasonCode);
        Assert.Equal("pending", Assert.Single(world.Spool.Pending).EventId);
        Assert.Empty(world.Spool.Deleted);
    }

    [Fact]
    public async Task Dirty_and_unborn_repositories_are_known_unavailable_is_unknown()
    {
        var world = CreateWorld();
        world.Journal.Entries[world.Command.RequestId] = Entry(world.Assignment);
        IAssignmentRecoveryRuntime runtime = world.Runtime;

        world.Inspector.Baseline = new RepositoryBaseline(
            "main",
            "abc123",
            " M file",
            IsClean: false,
            DirtyPaths: ["file"]);
        var dirty = await runtime.InspectRepositoryAsync(world.Command, CancellationToken.None);
        Assert.True(dirty.Available);
        Assert.Equal("abc123", dirty.Head);
        Assert.Equal("modified", dirty.WorktreeSummary);
        Assert.True(dirty.UntrackedCount.IsKnown);

        world.Inspector.Baseline = new RepositoryBaseline(
            string.Empty,
            string.Empty,
            string.Empty,
            IsClean: true,
            DirtyPaths: []);
        var unborn = await runtime.InspectRepositoryAsync(world.Command, CancellationToken.None);
        Assert.True(unborn.Available);
        Assert.Null(unborn.Head);
        Assert.Equal("empty", unborn.WorktreeSummary);
        Assert.True(unborn.UntrackedCount.IsKnown);

        world.Inspector.Throw = new IOException("git missing");
        var unavailable = await runtime.InspectRepositoryAsync(world.Command, CancellationToken.None);
        Assert.False(unavailable.Available);
        Assert.True(unavailable.UntrackedCount.IsUnknown);
        Assert.Equal(
            RecoveryReasonCodes.RepositoryStatusUnknown,
            unavailable.UntrackedCount.UnknownReasonCode);
        Assert.Equal(world.Assignment.CanonicalRepositoryPathSnapshot, world.Inspector.LastRoot);
    }

    [Fact]
    public async Task Reservations_resolve_only_after_proven_stop_and_only_for_the_assignment()
    {
        var world = CreateWorld();
        var own = new ReservationLeaseInfo(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            1,
            "Active",
            T0.AddMinutes(1),
            [new ReservationScopeSpec("file", "a.txt")],
            "root");
        var foreign = new ReservationLeaseInfo(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            1,
            "Active",
            T0.AddMinutes(1),
            [new ReservationScopeSpec("file", "b.txt")],
            "other");
        world.Reservations.Seed(own);
        world.Reservations.Seed(foreign);
        world.Catalog.ByRequest[world.Command.RequestId] = [own];
        world.Catalog.ByRequest[world.OtherAssignment.RequestId] = [foreign];
        IAssignmentRecoveryRuntime runtime = world.Runtime;

        var skipped = await runtime.ResolveReservationsAsync(world.Command, CancellationToken.None);
        Assert.Empty(skipped);
        Assert.Empty(world.Reservations.Recoveries);

        world.Registry.Register(
            world.Command.RequestId,
            new RecordingIsolation(new AssignmentProcessIdentity(8, 1, 8, 8, "root"), proven: true));
        await runtime.StopIsolatedProcessesAsync(world.Command, CancellationToken.None);
        var resolved = await runtime.ResolveReservationsAsync(world.Command, CancellationToken.None);

        var disposition = Assert.Single(resolved);
        Assert.Equal(own.LeaseId, disposition.LeaseId);
        Assert.Equal("resolved", disposition.Disposition);
        Assert.Equal(own.LeaseId, Assert.Single(world.Reservations.Recoveries).LeaseId);
        Assert.DoesNotContain(world.Reservations.Recoveries, item => item.LeaseId == foreign.LeaseId);
        Assert.Equal(
            ["registry-stop", "reservation-list", "reservation-mark"],
            world.Sequence.Where(step => step.StartsWith("registry", StringComparison.Ordinal)
                || step.StartsWith("reservation", StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task Observe_inventory_propagates_unknown_and_isolates_projects()
    {
        var world = CreateWorld();
        world.Activities.ChildrenUnknown = RecoveryReasonCodes.ProcessStopUnproven;
        world.Activities.OperationsUnknown = RecoveryReasonCodes.OperationDrainTimeout;
        world.Spool.Pending.Add(Event(world.Assignment, "a", 1));
        world.Spool.Pending.Add(Event(world.OtherAssignment, "b", 2));
        world.Catalog.ThrowFor.Add(world.Command.RequestId);
        IAssignmentRecoveryRuntime runtime = world.Runtime;

        var observed = await runtime.ObserveInventoryAsync(world.Command, CancellationToken.None);

        Assert.True(observed.Children.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.ProcessStopUnproven, observed.Children.UnknownReasonCode);
        Assert.True(observed.Operations.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.OperationDrainTimeout, observed.Operations.UnknownReasonCode);
        Assert.True(observed.Processes.IsUnknown);
        Assert.True(observed.PendingEvents.IsKnown);
        Assert.Equal(1, observed.PendingEvents.Value);
        Assert.True(observed.Reservations.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.ReservationUnresolved, observed.Reservations.UnknownReasonCode);
    }


    private static World CreateWorld()
    {
        var projectId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var requestId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
        var otherRequest = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");
        var assignment = MakeAssignment(requestId, projectId, "/srv/repos/canonical");
        var other = MakeAssignment(otherRequest, Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"), "/srv/repos/other");
        var command = new RecoverAssignmentCommandMessage(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            1,
            projectId,
            requestId,
            "claim-token",
            BindingRevision: 7,
            T0.AddMinutes(1));
        var sequence = new List<string>();
        var options = new NodeOptions
        {
            RecoveryCooperativeStopSeconds = 10,
            RecoveryTerminationSeconds = 20,
        };
        var journal = new RecordingJournal(sequence);
        var registry = new RecordingRegistry(sequence);
        var admission = new RecordingAdmission(sequence);
        var sessions = new RecordingSessions(sequence);
        var activities = new RecordingActivities(sequence);
        var spool = new RecordingSpool(sequence);
        var publisher = new RecordingPublisher(sequence);
        var inspector = new RecordingInspector(sequence);
        var reservations = new RecordingReservations(sequence);
        var catalog = new RecordingCatalog(sequence);
        var runtime = new NodeAssignmentRecoveryRuntime(
            journal,
            registry.Inner,
            admission,
            sessions,
            activities,
            spool,
            publisher,
            inspector,
            reservations,
            catalog,
            Options.Create(options),
            new FixedTimeProvider(T0));
        return new World(
            runtime,
            journal,
            registry,
            admission,
            sessions,
            activities,
            spool,
            publisher,
            inspector,
            reservations,
            catalog,
            options,
            command,
            assignment,
            other,
            sequence);
    }

    private static NodeAssignmentJournalEntry Entry(ExecutionAssignmentMessage assignment) =>
        new(assignment, AssignmentSupervisorState.Running, RepositoryKnown: true, PendingEventCount: 0);

    private static ExecutionAssignmentMessage MakeAssignment(Guid requestId, Guid projectId, string path) =>
        new(
            requestId,
            projectId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            path,
            "main",
            1,
            "Running",
            "claim-token",
            T0,
            T0.AddHours(1),
            "title",
            "prompt",
            "kind",
            "risk",
            false,
            false);

    private static NodeEventMessage Event(
        ExecutionAssignmentMessage assignment,
        string eventId,
        long sequence) =>
        new(
            eventId,
            assignment.NodeIdSnapshot,
            assignment.ProjectId,
            assignment.RequestId,
            assignment.ClaimToken,
            "session",
            sequence,
            "log",
            T0,
            "{}");

    private sealed record World(
        NodeAssignmentRecoveryRuntime Runtime,
        RecordingJournal Journal,
        RecordingRegistry Registry,
        RecordingAdmission Admission,
        RecordingSessions Sessions,
        RecordingActivities Activities,
        RecordingSpool Spool,
        RecordingPublisher Publisher,
        RecordingInspector Inspector,
        RecordingReservations Reservations,
        RecordingCatalog Catalog,
        NodeOptions Options,
        RecoverAssignmentCommandMessage Command,
        ExecutionAssignmentMessage Assignment,
        ExecutionAssignmentMessage OtherAssignment,
        List<string> Sequence);

    private sealed class RecordingJournal(List<string> sequence) : INodeAssignmentJournal
    {
        public Dictionary<Guid, NodeAssignmentJournalEntry> Entries { get; } = [];
        public List<NodeAssignmentJournalEntry> Upserts { get; } = [];

        public Task<IReadOnlyList<NodeAssignmentJournalEntry>> LoadAsync(CancellationToken cancellationToken)
        {
            sequence.Add("journal-load");
            return Task.FromResult<IReadOnlyList<NodeAssignmentJournalEntry>>([.. Entries.Values]);
        }

        public Task UpsertAsync(NodeAssignmentJournalEntry entry, CancellationToken cancellationToken)
        {
            sequence.Add("journal-upsert");
            Upserts.Add(entry);
            Entries[entry.Assignment.RequestId] = entry;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid requestId, CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRegistry
    {
        public RecordingRegistry(List<string> sequence)
        {
            Inner = new AssignmentProcessRegistry(new RecordingStopInvoker(this, sequence));
        }

        public AssignmentProcessRegistry Inner { get; }
        public TimeSpan? LastCooperativeBudget { get; private set; }
        public TimeSpan? LastTerminationBudget { get; private set; }

        public IDisposable Register(Guid requestId, IAssignmentProcessIsolation isolation)
            => Inner.Register(requestId, isolation);

        private sealed class RecordingStopInvoker(RecordingRegistry owner, List<string> sequence)
            : IAssignmentProcessStopInvoker
        {
            public async Task<AssignmentProcessStopResult> StopAsync(
                IAssignmentProcessIsolation isolation,
                TimeSpan cooperativeBudget,
                TimeSpan terminationBudget,
                CancellationToken cancellationToken)
            {
                sequence.Add("registry-stop");
                owner.LastCooperativeBudget = cooperativeBudget;
                owner.LastTerminationBudget = terminationBudget;
                return await isolation.StopIsolatedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private sealed class RecordingIsolation : IAssignmentProcessIsolation
    {
        private readonly AssignmentProcessIdentity _identity;
        private readonly bool _proven;

        public RecordingIsolation(AssignmentProcessIdentity identity, bool proven = true)
        {
            _identity = identity;
            _proven = proven;
            Identity = identity;
        }

        public AssignmentProcessIdentity? Identity { get; }
        public bool StopCalled { get; private set; }

        public Task<AssignmentProcessStopResult> StopIsolatedAsync(CancellationToken cancellationToken)
        {
            StopCalled = true;
            return Task.FromResult(
                _proven
                    ? AssignmentProcessStopResult.Stopped([_identity])
                    : AssignmentProcessStopResult.Unproven([_identity]));
        }
    }

    private sealed class RecordingAdmission(List<string> sequence) : IRequestAdmissionGate
    {
        private readonly HashSet<Guid> _closed = [];
        public List<Guid> Drained { get; } = [];

        public bool IsAdmissionClosed(Guid requestId) => _closed.Contains(requestId);

        public NodeActivityLease? TryEnterOperation(Guid requestId, string operation) => null;

        public NodeActivityLease? TryAdmitChild(Guid requestId, string childSessionId) => null;

        public NodeActivityLease? TryEnterTerminalization(Guid requestId, string operation) => null;

        public RequestCallbackLease? TryRegisterCallbackSource(Guid requestId, string sessionId) => null;

        public NodeActivityLease TrackProcess(Guid requestId, string description) =>
            throw new NotSupportedException();

        public bool TrySealAdmission(Guid requestId) => true;

        public void UnsealAdmission(Guid requestId)
        {
        }

        public Task<bool> WaitUntilDrainedAsync(
            Guid requestId,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            sequence.Add("admission-drain");
            Drained.Add(requestId);
            return Task.FromResult(true);
        }

        public void CloseAdmission(Guid requestId)
        {
            sequence.Add("admission-close");
            _closed.Add(requestId);
        }

        public Task<QuiescenceOutcome> ProveQuiescenceAsync(
            Guid requestId,
            QuiescenceObservation observation,
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<QuiescenceOutcome>(new QuiescenceOutcome.Uncertain(
                "test",
                new AssignmentQuiescenceProof(false, 0, 0, 0, 0, 0, false, T0)));

        public void CommitTerminalization(Guid requestId)
        {
        }
    }

    private sealed class RecordingSessions(List<string> sequence) : IAssignmentRecoverySessionCanceller
    {
        public List<Guid> Cancelled { get; } = [];

        public Task CancelRootAndChildrenAsync(Guid requestId, CancellationToken cancellationToken)
        {
            sequence.Add("session-cancel");
            Cancelled.Add(requestId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingActivities(List<string> sequence) : IAssignmentRecoveryActivityObserver
    {
        public string? ChildrenUnknown { get; set; }
        public string? OperationsUnknown { get; set; }

        public Task<RecoveryKnownCountMessage> ObserveChildrenAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            sequence.Add("activity-children");
            return Task.FromResult(
                ChildrenUnknown is null
                    ? new RecoveryKnownCountMessage(0, null)
                    : new RecoveryKnownCountMessage(null, ChildrenUnknown));
        }

        public Task<RecoveryKnownCountMessage> ObserveOperationsAsync(
            Guid requestId,
            CancellationToken cancellationToken)
        {
            sequence.Add("activity-operations");
            return Task.FromResult(
                OperationsUnknown is null
                    ? new RecoveryKnownCountMessage(0, null)
                    : new RecoveryKnownCountMessage(null, OperationsUnknown));
        }
    }

    private sealed class RecordingSpool(List<string> sequence) : INodeEventSpool
    {
        public List<NodeEventMessage> Pending { get; } = [];
        public List<IReadOnlyCollection<string>> Deleted { get; } = [];

        public Task AppendAsync(NodeEventMessage message, CancellationToken cancellationToken)
        {
            Pending.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NodeEventMessage>> PeekPendingAsync(int max, CancellationToken cancellationToken)
        {
            sequence.Add("spool-peek");
            return Task.FromResult<IReadOnlyList<NodeEventMessage>>(Pending.Take(max).ToArray());
        }

        public Task<int> CountPendingForRequestAsync(Guid requestId, CancellationToken cancellationToken)
            => Task.FromResult(Pending.Count(evt => evt.RequestId == requestId));

        public Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
        {
            sequence.Add("spool-delete");
            Deleted.Add(eventIds);
            Pending.RemoveAll(evt => eventIds.Contains(evt.EventId));
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingPublisher(List<string> sequence) : IAssignmentRecoveryEventPublisher
    {
        public AssignmentRecoveryEventAck Ack { get; set; } = new([], 0, null);
        public Guid LastRequestId { get; private set; }

        public Task<AssignmentRecoveryEventAck> PublishAsync(
            IReadOnlyList<NodeEventMessage> events,
            CancellationToken cancellationToken)
        {
            sequence.Add("event-publish");
            LastRequestId = events.Select(evt => evt.RequestId).OfType<Guid>().FirstOrDefault();
            return Task.FromResult(Ack);
        }
    }

    private sealed class RecordingInspector(List<string> sequence) : IRepositoryInspector
    {
        public RepositoryBaseline? Baseline { get; set; }
        public Exception? Throw { get; set; }
        public string? LastRoot { get; private set; }

        public Task<RepositoryBaseline> CaptureBaselineAsync(
            string repositoryRoot,
            bool requireCleanStart,
            bool allowUntrackedFiles,
            CancellationToken cancellationToken)
        {
            sequence.Add("repository-inspect");
            LastRoot = repositoryRoot;
            if (Throw is not null)
            {
                throw Throw;
            }

            return Task.FromResult(Baseline!);
        }

        public Task<RepositoryDiffInspection> InspectDiffAsync(
            string repositoryRoot,
            string baseCommit,
            IReadOnlyList<ReservationLeaseInfo> leases,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task DetectExternalChangesAsync(
            string repositoryRoot,
            string baseCommit,
            IReadOnlyList<ReservationLeaseInfo> leases,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingReservations(List<string> sequence) : INodeReservationGateway
    {
        private readonly Dictionary<Guid, ReservationLeaseInfo> _leases = [];
        public List<(Guid LeaseId, string Reason)> Recoveries { get; } = [];

        public void Seed(ReservationLeaseInfo lease) => _leases[lease.LeaseId] = lease;

        public Task<ReservationOperationResult> AcquireAsync(
            Guid projectId,
            Guid requestId,
            string ownerSessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            string reason,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> ExpandAsync(
            Guid leaseId,
            Guid projectId,
            long fencingToken,
            string sessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> ReleaseAsync(
            Guid leaseId,
            Guid projectId,
            string sessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> TransferAsync(
            Guid leaseId,
            string fromSessionId,
            string toSessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ReservationOperationResult> RenewAsync(
            Guid leaseId,
            long fencingToken,
            string sessionId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<MutationAuthorizationResult> AuthorizeAsync(
            Guid leaseId,
            long fencingToken,
            string sessionId,
            string targetPath,
            string operation,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
            Guid projectId,
            bool includeReleased,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReservationLeaseInfo>>([.. _leases.Values]);

        public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
            Guid leaseId,
            string reason,
            CancellationToken cancellationToken)
        {
            sequence.Add("reservation-mark");
            Recoveries.Add((leaseId, reason));
            if (!_leases.TryGetValue(leaseId, out var lease))
            {
                return Task.FromResult(new ReservationOperationResult(
                    null,
                    new GatewayError("not_found", "missing")));
            }

            var marked = lease with { State = "RecoveryRequired" };
            _leases[leaseId] = marked;
            return Task.FromResult(new ReservationOperationResult(marked, null));
        }
    }

    private sealed class RecordingCatalog(List<string> sequence) : IAssignmentRecoveryReservationCatalog
    {
        public Dictionary<Guid, IReadOnlyList<ReservationLeaseInfo>> ByRequest { get; } = [];
        public HashSet<Guid> ThrowFor { get; } = [];

        public Task<IReadOnlyList<ReservationLeaseInfo>> ListForAssignmentAsync(
            Guid projectId,
            Guid requestId,
            CancellationToken cancellationToken)
        {
            sequence.Add("reservation-list");
            if (ThrowFor.Contains(requestId))
            {
                throw new InvalidOperationException("catalog unavailable");
            }

            return Task.FromResult(
                ByRequest.TryGetValue(requestId, out var leases)
                    ? leases
                    : Array.Empty<ReservationLeaseInfo>());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
