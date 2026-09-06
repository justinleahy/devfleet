using System.Collections.Concurrent;

using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

public sealed class AssignmentProcessRegistryTests
{
    [Fact]
    public async Task Multiple_handles_snapshot_and_stop_aggregate_proven()
    {
        var registry = new AssignmentProcessRegistry();
        var requestId = Guid.NewGuid();
        var root = FakeIsolation.Proven(11, 1001, "root");
        var child = FakeIsolation.Proven(12, 1002, "child");

        using var rootReg = registry.Register(requestId, root);
        using var childReg = registry.Register(requestId, child);

        var snapshot = registry.Snapshot(requestId);
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, id => id.ProcessId == 11 && id.StartTimeTicks == 1001);
        Assert.Contains(snapshot, id => id.ProcessId == 12 && id.StartTimeTicks == 1002);
        Assert.NotSame(snapshot, registry.Snapshot(requestId));

        var result = await registry.StopAsync(
            requestId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.True(result.Proven);
        Assert.Equal(string.Empty, result.BlockerCode);
        Assert.Equal(1, root.StopCalls);
        Assert.Equal(1, child.StopCalls);
        Assert.Contains(result.DiscoveredProcesses, id => id.ProcessId == 11);
        Assert.Contains(result.DiscoveredProcesses, id => id.ProcessId == 12);
    }

