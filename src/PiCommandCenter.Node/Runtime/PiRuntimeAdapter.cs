using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Pi implementation of <see cref="IAgentRuntimeAdapter"/> (SPEC §25, §28): launches one worker
/// process per session (<c>node &lt;Pi:WorkerPath&gt;</c>), performs the <c>session.start</c>
/// handshake, and exposes normalized events, input, cancel, and snapshot. The orchestrator's
/// <see cref="AgentStartRequest.SessionId"/> is authoritative. The adapter never touches the workspace.
/// </summary>
public sealed class PiRuntimeAdapter : IAgentRuntimeAdapter
{
    /// <summary>Conventional prefixes for Pi session ids minted by supervisors.</summary>
    public const string RootSessionIdPrefix = "pi-root-";
    public const string ChildSessionIdPrefix = "pi-child-";

    /// <summary>Pi SDK provider prefix that a <c>codex/&lt;id&gt;</c> selector maps onto.</summary>
    internal const string PiCodexProviderPrefix = "openai-codex/";

    /// <summary>
    /// Pi-native model override for a selector: <c>openai-codex/&lt;id&gt;</c>. The provider default
    /// is sent as <c>openai-codex/default</c> so the worker resolves it inside that provider instead
    /// of letting the SDK fall back to any authenticated provider. Rejects non-codex runtimes.
    /// </summary>
    internal static string ResolveModelOverride(AgentModelSelector selector)
    {
        if (selector.Runtime != AgentModelSelector.Codex)
        {
            throw new NotSupportedException(
                $"Pi runtime only accepts '{AgentModelSelector.Codex}/<model>' selectors; got '{selector}'.");
        }

        return PiCodexProviderPrefix + selector.ModelId;
    }

    private readonly PiWorkerOptions _options;
    private readonly IPiWorkerProcessFactory _processFactory;
    private readonly IPiOrchestrationRequestHandler _orchestration;
    private readonly string _nodeId;
    private readonly TimeSpan _heartbeatStaleAfter;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PiRuntimeAdapter> _logger;
    private readonly Application.Git.ITrustedGitService? _gitService;
    private readonly ConcurrentDictionary<string, PiWorkerSession> _sessions = new();

    public PiRuntimeAdapter(
        IOptions<NodeOptions> nodeOptions,
        IOptions<PiWorkerOptions> workerOptions,
        IPiWorkerProcessFactory processFactory,
        IPiOrchestrationRequestHandler orchestration,
        TimeProvider timeProvider,
        ILogger<PiRuntimeAdapter> logger,
        Application.Git.ITrustedGitService? gitService = null)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        ArgumentNullException.ThrowIfNull(workerOptions);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _orchestration = orchestration ?? throw new ArgumentNullException(nameof(orchestration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = workerOptions.Value;
        _heartbeatStaleAfter = TimeSpan.FromSeconds(Math.Max(1, nodeOptions.Value.HeartbeatSeconds) * 3);
        _nodeId = nodeOptions.Value.Id.ToString("D");
        _gitService = gitService;
    }

    public string RuntimeKind => AgentRuntimeKinds.Pi;

    public AgentRuntimeCapabilities Capabilities { get; } = new(
        SupportsStreamingEvents: true,
        SupportsSendInput: true,
        SupportsCancel: true,
        SupportsSnapshot: true,
        SupportsChildSpawn: true,
        SupportsPlanTools: true);

    /// <summary>
    /// Launches the worker process and performs the <c>session.start</c> handshake. Throws when
    /// the process cannot start, the handshake fails, or the worker dies before responding.
    /// </summary>
    public async Task<AgentSessionHandle> StartAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Mode is not (AgentRuntimeMode.Root or AgentRuntimeMode.Child))
        {
            throw new NotSupportedException($"Agent runtime mode '{request.Mode}' is not supported.");
        }

        if (request.Mode == AgentRuntimeMode.Child && request.ParentSessionId is null)
        {
            throw new ArgumentException(
                "A child session requires a parent session id.", nameof(request));
        }

        var model = ResolveModelOverride(request.Model);

