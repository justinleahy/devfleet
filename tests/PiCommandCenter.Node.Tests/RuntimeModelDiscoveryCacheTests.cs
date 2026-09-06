using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// The model catalog is a node-hosted background cache: discovery starts immediately when the
/// hosted service starts, refreshes on a non-overlapping five-minute cadence, and readers never
/// invoke provider processes.
/// </summary>
public sealed class RuntimeModelDiscoveryCacheTests
{
    [Fact]
    public async Task Read_before_the_first_refresh_waits_and_never_runs_providers()
    {
        using var fixture = new CatalogFixture();
        using var cancellation = new CancellationTokenSource();

        var read = fixture.Discovery.DiscoverAsync(cancellation.Token);
        await Task.Delay(200);

        Assert.False(read.IsCompleted);
        Assert.Equal(0, fixture.Runner.Calls("node-test"));
        Assert.Equal(0, fixture.Runner.Calls("agy-test"));
        Assert.Equal(0, fixture.Muse.Reads);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task Startup_refreshes_immediately_and_reads_never_run_providers()
    {
        using var fixture = new CatalogFixture();
        await fixture.Discovery.StartAsync(CancellationToken.None);

        await fixture.WaitForWaveAsync(1);

        var first = await fixture.Discovery.DiscoverAsync();
        var second = await fixture.Discovery.DiscoverAsync();

        Assert.Equal(1, fixture.Runner.Calls("node-test"));
        Assert.Equal(1, fixture.Runner.Calls("agy-test"));
        Assert.Equal(1, fixture.Muse.Reads);
        Assert.Equal(
            first.Select(catalog => catalog.Provider),
            second.Select(catalog => catalog.Provider));
    }

    [Fact]
    public async Task Refresh_replaces_the_snapshot_on_the_five_minute_cadence()
    {
        using var fixture = new CatalogFixture();
        await fixture.Discovery.StartAsync(CancellationToken.None);
        var first = await fixture.Discovery.DiscoverAsync();
        Assert.Equal(["antigravity/gemini-test-1"], AntigravityModels(first));

        fixture.Time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await fixture.Discovery.DiscoverAsync();
        Assert.Equal(1, fixture.Runner.Calls("agy-test"));

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.WaitForWaveAsync(2);

        var second = await fixture.Discovery.DiscoverAsync();
        Assert.Equal(["antigravity/gemini-test-2"], AntigravityModels(second));
        Assert.Equal(2, fixture.Runner.Calls("node-test"));
        Assert.Equal(2, fixture.Muse.Reads);
    }

    [Fact]
    public async Task Failed_refresh_preserves_the_prior_catalogs()
    {
        using var fixture = new CatalogFixture();
        await fixture.Discovery.StartAsync(CancellationToken.None);
        var first = await fixture.Discovery.DiscoverAsync();
        Assert.Equal(["antigravity/gemini-test-1"], AntigravityModels(first));

        fixture.Runner.Failure = new InvalidOperationException("agy exploded");
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.WaitForWaveAsync(2);

        var preserved = await fixture.Discovery.DiscoverAsync();
        Assert.Equal(["antigravity/gemini-test-1"], AntigravityModels(preserved));
        Assert.Equal(
            first.Single(catalog => catalog.Provider == AgentModelSelector.Muse).Models,
            preserved.Single(catalog => catalog.Provider == AgentModelSelector.Muse).Models);

        fixture.Runner.Failure = null;
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        await fixture.WaitForWaveAsync(3);

        var recovered = await fixture.Discovery.DiscoverAsync();
        Assert.Equal(["antigravity/gemini-test-3"], AntigravityModels(recovered));
    }

    [Fact]
    public async Task Cached_external_catalogs_do_not_freeze_live_Claude_routes()
    {
        using var fixture = new CatalogFixture();
        await fixture.Discovery.StartAsync(CancellationToken.None);
        await fixture.Discovery.DiscoverAsync();
        var replacement = fixture.Store.Current.RoleRoutes
            .Select(route => route.Role == "reviewer"
                ? new RuntimeRoleRouteMessage(
                    route.Role,
                    [new RuntimeRouteCandidateMessage("claude-code/claude-new")])
                : route)
            .ToArray();

        await fixture.Store.UpdateAsync(new UpdateNodeRuntimeConfigurationMessage(replacement));
        var catalogs = await fixture.Discovery.DiscoverAsync();

        Assert.Contains(
            "claude-code/claude-new",
            catalogs.Single(catalog => catalog.Provider == AgentModelSelector.ClaudeCode)
                .Models.Select(model => model.Id));
        Assert.Equal(1, fixture.Runner.Calls("node-test"));
        Assert.Equal(1, fixture.Runner.Calls("agy-test"));
        Assert.Equal(1, fixture.Muse.Reads);
    }

    private static string[] AntigravityModels(IReadOnlyList<RuntimeModelCatalogMessage> catalogs)
        => catalogs.Single(catalog => catalog.Provider == AgentModelSelector.Antigravity)
            .Models.Select(model => model.Id).ToArray();

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "devfleet-model-cache", Guid.NewGuid().ToString("N"))).FullName;

        public CatalogFixture()
        {
            var worker = new PiWorkerOptions
            {
                NodeExecutable = "node-test",
                WorkerPath = Path.Combine(_root, "runtime", "index.ts"),
                AgentDataDirectory = Path.Combine(_root, "agent-data"),
            };
            Time = new MutableTimeProvider();
            Runner = new CountingModelRunner();
            Muse = new CountingMuseReader();
            Store = new NodeRuntimeRoutingStore(
                Options.Create(new NodeOptions { Id = Guid.NewGuid() }),
                Options.Create(worker));
            Discovery = new RuntimeModelDiscovery(
                Options.Create(worker),
                Options.Create(new AntigravityOptions { Executable = "agy-test" }),
                Store,
                Runner,
                Muse,
                NullLogger<RuntimeModelDiscovery>.Instance,
                Time);
        }

        public RuntimeModelDiscovery Discovery { get; }
        public NodeRuntimeRoutingStore Store { get; }
        public MutableTimeProvider Time { get; }
        public CountingModelRunner Runner { get; }
        public CountingMuseReader Muse { get; }

        public async Task WaitForWaveAsync(int wave)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Runner.Calls("node-test") < wave || Runner.Calls("agy-test") < wave
                || Muse.Reads < wave)
            {
                await Task.Delay(20, timeout.Token);
            }
        }

        public void Dispose()
        {
            Discovery.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Discovery.Dispose();
            Store.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class CountingModelRunner : IRuntimeModelCommandRunner
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public Exception? Failure { get; set; }

        public int Calls(string executable) => _calls.GetValueOrDefault(executable);

        public Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wave = _calls.AddOrUpdate(executable, 1, static (_, count) => count + 1);
            if (Failure is { } failure)
            {
                return Task.FromException<ModelCommandResult>(failure);
            }
            var output = executable == "node-test"
                ? "[]"
                : $"gemini-test-{wave}\tGemini Test {wave}\n";
            return Task.FromResult(new ModelCommandResult(0, output, string.Empty, false, false));
        }
    }

    private sealed class CountingMuseReader : IMuseModelCatalogReader
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _reads);
            return Task.FromResult(new MuseModelCatalogResult(
                ["muse/muse-spark-1.3"],
                [],
                null));
        }
    }

    /// <summary>
    /// Advances time manually; timers created through this provider fire when <see cref="Advance"/>
    /// crosses their due time, so the hosted <see cref="PeriodicTimer"/> cadence is deterministic.
    /// </summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _now;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _now);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan duration)
        {
            List<Action> due;
            lock (_gate)
            {
                _now += duration.Ticks;
                due = _timers
                    .Select(timer => timer.TakeIfDue(_now))
                    .OfType<Action>()
                    .ToList();
            }
            foreach (var fire in due)
            {
                fire();
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            MutableTimeProvider provider,
            TimerCallback callback,
            object? state) : ITimer
        {
            private long _dueTicks = -1;
            private long _periodTicks = -1;

            private void Arm(TimeSpan dueTime, TimeSpan period)
            {
                lock (provider._gate)
                {
                    _dueTicks = dueTime == Timeout.InfiniteTimeSpan
                        ? -1
                        : provider._now + dueTime.Ticks;
                    _periodTicks = period == Timeout.InfiniteTimeSpan ? -1 : period.Ticks;
                }
            }

            public Action? TakeIfDue(long now)
            {
                if (_dueTicks < 0 || _dueTicks > now)
                {
                    return null;
                }
                _dueTicks = _periodTicks < 0 ? -1 : now + _periodTicks;
                return () => callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Arm(dueTime, period);
                return true;
            }

            public bool Change(long dueTime, long period)
                => Change(TimeSpan.FromTicks(dueTime), TimeSpan.FromTicks(period));

            public bool Change(int dueTime, int period)
                => Change(TimeSpan.FromMilliseconds(dueTime), TimeSpan.FromMilliseconds(period));

            public bool Change(uint dueTime, uint period)
                => Change(TimeSpan.FromMilliseconds(dueTime), TimeSpan.FromMilliseconds(period));

            public void Dispose()
            {
                lock (provider._gate)
                {
                    _dueTicks = -1;
                    _periodTicks = -1;
                }
                provider.Remove(this);
            }
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
