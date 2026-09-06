using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Security;

using PiCommandCenter.Node.Repository;

namespace PiCommandCenter.Node;

/// <summary>
/// Root session supervisor: for each execution assignment it starts exactly one restricted Pi worker
/// root session and appends its normalized lifecycle to the durable local spool. Observed root
/// failures are routed through the shared terminalization proof; event projection never directly
/// terminalizes the request, and completions still come only from real runtime activity.
/// </summary>
public sealed class PiRootSessionSupervisor : IRootSessionSupervisor, IAsyncDisposable
{
    private readonly PiWorkerOptions _options;
    private readonly Runtime.PiRuntimeAdapter _adapter;
    private readonly INodeEventSpool _spool;
    private readonly TimeProvider _timeProvider;
    private readonly IRepositoryInspector _repository;
    private readonly RequestWorkspaceTracker _workspace;
    private readonly IRuntimeCrashRecovery _crashRecovery;
    private readonly IRootSessionTerminalizer _terminalizer;
    private readonly NodeOptions _nodeOptions;
    private readonly ILogger<PiRootSessionSupervisor> _logger;
    private readonly Application.Git.ITrustedGitService? _gitService;
    private readonly CancellationTokenSource _disposeCts = new();
    private readonly ConcurrentDictionary<Guid, ActiveRootSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _startGates = new();
    private int _disposed;

    public PiRootSessionSupervisor(
        IOptions<PiWorkerOptions> options,
        IOptions<NodeOptions> nodeOptions,
        Runtime.PiRuntimeAdapter adapter,
        INodeEventSpool spool,
        IRepositoryInspector repository,
        RequestWorkspaceTracker workspace,
        IRuntimeCrashRecovery crashRecovery,
        IRootSessionTerminalizer terminalizer,
        TimeProvider timeProvider,
        ILogger<PiRootSessionSupervisor> logger,
        Application.Git.ITrustedGitService? gitService = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nodeOptions);
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _spool = spool ?? throw new ArgumentNullException(nameof(spool));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
        _crashRecovery = crashRecovery ?? throw new ArgumentNullException(nameof(crashRecovery));
        _terminalizer = terminalizer ?? throw new ArgumentNullException(nameof(terminalizer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _gitService = gitService;
        _nodeOptions = nodeOptions.Value;
    }

    /// <summary>Session ids of all currently running root sessions, for node heartbeats.</summary>
    public IReadOnlyList<string> ActiveSessionIds
        => [.. _sessions.Values.Select(s => s.SessionId)];
    public Guid? FindRequestId(string sessionId)
        => _sessions.Values.FirstOrDefault(
            candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal))
            ?.Assignment.RequestId;

