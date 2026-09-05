using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Repository;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Supervisor integration tests driven through a fake worker executable
/// (<c>TestData/fake-pi-worker.mjs</c>): no provider network, authentication, or model call.
/// Proves the claimed assignment starts a root session whose lifecycle lands in the durable
/// spool, and that a worker crash is converted into session.failed/session.closed events.
/// </summary>
public class PiRootSessionSupervisorTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 4, 14, 0, 0, TimeSpan.Zero);

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-node-tests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        var debugLog = Path.Combine(_root, "debug.log");
        if (File.Exists(debugLog))
        {
            File.Copy(debugLog, "/tmp/pi-cc-node-tests-debug.log", overwrite: true);
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static RequestClaimMessage MakeClaim(string repositoryPath) => new(
        RequestId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        NodeId: Guid.NewGuid(),
        ClaimToken: "token-1",
        ClaimedAt: Base,
        LeaseExpiresAt: Base.AddMinutes(5),
        RepositoryPath: repositoryPath,
        DefaultBranch: "main",
        Title: "Ship the feature",
        Prompt: "Implement and review the feature",
        Kind: "Development",
        RiskLevel: "Standard",
        CreateRequestBranch: false,
        CreateRequestCommit: false);

    private (PiRootSessionSupervisor Supervisor, SqliteNodeEventSpool Spool, RecordingRequestHandler Handler)
        CreateWorld(string workerScriptName, int requestTimeoutSeconds = 30)
    {
        var spoolPath = Path.Combine(_root, "spool.db");
        var agentData = Path.Combine(_root, "agent-data");
        var logPath = Path.Combine(_root, "debug.log");

        var spool = new SqliteNodeEventSpool(Options.Create(new NodeOptions
        {
            Id = Guid.NewGuid(),
            EventSpoolPath = spoolPath,
        }));
        var handler = new RecordingRequestHandler();
        var adapter = new PiRuntimeAdapter(
            Options.Create(new NodeOptions { Id = Guid.NewGuid(), EventSpoolPath = spoolPath }),
            Options.Create(new PiWorkerOptions
            {
                NodeExecutable = "node",
                WorkerPath = Path.Combine(AppContext.BaseDirectory, "TestData", workerScriptName),
                AgentDataDirectory = agentData,
                RequestTimeoutSeconds = requestTimeoutSeconds,
            }),
            new NodeWorkerProcessFactory(),
            handler,
            TimeProvider.System,
            new FileLogger<PiRuntimeAdapter>(logPath));
        var supervisor = new PiRootSessionSupervisor(
            Options.Create(new PiWorkerOptions()),
            Options.Create(new NodeOptions { RequireCleanStart = false }),
            adapter,
            spool,
            new StubInspector(),
            new RequestWorkspaceTracker(),
            new StubCrash(),
            TimeProvider.System,
            new FileLogger<PiRootSessionSupervisor>(logPath));
        return (supervisor, spool, handler);
    }

    private static async Task<IReadOnlyList<NodeEventMessage>> SpoolAwaitingAsync(
        SqliteNodeEventSpool spool,
        Func<IReadOnlyList<NodeEventMessage>, bool> ready,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.UtcNow < deadline)
        {
            var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
            if (ready(pending))
            {
                return pending;
            }

            await Task.Delay(50);
        }

        return await spool.PeekPendingAsync(100, CancellationToken.None);
    }

    [Fact]
    public async Task A_claimed_assignment_starts_a_root_session_through_the_fake_worker()
    {
        var (supervisor, spool, handler) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var claim = MakeClaim(Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName);

        var sessionId = await supervisor.StartForClaimAsync(claim, CancellationToken.None);

        Assert.StartsWith(PiRuntimeAdapter.RootSessionIdPrefix, sessionId);

        var pending = await SpoolAwaitingAsync(spool, events => events.Any(e => e.Type == "turn.started"));

        // The spool is the durability boundary: registration first, then every runtime event.
        Assert.Equal("session.registered", pending[0].Type);
        Assert.Equal(0, pending[0].Sequence);
        Assert.Equal(claim.RequestId, pending[0].RequestId);
        Assert.Equal(claim.NodeId, pending[0].NodeId);
        Assert.Equal(sessionId, pending[0].SessionId);
        Assert.Contains("codex/default", pending[0].PayloadJson);
        Assert.Contains("fake-provider-session", pending[0].PayloadJson);

        Assert.Contains(pending, e => e.Type == "turn.started");
        Assert.Contains(sessionId, supervisor.ActiveSessionIds);

        // The supervisor never answers custom-tool requests itself; the fake worker sent none.
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Cancelling_a_root_session_is_terminal_and_idempotent()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var claim = MakeClaim(Directory.CreateDirectory(Path.Combine(_root, "cancel-repo")).FullName);
        var sessionId = await supervisor.StartForClaimAsync(claim, CancellationToken.None);

        Assert.True(await supervisor.CancelSessionAsync(sessionId, "operator_cancel"));
        Assert.DoesNotContain(sessionId, supervisor.ActiveSessionIds);
        Assert.False(await supervisor.CancelSessionAsync(sessionId, "duplicate_cancel"));

        var pending = await SpoolAwaitingAsync(
            spool,
            events => events.Any(e => e.Type == "session.cancelled"));
        var cancelled = Assert.Single(pending, e => e.Type == "session.cancelled");
        Assert.Contains("operator_cancel", cancelled.PayloadJson);
    }

    [Fact]
    public async Task Hung_graceful_cancel_still_forces_terminal_close()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker-hung-cancel.mjs", requestTimeoutSeconds: 1);
        await using var _ = supervisor;
        var claim = MakeClaim(Directory.CreateDirectory(Path.Combine(_root, "hung-cancel-repo")).FullName);
        var sessionId = await supervisor.StartForClaimAsync(claim, CancellationToken.None);

        Assert.True(await supervisor.CancelSessionAsync(sessionId, "operator_cancel"));
        Assert.DoesNotContain(sessionId, supervisor.ActiveSessionIds);
        var pending = await SpoolAwaitingAsync(
            spool,
            events => events.Any(e => e.Type == "session.cancelled"));
        Assert.Single(pending, e => e.Type == "session.cancelled");
    }

    [Fact]
    public async Task A_worker_crash_is_synthesized_into_failed_and_closed_events()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker-crash.mjs");
        await using var _ = supervisor;
        var claim = MakeClaim(Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName);

        await supervisor.StartForClaimAsync(claim, CancellationToken.None);

        var pending = await SpoolAwaitingAsync(
            spool, events => events.Any(e => e.Type == "session.closed"));

        Assert.Contains(pending, e => e.Type == "session.failed");
        Assert.Contains(pending, e => e.Type == "session.closed");
        var failed = pending.Single(e => e.Type == "session.failed");
        Assert.Contains("3", failed.PayloadJson); // the fake worker's exit code
    }

    [Fact]
    public async Task A_claim_whose_repository_does_not_exist_never_starts_a_session()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var claim = MakeClaim(Path.Combine(_root, "does-not-exist"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.StartForClaimAsync(claim, CancellationToken.None));

        Assert.Empty(await spool.PeekPendingAsync(100, CancellationToken.None));
        Assert.Empty(supervisor.ActiveSessionIds);
    }

    private sealed class FileLogger<T> : ILogger<T>
    {
        private readonly string _path;

        public FileLogger(string path) => _path = path;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (this)
            {
                File.AppendAllText(_path,
                    $"{DateTime.UtcNow:O} [{logLevel}] {typeof(T).Name}: {formatter(state, exception)} {exception}\n");
            }
        }
    }

    private sealed class RecordingRequestHandler : IPiOrchestrationRequestHandler
    {
        public List<(string RequestType, string SessionId)> Requests { get; } = new();

        public Task<PiToolResponse> HandleAsync(
            PiOrchestrationContext context,
            string requestType,
            System.Text.Json.JsonElement? payload,
            CancellationToken cancellationToken)
        {
            Requests.Add((requestType, context.SessionId));
            return Task.FromResult(PiToolResponse.Success());
        }
    }

    private sealed class StubInspector : IRepositoryInspector
    {
        public Task<RepositoryBaseline> CaptureBaselineAsync(
            string repositoryRoot, bool requireCleanStart, bool allowUntrackedFiles, CancellationToken cancellationToken)
            => Task.FromResult(new RepositoryBaseline("main", "abc123", "", true, []));

        public Task<RepositoryDiffInspection> InspectDiffAsync(
            string repositoryRoot, string baseCommit, IReadOnlyList<ReservationLeaseInfo> leases, CancellationToken cancellationToken)
            => Task.FromResult(new RepositoryDiffInspection("main", baseCommit, [], []));

        public Task DetectExternalChangesAsync(
            string repositoryRoot, string baseCommit, IReadOnlyList<ReservationLeaseInfo> leases, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubCrash : IRuntimeCrashRecovery
    {
        public Task MarkOwnedLeasesRecoveryRequiredAsync(
            Guid nodeId, Guid projectId, Guid? requestId, string ownerSessionId, string reason, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
