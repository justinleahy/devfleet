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
/// Proves an execution assignment starts a root session whose lifecycle lands in the durable
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

    private static ExecutionAssignmentMessage MakeAssignment(string repositoryPath) => new(
        RequestId: Guid.NewGuid(),
        ProjectId: Guid.NewGuid(),
        WorkspaceBindingId: Guid.NewGuid(),
        NodeIdSnapshot: Guid.NewGuid(),
        CanonicalRepositoryPathSnapshot: repositoryPath,
        DefaultBranchSnapshot: "main",
        BindingValidationRevisionSnapshot: 1,
        State: "Starting",
        ClaimToken: "token-1",
        AssignedAt: Base,
        LeaseExpiresAt: Base.AddMinutes(5),
        RequestTitle: "Ship the feature",
        RequestPrompt: "Implement and review the feature",
        RequestKind: "Development",
        RequestRiskLevel: "Standard",
        CreateRequestBranch: false,
        CreateRequestCommit: false);

    private (PiRootSessionSupervisor Supervisor, SqliteNodeEventSpool Spool, RecordingRequestHandler Handler)
        CreateWorld(
            string workerScriptName,
            int requestTimeoutSeconds = 30,
            FakeRootSessionTerminalizer? terminalizer = null,
            StubCrash? crashRecovery = null,
            PiCommandCenter.Application.Git.ITrustedGitService? gitService = null,
            IRepositoryInspector? inspector = null)
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
            new FileLogger<PiRuntimeAdapter>(logPath),
            new Quiescence.RequestAdmissionGate(TimeProvider.System));
        var supervisor = new PiRootSessionSupervisor(
            Options.Create(new PiWorkerOptions()),
            Options.Create(new NodeOptions { RequireCleanStart = false }),
            adapter,
            spool,
            inspector ?? new StubInspector(),
            new RequestWorkspaceTracker(),
            crashRecovery ?? new StubCrash(),
            terminalizer ?? new FakeRootSessionTerminalizer(),
            TimeProvider.System,
            new FileLogger<PiRootSessionSupervisor>(logPath),
            gitService);
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
    public async Task An_assignment_starts_a_root_session_through_the_fake_worker()
    {
        var (supervisor, spool, handler) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName);

        var sessionId = await supervisor.StartForAssignmentAsync(
            assignment,
            CancellationToken.None);

        Assert.StartsWith(PiRuntimeAdapter.RootSessionIdPrefix, sessionId);

        var pending = await SpoolAwaitingAsync(spool, events => events.Any(e => e.Type == "turn.started"));

        Assert.All(pending, message => Assert.Equal(assignment.ClaimToken, message.ClaimToken));
        // The spool is the durability boundary: registration first, then every runtime event.
        Assert.Equal("session.registered", pending[0].Type);
        Assert.Equal(0, pending[0].Sequence);
        Assert.Equal(assignment.RequestId, pending[0].RequestId);
        Assert.Equal(assignment.NodeIdSnapshot, pending[0].NodeId);
        Assert.Equal(sessionId, pending[0].SessionId);
        Assert.Contains("codex/gpt-5.6-sol", pending[0].PayloadJson);
        Assert.Contains("fake-provider-session", pending[0].PayloadJson);

        Assert.Contains(pending, e => e.Type == "turn.started");
        Assert.Contains(sessionId, supervisor.ActiveSessionIds);

        // The supervisor never answers custom-tool requests itself; the fake worker sent none.
        Assert.Empty(handler.Requests);
    }
    [Fact]
    public async Task Concurrent_starts_for_one_assignment_share_one_root_session()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, "concurrent-repo")).FullName);

        var sessions = await Task.WhenAll(
            supervisor.StartForAssignmentAsync(assignment, CancellationToken.None),
            supervisor.StartForAssignmentAsync(assignment, CancellationToken.None));

        Assert.Equal(sessions[0], sessions[1]);
        Assert.Single(supervisor.ActiveSessionIds);
        var pending = await SpoolAwaitingAsync(
            spool,
            events => events.Any(e => e.Type == "session.registered"));
        Assert.Single(pending, e => e.Type == "session.registered");
    }


    [Fact]
    public async Task Cancelling_a_root_session_is_terminal_and_idempotent()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, "cancel-repo")).FullName);
        var sessionId = await supervisor.StartForAssignmentAsync(
            assignment,
            CancellationToken.None);

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
        var (supervisor, spool, _) = CreateWorld(
            "fake-pi-worker-hung-cancel.mjs",
            requestTimeoutSeconds: 1);
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, "hung-cancel-repo")).FullName);
        var sessionId = await supervisor.StartForAssignmentAsync(
            assignment,
            CancellationToken.None);

        Assert.True(await supervisor.CancelSessionAsync(sessionId, "operator_cancel"));
        Assert.DoesNotContain(sessionId, supervisor.ActiveSessionIds);
        var pending = await SpoolAwaitingAsync(
            spool,
            events => events.Any(e => e.Type == "session.cancelled"));
        Assert.Single(pending, e => e.Type == "session.cancelled");
    }

    [Fact]
    public async Task A_worker_crash_is_failed_only_after_recovery_handling()
    {
        var trace = new List<string>();
        var crashRecovery = new StubCrash(() => trace.Add("recovery"));
        var terminalizer = new FakeRootSessionTerminalizer
        {
            Failing = () => trace.Add("terminalize"),
        };
        var (supervisor, spool, _) = CreateWorld(
            "fake-pi-worker-crash.mjs",
            terminalizer: terminalizer,
            crashRecovery: crashRecovery);
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName);

        var sessionId = await supervisor.StartForAssignmentAsync(
            assignment,
            CancellationToken.None);

        var pending = await SpoolAwaitingAsync(
            spool, events => events.Any(e => e.Type == "session.closed"));

        Assert.Equal(["recovery", "terminalize"], trace);
        var failure = Assert.Single(terminalizer.Failures);
        Assert.Equal(assignment, failure.Assignment);
        Assert.Equal(sessionId, failure.SessionId);
        Assert.False(string.IsNullOrWhiteSpace(failure.Reason));
        Assert.Contains("3", failure.Reason);
        Assert.Contains(pending, e => e.Type == "session.failed");
        Assert.Contains(pending, e => e.Type == "session.closed");
        Assert.All(pending, message => Assert.Equal(assignment.ClaimToken, message.ClaimToken));
    }

    [Theory]
    [InlineData(RootTerminalizationOutcome.Uncertain)]
    [InlineData(RootTerminalizationOutcome.Rejected)]
    public async Task An_unaccepted_failure_still_preserves_the_session_history(
        RootTerminalizationOutcome outcome)
    {
        var terminalizer = new FakeRootSessionTerminalizer { Outcome = outcome };
        var (supervisor, spool, _) = CreateWorld(
            "fake-pi-worker-crash.mjs",
            terminalizer: terminalizer);
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, $"repo-{outcome}")).FullName);

        await supervisor.StartForAssignmentAsync(assignment, CancellationToken.None);
        await terminalizer.FailureInvoked.Task.WaitAsync(TimeSpan.FromSeconds(20));

        var pending = await SpoolAwaitingAsync(
            spool, events => events.Any(e => e.Type == "session.closed"));
        Assert.Single(terminalizer.Failures);
        Assert.Contains(pending, e => e.Type == "session.failed");
        Assert.Contains(pending, e => e.Type == "session.closed");
    }

    [Fact]
    public async Task An_assignment_whose_repository_snapshot_does_not_exist_never_starts_a_session()
    {
        var (supervisor, spool, _) = CreateWorld("fake-pi-worker.mjs");
        await using var _ = supervisor;
        var assignment = MakeAssignment(Path.Combine(_root, "does-not-exist"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.StartForAssignmentAsync(assignment, CancellationToken.None));

        Assert.Empty(await spool.PeekPendingAsync(100, CancellationToken.None));
        Assert.Empty(supervisor.ActiveSessionIds);
    }

    [Fact]
    public async Task Workspace_preparation_runs_before_baseline_request_branch_and_runtime_start()
    {
        var order = new List<string>();
        var git = new RecordingGitService(order);
        var (supervisor, spool, _) = CreateWorld(
            "fake-pi-worker.mjs",
            gitService: git,
            inspector: new StubInspector(() => order.Add("baseline")));
        await using var _ = supervisor;
        var assignment = MakeAssignment(
            Directory.CreateDirectory(Path.Combine(_root, "prep-repo")).FullName)
            with
        { CreateRequestBranch = true };

        var sessionId = await supervisor.StartForAssignmentAsync(
            assignment,
            CancellationToken.None,
            _ =>
            {
                order.Add("prepared");
                return Task.CompletedTask;
            });

        Assert.Equal(["prepare", "baseline", "branch", "prepared"], order);
        var prepared = Assert.Single(git.Preparations);
        Assert.Equal(assignment.RequestId, prepared.RequestId);
        Assert.Equal(assignment.CanonicalRepositoryPathSnapshot, prepared.RepositoryPath);
        Assert.Equal(assignment.DefaultBranchSnapshot, prepared.DefaultBranch);
        var branch = Assert.Single(git.Branches);
        Assert.Equal(assignment.RequestId, branch.RequestId);
        Assert.StartsWith("request/", branch.BranchName);

        var pending = await SpoolAwaitingAsync(spool, events => events.Any(e => e.Type == "session.registered"));
        var registered = Assert.Single(pending, e => e.Type == "session.registered");
        Assert.Equal(sessionId, registered.SessionId);
        Assert.Contains(branch.BranchName, registered.PayloadJson);
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

    private sealed class RecordingGitService(List<string> order)
        : PiCommandCenter.Application.Git.ITrustedGitService
    {
        public List<PiCommandCenter.Application.Git.WorkspacePreparationRequest> Preparations { get; } = [];
        public List<PiCommandCenter.Application.Git.RequestBranchRequest> Branches { get; } = [];

        public Task<PiCommandCenter.Application.Git.WorkspacePreparation> PrepareWorkspaceAsync(
            PiCommandCenter.Application.Git.WorkspacePreparationRequest request,
            CancellationToken cancellationToken = default)
        {
            order.Add("prepare");
            Preparations.Add(request);
            return Task.FromResult(new PiCommandCenter.Application.Git.WorkspacePreparation(
                request.RepositoryPath, request.DefaultBranch, "baseline-commit"));
        }

        public Task<PiCommandCenter.Application.Git.RequestBranchCreated> CreateRequestBranchAsync(
            PiCommandCenter.Application.Git.RequestBranchRequest request,
            CancellationToken cancellationToken = default)
        {
            order.Add("branch");
            Branches.Add(request);
            return Task.FromResult(new PiCommandCenter.Application.Git.RequestBranchCreated(
                request.BranchName, "base-commit"));
        }

        public Task<PiCommandCenter.Application.Git.CheckpointCommitted> CreateCheckpointCommitAsync(
            PiCommandCenter.Application.Git.CheckpointCommitRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubInspector(Action? onBaseline = null) : IRepositoryInspector
    {
        public Task<RepositoryBaseline> CaptureBaselineAsync(
            string repositoryRoot, bool requireCleanStart, bool allowUntrackedFiles, CancellationToken cancellationToken)
        {
            onBaseline?.Invoke();
            return Task.FromResult(new RepositoryBaseline("main", "abc123", "", true, []));
        }

        public Task<RepositoryDiffInspection> InspectDiffAsync(
            string repositoryRoot, string baseCommit, IReadOnlyList<ReservationLeaseInfo> leases, CancellationToken cancellationToken)
            => Task.FromResult(new RepositoryDiffInspection("main", baseCommit, [], []));

        public Task DetectExternalChangesAsync(
            string repositoryRoot, string baseCommit, IReadOnlyList<ReservationLeaseInfo> leases, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class StubCrash(Action? marked = null) : IRuntimeCrashRecovery
    {
        public Task MarkOwnedLeasesRecoveryRequiredAsync(
            Guid nodeId, Guid projectId, Guid? requestId, string ownerSessionId, string reason, CancellationToken cancellationToken)
        {
            marked?.Invoke();
            return Task.CompletedTask;
        }
    }
}
