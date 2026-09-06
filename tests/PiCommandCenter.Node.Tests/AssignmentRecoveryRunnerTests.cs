using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Recovery;

namespace PiCommandCenter.Node.Tests;

public class AssignmentRecoveryRunnerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Emits_stages_in_automatic_recovery_order()
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new FakeAssignmentRecoveryRuntime();
        var runner = CreateRunner(runtime, clock);
        var stages = new List<string?>();

        var proof = await runner.RunAsync(
            Command(clock),
            (progress, _) =>
            {
                stages.Add(progress.Stage);
                return Task.CompletedTask;
            });

        Assert.Equal(
            [
                AssignmentRecoveryStages.JournalIntent,
                AssignmentRecoveryStages.CloseAdmission,
                AssignmentRecoveryStages.CooperativeStop,
                AssignmentRecoveryStages.IsolatedProcessStop,
                AssignmentRecoveryStages.DrainOperations,
                AssignmentRecoveryStages.FlushEvents,
                AssignmentRecoveryStages.InspectRepository,
                AssignmentRecoveryStages.ResolveReservations,
            ],
            stages);
        Assert.True(proof.AdmissionClosed);
        Assert.True(proof.Children.IsKnown && proof.Children.Value == 0);
        Assert.True(proof.Processes.IsKnown && proof.Processes.Value == 0);
        Assert.True(proof.PendingEvents.IsKnown && proof.PendingEvents.Value == 0);
        Assert.True(proof.Reservations.IsKnown && proof.Reservations.Value == 0);
        Assert.NotNull(proof.Repository);
        Assert.Equal(1, runtime.JournalCalls);
        Assert.Equal(1, runtime.ResolveReservationCalls);
    }

    [Fact]
    public async Task Hung_runtime_call_hits_deadline_without_known_zero()
    {
        var clock = new FakeTimeProvider(T0);
        var hung = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Journal = async (_, ct) =>
            {
                using var reg = ct.Register(() => hung.TrySetCanceled(ct));
                await hung.Task;
            },
        };
        var runner = CreateRunner(runtime, clock);
        var command = Command(clock, deadline: T0);

        var proof = await runner.RunAsync(command);

        Assert.True(proof.Children.IsUnknown);
        Assert.NotEqual(0, proof.Children.Value.GetValueOrDefault(-1));
        Assert.Equal(RecoveryReasonCodes.ProcessStopUnproven, proof.Children.UnknownReasonCode);
        Assert.True(proof.Processes.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.ProcessStopUnproven, proof.Processes.UnknownReasonCode);
        Assert.Null(proof.Repository);
        Assert.False(proof.Processes.IsKnown);
    }

    [Fact]
    public async Task Unknown_process_inventory_is_never_proven_zero()
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Processes = new AssignmentRecoveryProcessSnapshot(
                new RecoveryKnownCountMessage(null, RecoveryReasonCodes.ProcessStopUnproven),
                [
                    new RecoveryProcessIdentityMessage(4242, T0, "scope", EscapedDescendant: true),
                ]),
        };
        var runner = CreateRunner(runtime, clock);

        var proof = await runner.RunAsync(Command(clock));

        Assert.True(proof.Processes.IsUnknown);
        Assert.Null(proof.Processes.Value);
        Assert.Equal(RecoveryReasonCodes.ProcessStopUnproven, proof.Processes.UnknownReasonCode);
        Assert.Equal(0, runtime.ResolveReservationCalls);
        Assert.True(proof.Reservations.IsUnknown);
        Assert.Equal(RecoveryReasonCodes.ReservationUnresolved, proof.Reservations.UnknownReasonCode);
        Assert.Single(proof.ProcessIdentities);
    }

    [Fact]
    public async Task Pending_events_stay_known_nonzero_and_block()
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Events = new AssignmentRecoveryEventFlushResult(
                new RecoveryKnownCountMessage(3, null),
                EventAcknowledgementPosition: 12,
                EventAcknowledgementUnknownReasonCode: null),
        };
        var runner = CreateRunner(runtime, clock);

        var proof = await runner.RunAsync(Command(clock));

        Assert.True(proof.PendingEvents.IsKnown);
        Assert.Equal(3, proof.PendingEvents.Value);
        Assert.NotEqual(0, proof.PendingEvents.Value);
        Assert.Equal(12, proof.EventAcknowledgementPosition);
        Assert.Equal(1, runtime.ResolveReservationCalls);
    }

    [Fact]
    public async Task Dirty_repository_is_known_and_does_not_become_empty()
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Repository = new RecoveryRepositoryStatusMessage(
                Available: true,
                Head: "abc123",
                Branch: "main",
                IndexSummary: "dirty",
                WorktreeSummary: "modified",
                UntrackedCount: new RecoveryKnownCountMessage(2, null),
                InterruptedOperationIndicators: [],
                ObservedAt: T0),
        };
        var runner = CreateRunner(runtime, clock);

        var proof = await runner.RunAsync(Command(clock));

        Assert.NotNull(proof.Repository);
        Assert.True(proof.Repository.Available);
        Assert.Equal("dirty", proof.Repository.IndexSummary);
        Assert.Equal(2, proof.Repository.UntrackedCount.Value);
        Assert.True(proof.Reservations.IsKnown);
    }

    [Fact]
    public async Task Unborn_repository_is_known_without_head()
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Repository = new RecoveryRepositoryStatusMessage(
                Available: true,
                Head: null,
                Branch: null,
                IndexSummary: "unborn",
                WorktreeSummary: "empty",
                UntrackedCount: new RecoveryKnownCountMessage(0, null),
                InterruptedOperationIndicators: [],
                ObservedAt: T0),
        };
        var runner = CreateRunner(runtime, clock);

        var proof = await runner.RunAsync(Command(clock));

        Assert.NotNull(proof.Repository);
        Assert.True(proof.Repository.Available);
        Assert.Null(proof.Repository.Head);
        Assert.False(string.Equals(
            proof.Repository.IndexSummary,
            "unknown",
            StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unavailable_repository_is_unknown_status_not_clean()
    {
        var clock = new FakeTimeProvider(T0);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Repository = new RecoveryRepositoryStatusMessage(
                Available: false,
                Head: null,
                Branch: null,
                IndexSummary: null,
                WorktreeSummary: null,
                UntrackedCount: new RecoveryKnownCountMessage(null, RecoveryReasonCodes.RepositoryStatusUnknown),
                InterruptedOperationIndicators: [],
                ObservedAt: T0),
        };
        var runner = CreateRunner(runtime, clock);

        var proof = await runner.RunAsync(Command(clock));

        Assert.NotNull(proof.Repository);
        Assert.False(proof.Repository.Available);
        Assert.True(proof.Repository.UntrackedCount.IsUnknown);
        Assert.Null(proof.Repository.UntrackedCount.Value);
    }

    [Fact]
    public async Task Reservations_resolve_only_after_proven_process_stop()
    {
        var clock = new FakeTimeProvider(T0);
        var order = new List<string>();
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            OnStopProcesses = () => order.Add("stop"),
            OnResolveReservations = () => order.Add("reservations"),
        };
        var runner = CreateRunner(runtime, clock);

        await runner.RunAsync(Command(clock));

        Assert.Equal(["stop", "reservations"], order);

        runtime.Processes = new AssignmentRecoveryProcessSnapshot(
            new RecoveryKnownCountMessage(null, RecoveryReasonCodes.ProcessStopUnproven),
            []);
        order.Clear();
        runtime.ResolveReservationCalls = 0;
        var blocked = await runner.RunAsync(Command(clock, attempt: 2));
        Assert.Equal(["stop"], order);
        Assert.Equal(0, runtime.ResolveReservationCalls);
        Assert.True(blocked.Reservations.IsUnknown);
    }

    [Fact]
    public async Task Duplicate_attempt_shares_one_runtime_execution()
    {
        var clock = new FakeTimeProvider(T0);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Journal = async (_, _) =>
            {
                started.TrySetResult();
                await release.Task;
            },
        };
        var runner = CreateRunner(runtime, clock);
        var command = Command(clock);

        var first = runner.RunAsync(command);
        await started.Task;
        var second = runner.RunAsync(command);
        release.TrySetResult();


        var proofs = await Task.WhenAll(first, second);
        Assert.Same(proofs[0], proofs[1]);
        Assert.Equal(1, runtime.JournalCalls);
    }

    [Fact]
    public async Task Different_attempt_waits_for_prior_then_runs()
    {
        var clock = new FakeTimeProvider(T0);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeAssignmentRecoveryRuntime
        {
            Journal = async (command, _) =>
            {
                if (command.Attempt == 1)
                {
                    firstStarted.TrySetResult();
                    await firstRelease.Task;
                }
                else
                {
                    secondStarted.TrySetResult();
                }
            },
        };
        var runner = CreateRunner(runtime, clock);
        var requestId = Guid.NewGuid();
        var first = runner.RunAsync(Command(clock, requestId: requestId, attempt: 1));
        await firstStarted.Task;

        var second = runner.RunAsync(Command(clock, requestId: requestId, attempt: 2));
        await Task.Delay(20);
        Assert.False(secondStarted.Task.IsCompleted);

        firstRelease.TrySetResult();
        await Task.WhenAll(first, second);
        Assert.True(secondStarted.Task.IsCompleted);
        Assert.Equal(2, runtime.JournalCalls);
    }

    private static AssignmentRecoveryRunner CreateRunner(
        FakeAssignmentRecoveryRuntime runtime,
        FakeTimeProvider clock)
    {
        var options = Options.Create(new NodeOptions
        {
            RecoveryCooperativeStopSeconds = 0,
            RecoveryTerminationSeconds = 20,
            RecoveryAttemptSeconds = 60,
        });
        return new AssignmentRecoveryRunner(runtime, options, clock);
    }

    private static RecoverAssignmentCommandMessage Command(
        FakeTimeProvider clock,
        Guid? requestId = null,
        int attempt = 1,
        DateTimeOffset? deadline = null) =>
        new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            attempt,
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            requestId ?? Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            "claim-token",
            BindingRevision: 7,
            deadline ?? clock.GetUtcNow().AddMinutes(1));

    private sealed class FakeAssignmentRecoveryRuntime : IAssignmentRecoveryRuntime
    {
        public int JournalCalls;
        public int ResolveReservationCalls;
        public Func<RecoverAssignmentCommandMessage, CancellationToken, Task>? Journal;
        public AssignmentRecoveryProcessSnapshot Processes { get; set; } =
            new(new RecoveryKnownCountMessage(0, null), []);
        public AssignmentRecoveryEventFlushResult Events { get; set; } =
            new(new RecoveryKnownCountMessage(0, null), 1, null);
        public RecoveryRepositoryStatusMessage Repository { get; set; } =
            new(
                true,
                "head",
                "main",
                "clean",
                "clean",
                new RecoveryKnownCountMessage(0, null),
                [],
                T0);
        public Action? OnStopProcesses { get; set; }
        public Action? OnResolveReservations { get; set; }

        public Task JournalRecoveryIntentAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref JournalCalls);
            return Journal is null ? Task.CompletedTask : Journal(command, cancellationToken);
        }

        public Task CloseAdmissionAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task RequestCooperativeStopAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<AssignmentRecoveryProcessSnapshot> StopIsolatedProcessesAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken)
        {
            OnStopProcesses?.Invoke();
            return Task.FromResult(Processes);
        }

        public Task DrainSupervisedOperationsAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<AssignmentRecoveryEventFlushResult> FlushAcknowledgedEventsAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Events);

        public Task<RecoveryRepositoryStatusMessage> InspectRepositoryAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken) =>
            Task.FromResult(Repository);

        public Task<IReadOnlyList<RecoveryReservationDispositionMessage>> ResolveReservationsAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ResolveReservationCalls);
            OnResolveReservations?.Invoke();
            return Task.FromResult<IReadOnlyList<RecoveryReservationDispositionMessage>>(
                [new RecoveryReservationDispositionMessage(Guid.NewGuid(), "released", null)]);
        }

        public Task<AssignmentRecoveryInventorySnapshot> ObserveInventoryAsync(
            RecoverAssignmentCommandMessage command,
            CancellationToken cancellationToken) =>
            Task.FromResult(new AssignmentRecoveryInventorySnapshot(
                new RecoveryKnownCountMessage(0, null),
                new RecoveryKnownCountMessage(0, null),
                Processes.Processes,
                Events.PendingEvents,
                new RecoveryKnownCountMessage(0, null)));
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private readonly object _gate = new();
        private DateTimeOffset _now = start;
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.Schedule(_now, dueTime, period);
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            ManualTimer[] timers;
            DateTimeOffset now;
            lock (_gate)
            {
                _now += by;
                now = _now;
                timers = [.. _timers];
            }

            foreach (var timer in timers)
            {
                timer.FireIfDue(now);
            }
        }

        private sealed class ManualTimer(
            FakeTimeProvider provider,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _next;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public void Schedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
            {
                _period = period;
                _next = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (provider._gate)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    Schedule(provider._now, dueTime, period);
                    return true;
                }
            }

            public void FireIfDue(DateTimeOffset now)
            {
                bool fire;
                lock (provider._gate)
                {
                    fire = !_disposed && _next is { } next && next <= now;
                    if (fire)
                    {
                        _next = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
                    }
                }

                if (fire)
                {
                    callback(state);
                }
            }

            public void Dispose()
            {
                lock (provider._gate)
                {
                    _disposed = true;
                    provider._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
