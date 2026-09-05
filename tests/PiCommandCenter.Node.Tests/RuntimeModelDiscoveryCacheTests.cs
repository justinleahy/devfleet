using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

namespace PiCommandCenter.Node.Tests;

public sealed class RuntimeModelDiscoveryCacheTests
{
    [Fact]
    public async Task Reuses_external_catalogs_until_five_minutes_then_refreshes()
    {
        using var fixture = new CatalogFixture();

        await fixture.Discovery.DiscoverAsync();
        fixture.Time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        await fixture.Discovery.DiscoverAsync();

        Assert.Equal(1, fixture.Runner.Calls("node-test"));
        Assert.Equal(1, fixture.Runner.Calls("agy-test"));
        Assert.Equal(1, fixture.Muse.Reads);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Discovery.DiscoverAsync();

        Assert.Equal(2, fixture.Runner.Calls("node-test"));
        Assert.Equal(2, fixture.Runner.Calls("agy-test"));
        Assert.Equal(2, fixture.Muse.Reads);
    }

    [Fact]
    public async Task Concurrent_cache_misses_share_one_discovery_wave()
    {
        var runner = new BlockingModelRunner();
        using var fixture = new CatalogFixture(runner);

        var first = fixture.Discovery.DiscoverAsync();
        await runner.AllCommandsStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var second = fixture.Discovery.DiscoverAsync();
        runner.Release();

        await Task.WhenAll(first, second);

        Assert.Equal(1, runner.Calls("node-test"));
        Assert.Equal(1, runner.Calls("agy-test"));
        Assert.Equal(1, fixture.Muse.Reads);
    }

    [Fact]
    public async Task Cached_external_catalogs_do_not_freeze_live_Claude_routes()
    {
        using var fixture = new CatalogFixture();
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

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), "devfleet-model-cache", Guid.NewGuid().ToString("N"))).FullName;

        public CatalogFixture(IRuntimeModelCommandRunner? runner = null)
        {
            var worker = new PiWorkerOptions
            {
                NodeExecutable = "node-test",
                WorkerPath = Path.Combine(_root, "runtime", "index.ts"),
                AgentDataDirectory = Path.Combine(_root, "agent-data"),
            };
            Time = new MutableTimeProvider();
            Runner = runner as CountingModelRunner ?? new CountingModelRunner();
            Muse = new CountingMuseReader();
            Store = new NodeRuntimeRoutingStore(
                Options.Create(new NodeOptions { Id = Guid.NewGuid() }),
                Options.Create(worker));
            Discovery = new RuntimeModelDiscovery(
                Options.Create(worker),
                Options.Create(new AntigravityOptions { Executable = "agy-test" }),
                Store,
                runner ?? Runner,
                Muse,
                Time);
        }

        public RuntimeModelDiscovery Discovery { get; }
        public NodeRuntimeRoutingStore Store { get; }
        public MutableTimeProvider Time { get; }
        public CountingModelRunner Runner { get; }
        public CountingMuseReader Muse { get; }

        public void Dispose()
        {
            Discovery.Dispose();
            Store.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    private class CountingModelRunner : IRuntimeModelCommandRunner
    {
        private readonly ConcurrentDictionary<string, int> _calls = new(StringComparer.Ordinal);

        public int Calls(string executable) => _calls.GetValueOrDefault(executable);

        public virtual Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _calls.AddOrUpdate(executable, 1, static (_, count) => count + 1);
            return Task.FromResult(Result(executable));
        }

        protected void Record(string executable) =>
            _calls.AddOrUpdate(executable, 1, static (_, count) => count + 1);

        protected static ModelCommandResult Result(string executable) => new(
            0,
            executable == "node-test" ? "[]" : "gemini-test\tGemini Test\n",
            string.Empty,
            false,
            false);
    }

    private sealed class BlockingModelRunner : CountingModelRunner
    {
        private readonly TaskCompletionSource _allCommandsStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public Task AllCommandsStarted => _allCommandsStarted.Task;

        public void Release() => _release.TrySetResult();

        public override async Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            Record(executable);
            if (Interlocked.Increment(ref _started) == 2)
            {
                _allCommandsStarted.TrySetResult();
            }
            await _release.Task.WaitAsync(cancellationToken);
            return Result(executable);
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
            return Task.FromResult(new MuseModelCatalogResult(["muse/muse-spark-1.3"], null));
        }
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Volatile.Read(ref _timestamp);

        public void Advance(TimeSpan duration) => Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