    [Fact]
    public async Task Concurrent_register_stop_and_dispose_same_request_is_safe()
    {
        var entered = 0;
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStops = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = new ConcurrentBag<IAssignmentProcessIsolation>();
        var invoker = new RecordingStopInvoker(async (isolation, _, _, ct) =>
        {
            captured.Add(isolation);
            if (Interlocked.Increment(ref entered) == 1)
            {
                stopEntered.TrySetResult();
            }

            await releaseStops.Task.WaitAsync(ct).ConfigureAwait(false);
            return await isolation.StopIsolatedAsync(ct).ConfigureAwait(false);
        });
        var registry = new AssignmentProcessRegistry(invoker);
        var requestId = Guid.NewGuid();
        var handles = Enumerable.Range(1, 24)
            .Select(i => FakeIsolation.Proven(i, 10_000 + i, "worker"))
            .ToArray();

        var registrations = new IDisposable[handles.Length];
        Parallel.For(0, handles.Length, i =>
        {
            registrations[i] = registry.Register(requestId, handles[i]);
        });

        var stopTask = registry.StopAsync(
            requestId,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        await stopEntered.Task;

        var disposeTask = Task.Run(() =>
        {
            Parallel.ForEach(registrations, registration => registration.Dispose());
        });

        releaseStops.TrySetResult();
        await Task.WhenAll(stopTask, disposeTask);

        var result = await stopTask;
        Assert.Equal(handles.Length, captured.Count);
        Assert.True(result.Proven);
        Assert.Equal(string.Empty, result.BlockerCode);
        Assert.Empty(registry.Snapshot(requestId));
        Assert.All(handles, handle => Assert.Equal(1, handle.StopCalls));
        Assert.All(handles, handle =>
        {
            Assert.Contains(captured, isolation => ReferenceEquals(isolation, handle));
            Assert.Contains(
                result.DiscoveredProcesses,
                id => id.ProcessId == handle.Identity!.ProcessId
                    && id.StartTimeTicks == handle.Identity.StartTimeTicks);
        });
    }

    [Fact]
    public async Task Unregister_removes_handle_from_snapshot_and_stop()
    {
        var registry = new AssignmentProcessRegistry();
        var requestId = Guid.NewGuid();
        var kept = FakeIsolation.Proven(21, 2001, "kept");
        var dropped = FakeIsolation.Proven(22, 2002, "dropped");

        using var keptReg = registry.Register(requestId, kept);
        var droppedReg = registry.Register(requestId, dropped);
        droppedReg.Dispose();
        droppedReg.Dispose();

        var snapshot = registry.Snapshot(requestId);
        Assert.Single(snapshot);
        Assert.Equal(21, snapshot[0].ProcessId);

        var result = await registry.StopAsync(
            requestId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.True(result.Proven);
        Assert.Equal(1, kept.StopCalls);
        Assert.Equal(0, dropped.StopCalls);
    }

    [Fact]
    public async Task Unknown_handle_makes_aggregate_unproven()
    {
        var registry = new AssignmentProcessRegistry();
        var requestId = Guid.NewGuid();
        var known = FakeIsolation.Proven(31, 3001, "known");
        var unknown = FakeIsolation.Unproven(32, 3002, "unknown");

        using var knownReg = registry.Register(requestId, known);
        using var unknownReg = registry.Register(requestId, unknown);

        var result = await registry.StopAsync(
            requestId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.False(result.Proven);
        Assert.Equal(AssignmentProcessStopResult.ProcessStopUnproven, result.BlockerCode);
        Assert.Contains(result.DiscoveredProcesses, id => id.ProcessId == 31);
        Assert.Contains(result.DiscoveredProcesses, id => id.ProcessId == 32);
    }

    [Fact]
    public async Task Escaped_descendant_keeps_process_stop_unproven()
    {
        var registry = new AssignmentProcessRegistry();
        var requestId = Guid.NewGuid();
        var leader = new AssignmentProcessIdentity(41, 4001, 41, 41, "root");
        var escaped = new AssignmentProcessIdentity(42, 4002, 41, 99, "escaped");
        var isolation = new FakeIsolation
        {
            Identity = leader,
            Result = AssignmentProcessStopResult.Unproven([leader, escaped]),
        };

        using var registration = registry.Register(requestId, isolation);
        var result = await registry.StopAsync(
            requestId,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.False(result.Proven);
        Assert.Equal(AssignmentProcessStopResult.ProcessStopUnproven, result.BlockerCode);
        Assert.Contains(result.DiscoveredProcesses, id => id.ProcessId == 41);
        Assert.Contains(result.DiscoveredProcesses, id => id.ProcessId == 42 && id.SessionId == 99);
    }

    [Fact]
    public async Task Stop_does_not_touch_unrelated_request_isolation()
    {
        var registry = new AssignmentProcessRegistry();
        var requestA = Guid.NewGuid();
        var requestB = Guid.NewGuid();
        var isolationA = FakeIsolation.Proven(51, 5001, "a");
        var isolationB = FakeIsolation.Proven(52, 5002, "b");

        using var regA = registry.Register(requestA, isolationA);
        using var regB = registry.Register(requestB, isolationB);

        var result = await registry.StopAsync(
            requestA,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.True(result.Proven);
        Assert.Equal(1, isolationA.StopCalls);
        Assert.Equal(0, isolationB.StopCalls);
        Assert.Single(registry.Snapshot(requestB));
        Assert.DoesNotContain(result.DiscoveredProcesses, id => id.ProcessId == 52);
    }

    [Fact]
    public async Task Empty_registry_stop_is_unproven_not_known_zero()
    {
        var registry = new AssignmentProcessRegistry();
        var result = await registry.StopAsync(
            Guid.NewGuid(),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        Assert.False(result.Proven);
        Assert.Equal(AssignmentProcessStopResult.ProcessStopUnproven, result.BlockerCode);
        Assert.Empty(result.DiscoveredProcesses);
    }

    [Fact]
    public void Duplicate_identity_on_same_request_is_rejected()
    {
        var registry = new AssignmentProcessRegistry();
        var requestId = Guid.NewGuid();
        var first = FakeIsolation.Proven(61, 6001, "one");
        var duplicate = FakeIsolation.Proven(61, 6001, "dup");

        using var registration = registry.Register(requestId, first);
        Assert.Throws<InvalidOperationException>(() => registry.Register(requestId, duplicate));
    }

    [Fact]
    public void Handle_cannot_be_registered_to_two_requests()
    {
        var registry = new AssignmentProcessRegistry();
        var isolation = FakeIsolation.Proven(71, 7001, "shared");
        using var first = registry.Register(Guid.NewGuid(), isolation);
        Assert.Throws<InvalidOperationException>(() => registry.Register(Guid.NewGuid(), isolation));
    }

    [Fact]
    public void Empty_request_id_is_rejected()
    {
        var registry = new AssignmentProcessRegistry();
        var isolation = FakeIsolation.Proven(81, 8001, "bad");
        Assert.Throws<ArgumentException>(() => registry.Register(Guid.Empty, isolation));
        Assert.Throws<ArgumentException>(() => registry.Snapshot(Guid.Empty));
    }

    [Fact]
    public async Task Stop_invoker_receives_cooperative_and_termination_budgets()
    {
        TimeSpan? cooperative = null;
        TimeSpan? termination = null;
        var invoker = new RecordingStopInvoker((isolation, coop, term, ct) =>
        {
            cooperative = coop;
            termination = term;
            return isolation.StopIsolatedAsync(ct);
        });
        var registry = new AssignmentProcessRegistry(invoker);
        var requestId = Guid.NewGuid();
        var isolation = FakeIsolation.Proven(91, 9001, "budget");
        using var registration = registry.Register(requestId, isolation);

        await registry.StopAsync(
            requestId,
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(7),
            CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(3), cooperative);
        Assert.Equal(TimeSpan.FromSeconds(7), termination);
        Assert.Equal(1, isolation.StopCalls);
    }

    private sealed class FakeIsolation : IAssignmentProcessIsolation
    {
        public AssignmentProcessIdentity? Identity { get; init; }

        public AssignmentProcessStopResult Result { get; init; } =
            AssignmentProcessStopResult.Unproven();

        public int StopCalls;

        public Task<AssignmentProcessStopResult> StopIsolatedAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref StopCalls);
            return Task.FromResult(Result);
        }

        public static FakeIsolation Proven(int pid, long start, string scope) =>
            new()
            {
                Identity = new AssignmentProcessIdentity(pid, start, pid, pid, scope),
                Result = AssignmentProcessStopResult.Stopped(
                    [new AssignmentProcessIdentity(pid, start, pid, pid, scope)]),
            };

        public static FakeIsolation Unproven(int pid, long start, string scope) =>
            new()
            {
                Identity = new AssignmentProcessIdentity(pid, start, pid, pid, scope),
                Result = AssignmentProcessStopResult.Unproven(
                    [new AssignmentProcessIdentity(pid, start, pid, pid, scope)]),
            };
    }

    private sealed class RecordingStopInvoker : IAssignmentProcessStopInvoker
    {
        private readonly Func<
            IAssignmentProcessIsolation,
            TimeSpan,
            TimeSpan,
            CancellationToken,
            Task<AssignmentProcessStopResult>> _stop;

        public RecordingStopInvoker(
            Func<
                IAssignmentProcessIsolation,
                TimeSpan,
                TimeSpan,
                CancellationToken,
                Task<AssignmentProcessStopResult>> stop)
        {
            _stop = stop;
        }

        public Task<AssignmentProcessStopResult> StopAsync(
            IAssignmentProcessIsolation isolation,
            TimeSpan cooperativeBudget,
            TimeSpan terminationBudget,
            CancellationToken cancellationToken) =>
            _stop(isolation, cooperativeBudget, terminationBudget, cancellationToken);
    }
}