    /// <summary>Starts one restricted root session for the execution assignment and persists its lifecycle.</summary>
    public async Task<string> StartForAssignmentAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? preparationCompleted = null)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        var gate = _startGates.GetOrAdd(assignment.RequestId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(assignment.RequestId, out var active))
            {
                return active.SessionId;
            }

            return await StartForAssignmentCoreAsync(
                assignment,
                cancellationToken,
                preparationCompleted).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string> StartForAssignmentCoreAsync(
        ExecutionAssignmentMessage assignment,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? preparationCompleted)
    {
        ArgumentNullException.ThrowIfNull(assignment);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignment.ClaimToken);
        if (assignment.ClaimToken.Length > NodeAssignmentCredential.MaxClaimTokenLength)
        {
            throw new ArgumentException(
                $"Claim token must not exceed {NodeAssignmentCredential.MaxClaimTokenLength} characters.",
                nameof(assignment.ClaimToken));
        }
        // Supervisor-owned workspace preparation runs before baseline capture, request branch
        // creation, and runtime start: ordinary directories are initialized with one baseline
        // commit, unborn repositories are completed, and committed repositories are only
        // revalidated. Preparation is idempotent, so retries of the same assignment are safe.
        if (_gitService is not null)
        {
            await _gitService.PrepareWorkspaceAsync(
                new Application.Git.WorkspacePreparationRequest(
                    assignment.RequestId,
                    assignment.CanonicalRepositoryPathSnapshot,
                    assignment.DefaultBranchSnapshot),
                cancellationToken).ConfigureAwait(false);
        }


        RepositoryBaseline baseline;
        try
        {
            baseline = await _repository.CaptureBaselineAsync(
                assignment.CanonicalRepositoryPathSnapshot,
                _nodeOptions.RequireCleanStart,
                _nodeOptions.AllowUntrackedFiles,
                cancellationToken).ConfigureAwait(false);
        }
        catch (RepositoryDirtyException ex)
        {
            throw new InvalidOperationException(
                "BLOCKED — repository is dirty at request start: " + string.Join(", ", ex.DirtyPaths),
                ex);
        }

        _workspace.SetBaseline(assignment.RequestId, baseline);
        if (!Directory.Exists(assignment.CanonicalRepositoryPathSnapshot))
        {
            throw new InvalidOperationException(
                $"Assigned repository path '{assignment.CanonicalRepositoryPathSnapshot}' does not exist; refusing to start a root session.");
        }

        var sessionId = $"pi-root-{assignment.RequestId:N}-{Guid.NewGuid():N}";
        var model = AgentModelSelector.Parse(_options.Model);
        var startRequest = new AgentStartRequest(
            sessionId,
            new Domain.ProjectId(assignment.ProjectId),
            new Domain.Requests.WorkRequestId(assignment.RequestId),
            parentSessionId: null,
            agentName: "root",
            role: "root",
            workingDirectory: assignment.CanonicalRepositoryPathSnapshot,
            prompt: BuildPrompt(assignment),
            AgentRuntimeMode.Root,
            model.Value,
            createRequestCommit: assignment.CreateRequestCommit);

        // Supervisor-owned request branch, created exactly once before the worker starts so the
        // agent works on the request branch. No branch, no checkpoint policy: without the flag
        // the request stays on its configured branch and checkpoint tools refuse.
        string? requestBranch = null;
        string? baseCommitId = null;
        if (assignment.CreateRequestBranch && _gitService is not null)
        {
            var branchName = Git.PiRequestGit.RequestBranchName(assignment.RequestId);
            var created = await _gitService.CreateRequestBranchAsync(
                new Application.Git.RequestBranchRequest(
                    assignment.RequestId,
                    assignment.CanonicalRepositoryPathSnapshot,
                    assignment.DefaultBranchSnapshot,
                    branchName),
                cancellationToken).ConfigureAwait(false);
            requestBranch = created.BranchName;
            baseCommitId = created.BaseCommitId;
        }
        if (preparationCompleted is not null)
        {
            await preparationCompleted(cancellationToken).ConfigureAwait(false);
        }

        var handle = await _adapter.StartAsync(startRequest, cancellationToken).ConfigureAwait(false);
        var active = new ActiveRootSession(sessionId, assignment);
        if (!_sessions.TryAdd(assignment.RequestId, active))
        {
            await _adapter.CloseSessionAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Request {assignment.RequestId} already has a running root session.");
        }

        // session.registered first, then every adapter event, in order, durably.
        await AppendAsync(
            active,
            sessionId,
            sequence: 0,
            type: "session.registered",
            payload: new Dictionary<string, object?>
            {
                ["sessionId"] = sessionId,
                ["runtimeKind"] = handle.RuntimeKind,
                ["providerSessionId"] = handle.ProviderSessionId,
                ["agentName"] = "root",
                ["role"] = "root",
                ["mode"] = AgentRuntimeMode.Root.ToString(),
                ["model"] = model.Value,
                ["repositoryPath"] = assignment.CanonicalRepositoryPathSnapshot,
                ["defaultBranch"] = assignment.DefaultBranchSnapshot,
                ["requestTitle"] = assignment.RequestTitle,
                ["requestKind"] = assignment.RequestKind,
                ["riskLevel"] = assignment.RequestRiskLevel,
                ["requestBranch"] = requestBranch,
                ["baseCommitId"] = baseCommitId,
                ["createRequestCommit"] = assignment.CreateRequestCommit,
            },
            cancellationToken).ConfigureAwait(false);
        await AppendAsync(
            active,
            sessionId,
            sequence: 0,
            type: "repository.checkpoint_created",
            payload: new Dictionary<string, object?>
            {
                ["branch"] = baseline.Branch,
                ["baseCommit"] = baseline.BaseCommit,
            },
            cancellationToken).ConfigureAwait(false);

        _ = RunWatchLoopAsync(active);
        _logger.LogInformation(
            "Root session {SessionId} started for request {RequestId}.",
            sessionId,
            assignment.RequestId);
        return sessionId;
    }

    /// <summary>Forwards user input to the active root session of a request, when one exists.</summary>
    public async Task<bool> SendInputAsync(Guid requestId, string text, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(requestId, out var active))
        {
            return false;
        }

        await _adapter.SendAsync(active.SessionId, new AgentInput(text), cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels the active root session of a request, when one exists.</summary>
    public async Task<bool> CancelAsync(Guid requestId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(requestId, out var active))
        {
            return false;
        }

        SecurityAuditLog.Cancellation(_logger, active.SessionId, "operator", "root_cancel");
        try
        {
            await _adapter.CancelAsync(active.SessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Graceful cancellation failed for root {SessionId}; forcing session close.",
                active.SessionId);
        }

        await TerminateForRequestAsync(requestId, "root_cancel", "session.cancelled")
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>Cancels an active root by its public session id.</summary>
    public async Task<bool> CancelSessionAsync(string sessionId, string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        var active = _sessions.Values.FirstOrDefault(
            candidate => string.Equals(candidate.SessionId, sessionId, StringComparison.Ordinal));
        if (active is null)
        {
            return false;
        }

        SecurityAuditLog.Cancellation(_logger, sessionId, "operator", reason);
        try
        {
            await _adapter.CancelAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Graceful cancellation failed for root {SessionId}; forcing session close.",
                sessionId);
        }

        await TerminateForRequestAsync(active.Assignment.RequestId, reason, "session.cancelled")
            .ConfigureAwait(false);
        return true;
    }

    /// <summary>Stops the root session for a request when its assignment is lost or the node shuts down.</summary>
    public Task StopForRequestAsync(Guid requestId, string reason)
        => TerminateForRequestAsync(requestId, reason, "session.closed");

    private async Task TerminateForRequestAsync(Guid requestId, string reason, string eventType)
    {
        if (!_sessions.TryRemove(requestId, out var active))
        {
            return;
        }

        try
        {
            await AppendAsync(
                active,
                active.SessionId,
                active.NextSequence(),
                eventType,
                new Dictionary<string, object?> { ["reason"] = reason },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Failed to append {EventType} for request {RequestId}.", eventType, requestId);
        }

        try
        {
            await _adapter.CloseSessionAsync(active.SessionId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Forced close failed for root {SessionId}; adapter ownership was still removed.", active.SessionId);
        }
    }

    /// <summary>Stops every running root session; used at node shutdown.</summary>
    public async Task StopAllAsync()
    {
        foreach (var requestId in _sessions.Keys)
        {
            await StopForRequestAsync(requestId, "node_shutdown").ConfigureAwait(false);
        }
    }

    private async Task RunWatchLoopAsync(ActiveRootSession active)
    {
        try
        {
            await foreach (var sessionEvent in _adapter
                .WatchAsync(active.SessionId, _disposeCts.Token)
                .ConfigureAwait(false))
            {
                await AppendAsync(
                    active,
                    sessionEvent.SessionId,
                    sessionEvent.Sequence,
                    sessionEvent.Type,
                    sessionEvent.Payload,
                    CancellationToken.None).ConfigureAwait(false);
                if (sessionEvent.Type == "session.failed")
                {
                    var reason = sessionEvent.Payload.TryGetValue("reason", out var value)
                        && !string.IsNullOrWhiteSpace(value?.ToString())
                            ? value.ToString()!
                            : "session.failed";
                    try
                    {
                        await _crashRecovery.MarkOwnedLeasesRecoveryRequiredAsync(
                            active.Assignment.NodeIdSnapshot,
                            active.Assignment.ProjectId,
                            active.Assignment.RequestId,
                            active.SessionId,
                            reason,
                            CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception recoveryEx)
                    {
                        _logger.LogWarning(
                            recoveryEx,
                            "Failed to mark leases recovery-required after root crash {SessionId}.",
                            active.SessionId);
                    }

                    var outcome = await _terminalizer.FailAsync(
                        active.Assignment,
                        active.SessionId,
                        reason,
                        CancellationToken.None).ConfigureAwait(false);
                    if (outcome != RootTerminalizationOutcome.Accepted)
                    {
                        _logger.LogWarning(
                            "Root failure for request {RequestId} was {Outcome}; "
                            + "assignment ownership was retained.",
                            active.Assignment.RequestId,
                            outcome);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Watch loop for session {SessionId} failed.", active.SessionId);
        }
        finally
        {
            try
            {
                await _adapter.CloseSessionAsync(active.SessionId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to drain stopped root session {SessionId}; terminal fence was retained.",
                    active.SessionId);
            }
            finally
            {
                _sessions.TryRemove(active.Assignment.RequestId, out _);
            }
        }
    }

    private async Task AppendAsync(
        ActiveRootSession active,
        string sessionId,
        long sequence,
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        var message = new NodeEventMessage(
            EventId: $"{sessionId}-{sequence}-{type}",
            NodeId: active.Assignment.NodeIdSnapshot,
            ProjectId: active.Assignment.ProjectId,
            RequestId: active.Assignment.RequestId,
            ClaimToken: active.Assignment.ClaimToken,
            SessionId: sessionId,
            Sequence: active.AssignSequence(sequence),
            Type: type,
            OccurredAt: _timeProvider.GetUtcNow(),
            PayloadJson: JsonSerializer.Serialize(payload, JsonOptions));

        // Durable before anything else: replay survives node restarts and control-plane outages.
        await _spool.AppendAsync(message, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug(
            "Spooled event {Type} (seq {Sequence}) for request {RequestId}.",
            type, message.Sequence, active.Assignment.RequestId);
    }

    private static string BuildPrompt(ExecutionAssignmentMessage assignment)
    {
        var prompt = string.IsNullOrWhiteSpace(assignment.RequestPrompt)
            ? assignment.RequestTitle
            : assignment.RequestPrompt;
        return string.IsNullOrWhiteSpace(prompt)
            ? "Inspect the repository and create the plan for this work request."
            : prompt;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAllAsync().ConfigureAwait(false);
        _disposeCts.Cancel();
        _disposeCts.Dispose();
    }

    /// <summary>One running root session: identity, assignment, and supervisor-side sequence gate.</summary>
    private sealed class ActiveRootSession(
        string sessionId,
        ExecutionAssignmentMessage assignment)
    {
        private long _lastSequence = -1;

        public string SessionId { get; } = sessionId;

        public ExecutionAssignmentMessage Assignment { get; } = assignment;

        /// <summary>
        /// Guarantees a strictly increasing sequence in the spool even when worker sequences are
        /// reused across worker restarts.
        /// </summary>
        public long AssignSequence(long candidate)
        {
            var next = Math.Max(candidate, Volatile.Read(ref _lastSequence) + 1);
            var current = Volatile.Read(ref _lastSequence);
            long seen;
            do
            {
                seen = current;
                if (next <= seen)
                {
                    next = seen + 1;
                }
            }
            while ((current = Interlocked.CompareExchange(ref _lastSequence, next, seen)) != seen);
            return next;
        }

        public long NextSequence() => AssignSequence(0);
    }
}