        // The orchestrator owns the session id; AgentStartRequest validates it non-empty.
        var sessionId = request.SessionId;
        var identity = new PiOrchestrationContext(
            sessionId,
            _nodeId,
            request.ProjectId.Value.ToString("D"),
            request.RequestId.Value.ToString("D"),
            request.ParentSessionId,
            (type, payload, token) => EmitSessionEventAsync(sessionId, type, payload, token),
            request.WorkingDirectory,
            CreateCheckpointDelegate(request));

        var process = _processFactory.Start(
            _options.NodeExecutable, _options.WorkerPath, request.WorkingDirectory);
        var session = new PiWorkerSession(
            identity,
            process,
            _orchestration,
            TimeSpan.FromSeconds(_options.RequestTimeoutSeconds),
            _timeProvider,
            _logger,
            _heartbeatStaleAfter);

        // Register before the handshake so custom-tool requests racing the start response can
        // already be persisted; removed again below if the handshake fails.
        _sessions[sessionId] = session;
        try
        {
            await session.StartAsync(
                request.WorkingDirectory,
                _options.AgentDataDirectory,
                model,
                systemPrompt: _options.SystemPrompt,
                request.Mode,
                request.ParentSessionId,
                cancellationToken).ConfigureAwait(false);

            // The claimed session immediately receives its work prompt: the worker queues it
            // as the first turn input, so a root starts working without a second nudge.
            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                await session.SendInputAsync(request.Prompt, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            _sessions.TryRemove(sessionId, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "Started Pi {Mode} session {SessionId} (provider {ProviderSessionId}) for request {RequestId}.",
            request.Mode, sessionId, session.ProviderSessionId, request.RequestId.Value);
        return new AgentSessionHandle(
            sessionId,
            session.ProviderSessionId,
            RuntimeKind,
            _timeProvider.GetUtcNow());
    }

    public IAsyncEnumerable<NormalizedAgentEvent> WatchAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(sessionId);
        return session.ReadAllEventsAsync(cancellationToken);
    }

    public async Task SendAsync(
        string sessionId,
        AgentInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var session = RequireSession(sessionId);
        await session.SendInputAsync(input.Text, cancellationToken).ConfigureAwait(false);
    }

    public async Task CancelAsync(string sessionId, CancellationToken cancellationToken)
    {
        var session = RequireSession(sessionId);
        await session.CancelAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<AgentRuntimeSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        var session = RequireSession(sessionId);
        return await session.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes one session gracefully, killing the process tree after a short grace period.</summary>
    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        try
        {
            await session.CloseAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Current stderr tail for diagnostics, when the session is known.</summary>
    public IReadOnlyList<string> GetStderrTail(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.StderrTail : [];

    private Func<PiCheckpointRequest, CancellationToken, Task<PiCheckpointResult>>? CreateCheckpointDelegate(
        AgentStartRequest request)
    {
        if (_gitService is null
            || request.Mode != AgentRuntimeMode.Root
            || !request.CreateRequestCommit)
        {
            return null;
        }

        var branchName = Git.PiRequestGit.RequestBranchName(request.RequestId.Value);
        return async (checkpointRequest, token) =>
        {
            try
            {
                var committed = await _gitService.CreateCheckpointCommitAsync(
                    new Application.Git.CheckpointCommitRequest(
                        request.RequestId.Value,
                        request.WorkingDirectory,
                        string.IsNullOrWhiteSpace(checkpointRequest.BranchName)
                            ? branchName
                            : checkpointRequest.BranchName,
                        checkpointRequest.Message,
                        checkpointRequest.Paths),
                    token).ConfigureAwait(false);
                return PiCheckpointResult.Committed(committed.CommitId, committed.BranchName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex, "Checkpoint commit failed for request {RequestId}.", request.RequestId.Value);
                return PiCheckpointResult.Failure("checkpoint_failed", ex.Message);
            }
        };
    }

    private async Task EmitSessionEventAsync(
        string sessionId,
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        // Orchestration events ride the same normalized channel as worker events so they are
        // persisted exactly once, in sequence, by the supervisor's watch loop.
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            throw new InvalidOperationException(
                $"Session {sessionId} is not running; cannot persist orchestration event '{type}'.");
        }

        await session.EmitOrchestrationEventAsync(type, payload, cancellationToken)
            .ConfigureAwait(false);
    }

    private PiWorkerSession RequireSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new KeyNotFoundException($"Unknown or stopped Pi session '{sessionId}'.");
    }

}
