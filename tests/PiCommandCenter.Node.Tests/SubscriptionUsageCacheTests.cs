using Microsoft.Extensions.Logging.Abstractions;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class SubscriptionUsageCacheTests
{
    private static readonly Guid NodeId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public async Task FirstReadWaitsForInitialRefreshAndLaterReadsDoNotProbe()
    {
        var pending = new TaskCompletionSource<NodeSubscriptionUsageMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new FakeProbe((_, _) => pending.Task);
        using var time = new ManualTimeProvider();
        using var cache = Create(probe, time);

        await cache.StartAsync(CancellationToken.None);
        var read = cache.GetAsync();

        Assert.False(read.IsCompleted);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAsync<TaskCanceledException>(() => cache.GetAsync(cancelled.Token));

        var snapshot = Snapshot("first");
        pending.SetResult(snapshot);
        Assert.Same(snapshot, await read);
        Assert.Same(snapshot, await cache.GetAsync());
        Assert.Equal(1, probe.CallCount);

        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CadenceReplacesSuccessfulSnapshotAndFailureKeepsLastSuccess()
    {
        var secondFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Snapshot("first");
        var third = Snapshot("third");
        var probe = new FakeProbe((call, _) => call switch
        {
            1 => Task.FromResult(first),
            2 => Fail(secondFinished),
            3 => Complete(third, thirdFinished),
            _ => throw new InvalidOperationException("Unexpected probe call."),
        });
        using var time = new ManualTimeProvider();
        using var cache = Create(probe, time);

        await cache.StartAsync(CancellationToken.None);
        Assert.Same(first, await cache.GetAsync());
        Assert.Equal(SubscriptionUsageCache.RefreshInterval, time.DueTime);
        Assert.Equal(SubscriptionUsageCache.RefreshInterval, time.Period);

        time.Tick();
        await secondFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(first, await cache.GetAsync());

        time.Tick();
        await thirdFinished.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Same(third, await WaitForSnapshotAsync(cache, third));
        Assert.Equal(3, probe.CallCount);

        await cache.StopAsync(CancellationToken.None);
    }

    private static async Task<NodeSubscriptionUsageMessage> WaitForSnapshotAsync(
        SubscriptionUsageCache cache,
        NodeSubscriptionUsageMessage expected)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var snapshot = await cache.GetAsync(timeout.Token);
            if (ReferenceEquals(snapshot, expected))
            {
                return snapshot;
            }

            await Task.Yield();
        }
    }

    private static SubscriptionUsageCache Create(
        IRuntimeSubscriptionUsageProbe probe,
        TimeProvider timeProvider) =>
        new(probe, timeProvider, NullLogger<SubscriptionUsageCache>.Instance);

    private static NodeSubscriptionUsageMessage Snapshot(string provider) =>
        new(NodeId, [
            new ProviderSubscriptionUsageMessage(
                provider,
                SubscriptionUsageStatuses.Unavailable,
                Authenticated: null,
                PlanLabel: null,
                Version: null,
                Windows: [],
                ObservedAt: DateTimeOffset.UnixEpoch,
                Source: "test",
                Diagnostic: "unavailable")
        ]);

    private static async Task<NodeSubscriptionUsageMessage> Fail(TaskCompletionSource finished)
    {
        finished.SetResult();
        await Task.Yield();
        throw new InvalidOperationException("SECRET provider failure");
    }

    private static Task<NodeSubscriptionUsageMessage> Complete(
        NodeSubscriptionUsageMessage snapshot,
        TaskCompletionSource finished)
    {
        finished.SetResult();
        return Task.FromResult(snapshot);
    }

    private sealed class FakeProbe(
        Func<int, CancellationToken, Task<NodeSubscriptionUsageMessage>> handler)
        : IRuntimeSubscriptionUsageProbe
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<NodeSubscriptionUsageMessage> GetAsync(
            CancellationToken cancellationToken = default) =>
            handler(Interlocked.Increment(ref _callCount), cancellationToken);
    }

    private sealed class ManualTimeProvider : TimeProvider, IDisposable
    {
        private ManualTimer? _timer;

        public TimeSpan DueTime { get; private set; }
        public TimeSpan Period { get; private set; }

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            DueTime = dueTime;
            Period = period;
            return _timer = new ManualTimer(callback, state);
        }

        public void Tick() => Assert.IsType<ManualTimer>(_timer).Fire();
        public void Dispose() => _timer?.Dispose();


        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (!_disposed)
                {
                    callback(state);
                }
            }
        }
    }
}
