using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;
using PiCommandCenter.Node.RuntimeRouting;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Muse model discovery consumes the catalog reader's canonical selectors only: no Muse host is
/// launched, no credentials are read, and nothing the reader did not vouch for reaches the catalog.
/// </summary>
public sealed class MuseRuntimeModelDiscoveryTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "devfleet-muse-discovery", Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task Reports_reader_selectors_as_muse_catalog_in_deterministic_order()
    {
        var reader = new FakeMuseReader(new MuseModelCatalogResult(["muse/llama-b", "muse/llama-a"], null));

        var catalogs = await Discover(reader);

        Assert.Equal(["claude-code", "antigravity", "muse"], catalogs.Select(catalog => catalog.Provider));
        var muse = catalogs.Single(catalog => catalog.Provider == AgentModelSelector.Muse);
        Assert.Null(muse.Error);
        Assert.Equal(
            [
                new RuntimeModelMessage("muse/llama-a", "muse/llama-a", "muse"),
                new RuntimeModelMessage("muse/llama-b", "muse/llama-b", "muse"),
            ],
            muse.Models);
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public async Task Empty_catalog_fails_closed_with_a_stable_error()
    {
        var muse = await DiscoverMuse(new FakeMuseReader(new MuseModelCatalogResult([], null)));

        Assert.Empty(muse.Models);
        Assert.Equal("Muse model discovery returned no models.", muse.Error);
    }

    [Fact]
    public async Task Reader_error_is_preserved_verbatim()
    {
        const string error = "Muse is not logged in on this node. Run `muse login` locally and retry.";

        var muse = await DiscoverMuse(new FakeMuseReader(MuseModelCatalogResult.Failure(error)));

        Assert.Empty(muse.Models);
        Assert.Equal(error, muse.Error);
    }

    [Fact]
    public async Task Duplicate_selectors_collapse_to_one_model()
    {
        var muse = await DiscoverMuse(new FakeMuseReader(
            new MuseModelCatalogResult(["muse/llama-a", " muse/llama-a ", "muse/llama-a"], null)));

        Assert.Null(muse.Error);
        var model = Assert.Single(muse.Models);
        Assert.Equal("muse/llama-a", model.Id);
    }

    [Fact]
    public async Task Malformed_and_foreign_rows_are_dropped_without_reprefixing()
    {
        var muse = await DiscoverMuse(new FakeMuseReader(new MuseModelCatalogResult(
            ["codex/gpt-test", "llama-a", "", "   ", "muse/", "/llama-a", "muse/default", "muse/llama-ok"],
            null)));

        Assert.Null(muse.Error);
        Assert.Equal(["muse/llama-ok"], muse.Models.Select(model => model.Id));
    }

    [Fact]
    public async Task Only_malformed_rows_fails_closed()
    {
        var muse = await DiscoverMuse(new FakeMuseReader(
            new MuseModelCatalogResult(["llama-a", "codex/gpt-test", "muse/default"], null)));

        Assert.Empty(muse.Models);
        Assert.Equal("Muse model discovery returned no models.", muse.Error);
    }

    [Fact]
    public async Task Cancellation_reaches_the_reader_and_aborts_discovery()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new CancellationAwareMuseReader();
        var discovery = Discover(reader, cancellation.Token);
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => discovery);
        Assert.True(reader.LastToken.IsCancellationRequested);
    }

    private async Task<RuntimeModelCatalogMessage> DiscoverMuse(FakeMuseReader reader)
        => (await Discover(reader)).Single(catalog => catalog.Provider == AgentModelSelector.Muse);

    private async Task<IReadOnlyList<RuntimeModelCatalogMessage>> Discover(
        IMuseModelCatalogReader reader,
        CancellationToken cancellationToken = default)
    {
        var worker = new PiWorkerOptions
        {
            NodeExecutable = "node-test",
            WorkerPath = Path.Combine(_root, "runtime", "index.ts"),
            AgentDataDirectory = Path.Combine(_root, "agent-data"),
        };
        using var store = new NodeRuntimeRoutingStore(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }), Options.Create(worker));
        var discovery = new RuntimeModelDiscovery(
            Options.Create(worker),
            Options.Create(new AntigravityOptions { Executable = "agy-test" }),
            store,
            new EmptyModelRunner(),
            reader,
            TimeProvider.System);
        return await discovery.DiscoverAsync(cancellationToken);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class FakeMuseReader(MuseModelCatalogResult result) : IMuseModelCatalogReader
    {
        public int Reads { get; private set; }

        public CancellationToken LastToken { get; private set; }

        public Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
        {
            Reads++;
            LastToken = cancellationToken;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class CancellationAwareMuseReader : IMuseModelCatalogReader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken LastToken { get; private set; }

        public async Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
        {
            LastToken = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Infinite delay completed unexpectedly.");
        }
    }

    /// <summary>Sibling runtimes report nothing so the Muse catalog is the only variable.</summary>
    private sealed class EmptyModelRunner : IRuntimeModelCommandRunner
    {
        public Task<ModelCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            string workingDirectory,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = executable == "node-test" ? "[]" : string.Empty;
            return Task.FromResult(new ModelCommandResult(0, output, string.Empty, false, false));
        }
    }
}
