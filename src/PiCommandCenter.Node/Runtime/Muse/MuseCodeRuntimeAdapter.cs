using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// Official Muse Code adapter: one read-only <c>muse serve</c> host per DevFleet session,
/// driven over MSP v1 JSON-RPC. Start handshakes, creates the provider session with
/// <c>denyUnmatched</c> approvals, and submits the initial prompt; later input is another
/// <c>turn/start</c>; cancel is <c>turn/cancel</c>; close is <c>view/unsubscribe</c> followed
/// by bounded host termination. Any write authorization is rejected up front.
/// </summary>
public sealed class MuseCodeRuntimeAdapter : IAgentRuntimeAdapter
{
    internal const string LoginReason =
        "Complete Muse Code login locally (muse login). The Command Center does not collect provider credentials.";

    private static readonly Action<ILogger, string, Exception?> LogCancelRejected =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(1, nameof(LogCancelRejected)),
            "Muse turn/cancel was not accepted for {SessionId}; terminating host.");

    private static readonly Action<ILogger, string, Exception?> LogUnsubscribeFailed =
        LoggerMessage.Define<string>(
            LogLevel.Debug,
            new EventId(2, nameof(LogUnsubscribeFailed)),
            "Muse view/unsubscribe did not complete for {SessionId}.");

    internal static IReadOnlyList<string> BuildLaunchArguments() => MuseProtocol.LaunchArguments;

    /// <summary>
    /// <c>session/start.modelId</c> for a selector: the provider-native id, or null when the
    /// selector asks for the provider default so the host picks its own. Rejects non-muse runtimes.
    /// </summary>
    internal static string? ResolveModelArgument(AgentModelSelector selector)
    {
        if (selector.Provider != AgentModelSelector.Muse)
        {
            throw new NotSupportedException(
                $"Muse runtime only accepts '{AgentModelSelector.Muse}/<model>' selectors; got '{selector}'.");
        }

        return selector.IsProviderDefault ? null : selector.ModelId;
    }

    /// <summary>
    /// Muse never writes: a reservation grant would imply write intent the host cannot honour,
    /// so the start fails closed instead of silently running read-only.
    /// </summary>
    internal static void RejectWriteAuthorization(AgentStartRequest request)
    {
        if (request.Authorization is not null)
        {
            throw new InvalidOperationException(
                "Muse Code is read-only; a write authorization cannot be honoured "
                + $"(lease {request.Authorization.LeaseId:D}).");
        }
    }

    /// <summary>Muse/Meta login or API-key failures on top of the shared provider heuristics.</summary>
    internal static bool IsAuthFailure(string? diagnostic)
    {
        if (string.IsNullOrWhiteSpace(diagnostic))
        {
            return false;
        }

        return ProviderAuthClassifier.IsMissing(diagnostic)
               || Contains(diagnostic, "muse login")
               || Contains(diagnostic, "muse auth")
               || Contains(diagnostic, "meta login")
               || Contains(diagnostic, "not signed in")
               || Contains(diagnostic, "login required")
               || Contains(diagnostic, "unauthorized")
               || Contains(diagnostic, "authentication failed")
               || Contains(diagnostic, "api key");

        static bool Contains(string haystack, string needle)
            => haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private readonly MuseCodeOptions _options;
    private readonly IMuseProcessFactory _processFactory;
    private readonly string _nodeId;
    private readonly string _clientVersion;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<MuseCodeRuntimeAdapter> _logger;
    private readonly ConcurrentDictionary<string, MuseSession> _sessions = new();

    public MuseCodeRuntimeAdapter(
        IOptions<NodeOptions> nodeOptions,
        IOptions<MuseCodeOptions> options,
        IMuseProcessFactory processFactory,
        TimeProvider timeProvider,
        ILogger<MuseCodeRuntimeAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        ArgumentNullException.ThrowIfNull(options);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _nodeId = nodeOptions.Value.Id.ToString("D");
        _clientVersion = string.IsNullOrWhiteSpace(nodeOptions.Value.AgentVersion)
            ? "0"
            : nodeOptions.Value.AgentVersion;
    }

    public string RuntimeKind => AgentRuntimeKinds.Muse;

    public AgentRuntimeCapabilities Capabilities { get; } = new(
        SupportsStreamingEvents: true,
        SupportsSendInput: true,
        SupportsCancel: true,
        SupportsSnapshot: true,
        SupportsChildSpawn: false,
        SupportsPlanTools: false);

    public async Task<AgentSessionHandle> StartAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        RejectWriteAuthorization(request);
        var model = ResolveModelArgument(request.Model);

        var process = _processFactory.Start(new MuseProcessStartInfo(
            _options.Executable,
            BuildLaunchArguments(),
            request.WorkingDirectory));

        var session = new MuseSession(
            request,
            process,
            model,
            _nodeId,
            _clientVersion,
            _options,
            _timeProvider,
            _logger);

        if (!_sessions.TryAdd(request.SessionId, session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Session '{request.SessionId}' is already running.");
        }

        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _sessions.TryRemove(request.SessionId, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new AgentSessionHandle(
            request.SessionId,
            session.ProviderSessionId,
            RuntimeKind,
            _timeProvider.GetUtcNow());
    }

    public IAsyncEnumerable<NormalizedAgentEvent> WatchAsync(
        string sessionId,
        CancellationToken cancellationToken)
        => RequireSession(sessionId).ReadAllEventsAsync(cancellationToken);

    public Task SendAsync(string sessionId, AgentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        return RequireSession(sessionId).SendAsync(input.Text, cancellationToken);
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
        => RequireSession(sessionId).CancelAsync(cancellationToken);

    /// <summary>
    /// Unsubscribes from the provider session, then terminates the host within a bounded grace
    /// period. MSP has no session close, so the process boundary is the close.
    /// </summary>
    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
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

    public Task<AgentRuntimeSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(RequireSession(sessionId).GetSnapshot());
    }

    public IReadOnlyList<string> GetStderrTail(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.StderrTail : [];

    public int? GetProcessId(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.ProcessId : null;

    private MuseSession RequireSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new KeyNotFoundException($"Unknown or stopped Muse session '{sessionId}'.");
    }

    private sealed class MuseSession : IAsyncDisposable
    {
        private const int MaxTrackedItems = 4096;

        private readonly AgentStartRequest _request;
        private readonly MuseHostClient _client;
        private readonly string? _model;
        private readonly string _nodeId;
        private readonly string _clientVersion;
        private readonly MuseCodeOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Channel<NormalizedAgentEvent> _events = Channel.CreateUnbounded<NormalizedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        private readonly SemaphoreSlim _commandGate = new(1, 1);
        private readonly Dictionary<string, string> _itemKinds = new(StringComparer.Ordinal);
        private readonly object _itemLock = new();

        private long _sequence;
        private int _exitHandled;
        private int _cancelRequested;
        private int _closing;
        private bool _turnOpen;
        private bool _sessionFailed;
        private bool _turnFailed;
        private bool _turnCancelled;
        private bool _authBlocked;
        private bool _inputPending;
        private readonly object _turnLock = new();
        private string? _currentTurnId;
        private string? _lastSettledTurnId;
        private string? _lastEventType;
        private string? _currentOperation;
        private TaskCompletionSource _turnSettled = NewSettled();

        public MuseSession(
            AgentStartRequest request,
            IMuseProcess process,
            string? model,
            string nodeId,
            string clientVersion,
            MuseCodeOptions options,
            TimeProvider timeProvider,
            ILogger logger)
        {
            _request = request;
            _model = model;
            _nodeId = nodeId;
            _clientVersion = clientVersion;
            _options = options;
            _timeProvider = timeProvider;
            _logger = logger;
            _client = new MuseHostClient(process, options.MaxLineBytes, options.MaxStderrLines, HandleNotification, logger);
            ProcessId = process.Id;
        }

        public int ProcessId { get; }

        public string? ProviderSessionId { get; private set; }

        public IReadOnlyList<string> StderrTail => _client.StderrTail;

        private TimeSpan RequestTimeout => TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _client.Start();
            _ = PumpExitAsync();

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(_options.StartTimeoutSeconds));
            var timeout = Timeout.InfiniteTimeSpan;
            try
            {
                await MuseProtocol.HandshakeAsync(_client, _clientVersion, timeout, _logger, bound.Token)
                    .ConfigureAwait(false);

                var started = await _client.RequestAsync(
                    "session/start",
                    new
                    {
                        commandId = MuseProtocol.NewCommandId(),
                        workspaceRoot = _request.WorkingDirectory,
                        modelId = _model,
                        approvalMode = MuseProtocol.ApprovalMode,
                    },
                    timeout,
                    bound.Token).ConfigureAwait(false);

                if (!MuseProtocol.TryGetObject(started, "session", out var providerSession)
                    || MuseProtocol.GetString(providerSession, "sessionId") is not { Length: > 0 } providerSessionId)
                {
                    throw new MuseProtocolException("Muse host did not return a session id from session/start.");
                }

                ProviderSessionId = providerSessionId;
                Emit("session.registered", new Dictionary<string, object?>
                {
                    ["providerSessionId"] = providerSessionId,
                    ["processId"] = ProcessId,
                    ["modelId"] = MuseProtocol.GetString(providerSession, "modelId"),
                    ["providerId"] = MuseProtocol.GetString(providerSession, "providerId"),
                    ["approvalMode"] = MuseProtocol.ApprovalMode,
                });

                await SubmitTurnAsync(_request.Prompt, timeout, bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(StartFailure("Muse host did not complete session start before the start timeout."));
            }
            catch (MuseProtocolException ex)
            {
                throw new InvalidOperationException(StartFailure(ex.Message), ex);
            }
        }

        public IAsyncEnumerable<NormalizedAgentEvent> ReadAllEventsAsync(CancellationToken cancellationToken)
            => _events.Reader.ReadAllAsync(cancellationToken);

        /// <summary>Later input is another <c>turn/start</c>; the host queues it behind a running turn.</summary>
        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(text);
            await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await SubmitTurnAsync(text, RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _commandGate.Release();
            }
        }

        /// <summary>
        /// <c>turn/cancel</c> for the foreground turn; if the host does not settle it within the
        /// cancel grace period the host is terminated so cancel always lands.
        /// </summary>
        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _cancelRequested, 1);
            if (!Volatile.Read(ref _turnOpen) || ProviderSessionId is null)
            {
                return;
            }

            var settled = _turnSettled.Task;
            var grace = TimeSpan.FromSeconds(_options.CancelGraceSeconds);
            try
            {
                await _client.RequestAsync(
                    "turn/cancel",
                    new
                    {
                        commandId = MuseProtocol.NewCommandId(),
                        sessionId = ProviderSessionId,
                        turnId = _currentTurnId,
                    },
                    grace,
                    cancellationToken).ConfigureAwait(false);
                await settled.WaitAsync(grace, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (MuseProtocolException ex)
            {
                LogCancelRejected(_logger, _request.SessionId, ex);
            }

            if (!_client.Exited.IsCompleted)
            {
                await _client.TerminateAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary><c>view/unsubscribe</c> (best effort), then bounded host termination.</summary>
        public async Task CloseAsync(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _closing, 1);
            if (ProviderSessionId is not null && !_client.Exited.IsCompleted && _client.Fault is null)
            {
                try
                {
                    await _client.RequestAsync(
                        "view/unsubscribe",
                        new { sessionId = ProviderSessionId },
                        TimeSpan.FromSeconds(_options.CancelGraceSeconds),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is MuseProtocolException or TimeoutException)
                {
                    LogUnsubscribeFailed(_logger, _request.SessionId, ex);
                }
            }

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(_options.CancelGraceSeconds));
            try
            {
                await _client.TerminateAsync(bound.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // DisposeAsync applies the unconditional kill.
            }
        }

        public AgentRuntimeSnapshot GetSnapshot()
        {
            var exited = _client.Exited.IsCompleted;
            var turnOpen = Volatile.Read(ref _turnOpen);
            var blocked = _authBlocked || _inputPending;
            var failed = _sessionFailed || _turnFailed;
            var cancelled = _turnCancelled || (turnOpen && Volatile.Read(ref _cancelRequested) == 1);
            AgentLiveness liveness = exited ? AgentLiveness.Exited : AgentLiveness.Online;
            AgentActivity activity = turnOpen && !_inputPending ? AgentActivity.Responding : AgentActivity.Idle;
            AgentWorkState work = blocked
                ? AgentWorkState.Blocked
                : failed
                    ? AgentWorkState.Failed
                    : cancelled
                        ? AgentWorkState.Cancelled
                        : turnOpen
                            ? AgentWorkState.Executing
                            : AgentWorkState.Reviewing;
            AgentAttention attention = blocked
                ? AgentAttention.InputRequired
                : failed
                    ? AgentAttention.Error
                    : AgentAttention.None;
            var reason = _authBlocked
                ? LoginReason
                : _inputPending
                    ? "muse turn is waiting for user input the Command Center cannot supply"
                    : exited
                        ? "muse host exited"
                        : turnOpen
                            ? "muse turn in flight"
                            : "muse online";

            return new AgentRuntimeSnapshot(
                _request.SessionId,
                AgentRuntimeKinds.Muse,
                liveness,
                activity,
                attention,
                work,
                reason,
                _currentOperation ?? _lastEventType,
                ProviderSessionId,
                Volatile.Read(ref _sequence),
                _timeProvider.GetUtcNow());
        }

        public async ValueTask DisposeAsync()
        {
            Volatile.Write(ref _closing, 1);
            await _client.DisposeAsync().ConfigureAwait(false);
            _commandGate.Dispose();
        }

        private async Task SubmitTurnAsync(string text, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var result = await _client.RequestAsync(
                "turn/start",
                new
                {
                    commandId = MuseProtocol.NewCommandId(),
                    sessionId = ProviderSessionId,
                    input = new[] { new { type = "text", text } },
                },
                timeout,
                cancellationToken).ConfigureAwait(false);

            var turnId = MuseProtocol.GetString(result, "turnId");
            var disposition = MuseProtocol.GetString(result, "disposition");
            if (string.Equals(disposition, "started", StringComparison.Ordinal))
            {
                OpenTurn(turnId);
            }

            Emit("turn.submitted", new Dictionary<string, object?>
            {
                ["turnId"] = turnId,
                ["disposition"] = disposition,
            });
        }

        private string StartFailure(string detail)
        {
            var tail = string.Join(" | ", StderrTail.TakeLast(3));
            if (IsAuthFailure(detail) || IsAuthFailure(tail))
            {
                return $"{detail} {LoginReason}";
            }

            return tail.Length == 0 ? detail : $"{detail} ({tail})";
        }

        private async Task PumpExitAsync()
        {
            var exitCode = await _client.Exited.ConfigureAwait(false);
            await _client.AwaitPumpsAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref _exitHandled, 1) == 1)
            {
                return;
            }

            CloseTurn();
            var closing = Volatile.Read(ref _closing) == 1;
            var fault = _client.Fault;
            if (fault is not null)
            {
                _sessionFailed = true;
                Emit("session.failed", new Dictionary<string, object?>
                {
                    ["exitCode"] = exitCode,
                    ["reason"] = $"Muse host protocol fault: {fault}",
                });
            }
            else if (closing)
            {
                // Expected termination after view/unsubscribe; exit code is not a failure signal.
            }
            else if (Volatile.Read(ref _cancelRequested) == 1)
            {
                Emit("session.cancelled", new Dictionary<string, object?>
                {
                    ["exitCode"] = exitCode,
                    ["reason"] = "SIGTERM",
                });
            }
            else if (exitCode != 0)
            {
                var tail = string.Join(" | ", StderrTail.TakeLast(8));
                if (IsAuthFailure(tail))
                {
                    _authBlocked = true;
                    Emit("session.snapshot", AuthSnapshotPayload(tail));
                }
                else
                {
                    _sessionFailed = true;
                    Emit("session.failed", new Dictionary<string, object?>
                    {
                        ["exitCode"] = exitCode,
                        ["reason"] = $"muse host exited with code {exitCode}",
                        ["stderrTail"] = tail,
                    });
                }
            }

            Emit("session.closed", new Dictionary<string, object?>
            {
                ["exitCode"] = exitCode,
            });
            _events.Writer.TryComplete();
        }

        private void HandleNotification(string method, JsonElement parameters)
        {
            var slash = method.IndexOf('/');
            var family = slash < 0 ? method : method[..slash];
            var name = slash < 0 ? string.Empty : method[(slash + 1)..];
            switch (family)
            {
                case "turn":
                    HandleTurn(name, parameters);
                    return;
                case "item":
                    HandleItem(name, parameters);
                    return;
                case "session":
                    Emit(NormalizeSessionMethod(name), ToPayload(method, parameters, extra: null));
                    return;
                case "userInput" when name == "requested":
                    _inputPending = true;
                    Emit("input.requested", ToPayload(method, parameters, extra: null));
                    return;
                case "userInput" when name == "settled":
                    _inputPending = false;
                    Emit("input.settled", ToPayload(method, parameters, extra: null));
                    return;
                case "approval":
                    Emit("approval." + ToSnakeCase(name), ToPayload(method, parameters, extra: null));
                    return;
                case "view" when name == "gap":
                    Emit("runtime.gap", ToPayload(method, parameters, extra: null));
                    return;
                default:
                    Emit("runtime.notification", ToPayload(method, parameters, extra: null));
                    return;
            }
        }

        private void HandleTurn(string name, JsonElement parameters)
        {
            var turnId = MuseProtocol.GetString(parameters, "turnId");
            switch (name)
            {
                case "started":
                    OpenTurn(turnId);
                    Emit("turn.started", ToPayload("turn/started", parameters, extra: null));
                    return;
                case "completed":
                    HandleTurnCompleted(parameters);
                    return;
                case "retryScheduled":
                    Emit("turn.retry_scheduled", ToPayload("turn/retryScheduled", parameters, extra: null));
                    return;
                default:
                    Emit("turn." + ToSnakeCase(name), ToPayload("turn/" + name, parameters, extra: null));
                    return;
            }
        }

        private void HandleTurnCompleted(JsonElement parameters)
        {
            var terminal = MuseProtocol.GetString(parameters, "terminal") ?? "unknown";
            var extra = new Dictionary<string, object?>
            {
                ["terminal"] = terminal,
                ["reason"] = MuseProtocol.GetString(parameters, "reason"),
            };
            string? errorMessage = null;
            if (MuseProtocol.TryGetObject(parameters, "error", out var error))
            {
                errorMessage = MuseProtocol.GetString(error, "message");
                extra["errorKind"] = MuseProtocol.GetString(error, "kind");
                extra["errorMessage"] = DiagnosticSanitizer.Sanitize(errorMessage, 512);
            }

            CloseTurn(MuseProtocol.GetString(parameters, "turnId"));
            _inputPending = false;
            var type = terminal switch
            {
                "completed" => "turn.completed",
                "failed" => "turn.failed",
                "cancelled" => "turn.cancelled",
                _ => "turn.completed",
            };

            if (type == "turn.failed")
            {
                if (IsAuthFailure(errorMessage))
                {
                    _authBlocked = true;
                    Emit("session.snapshot", AuthSnapshotPayload(errorMessage));
                }
                else
                {
                    _turnFailed = true;
                }
            }
            else if (type == "turn.cancelled")
            {
                _turnCancelled = true;
            }

            Emit(type, ToPayload("turn/completed", parameters, extra));
        }

        private void HandleItem(string phase, JsonElement parameters)
        {
            string? itemId;
            string kind;
            string? status = null;
            string? tool = null;
            if (phase == "delta")
            {
                itemId = MuseProtocol.GetString(parameters, "itemId");
                kind = LookupItemKind(itemId) ?? "unknown";
            }
            else
            {
                MuseProtocol.TryGetObject(parameters, "item", out var item);
                itemId = MuseProtocol.GetString(item, "itemId");
                kind = MuseProtocol.GetString(item, "kind") ?? "unknown";
                status = MuseProtocol.GetString(item, "status");
                tool = MuseProtocol.GetString(item, "tool");
                TrackItemKind(itemId, kind, forget: phase == "completed");
            }

            var extra = new Dictionary<string, object?>
            {
                ["itemId"] = itemId,
                ["kind"] = kind,
                ["status"] = status,
                ["tool"] = tool,
            };
            if (phase == "delta")
            {
                extra["delta"] = MuseProtocol.GetString(parameters, "delta");
                extra["field"] = MuseProtocol.GetString(parameters, "field") ?? "text";
            }

            _currentOperation = kind == "toolCall" && tool is not null ? tool : kind;
            var type = kind switch
            {
                "agentMessage" => "message." + phase,
                "reasoning" => "reasoning." + phase,
                "toolCall" when phase == "completed" && !string.Equals(status, "completed", StringComparison.Ordinal)
                    => "tool.failed",
                "toolCall" => "tool." + phase,
                _ => "item." + phase,
            };
            Emit(type, ToPayload("item/" + phase, parameters, extra));
        }

        /// <summary>
        /// Opens the turn unless the host already settled that exact turn — the <c>turn/start</c>
        /// acknowledgement can be observed after the pump has dispatched <c>turn/completed</c>
        /// for an instantly finishing turn, and reopening it would leave a phantom in-flight turn.
        /// </summary>
        private void OpenTurn(string? turnId)
        {
            lock (_turnLock)
            {
                if (turnId is not null)
                {
                    if (string.Equals(turnId, _lastSettledTurnId, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _currentTurnId = turnId;
                }

                if (!Volatile.Read(ref _turnOpen))
                {
                    _turnFailed = false;
                    _turnCancelled = false;
                    _turnSettled = NewSettled();
                    Volatile.Write(ref _turnOpen, true);
                }
            }
        }

        private void CloseTurn(string? settledTurnId = null)
        {
            lock (_turnLock)
            {
                if (settledTurnId is not null)
                {
                    _lastSettledTurnId = settledTurnId;
                }

                Volatile.Write(ref _turnOpen, false);
                _turnSettled.TrySetResult();
            }
        }

        private string? LookupItemKind(string? itemId)
        {
            if (itemId is null)
            {
                return null;
            }

            lock (_itemLock)
            {
                return _itemKinds.TryGetValue(itemId, out var kind) ? kind : null;
            }
        }

        private void TrackItemKind(string? itemId, string kind, bool forget)
        {
            if (itemId is null)
            {
                return;
            }

            lock (_itemLock)
            {
                if (forget)
                {
                    _itemKinds.Remove(itemId);
                    return;
                }

                if (_itemKinds.Count >= MaxTrackedItems && !_itemKinds.ContainsKey(itemId))
                {
                    _itemKinds.Clear();
                }

                _itemKinds[itemId] = kind;
            }
        }

        private void Emit(string type, IReadOnlyDictionary<string, object?> payload)
        {
            _lastEventType = type;
            var sequence = Interlocked.Increment(ref _sequence);
            var value = new NormalizedAgentEvent(
                PiProtocol.Version,
                $"{_request.SessionId}-{sequence}",
                _nodeId,
                _request.ProjectId.Value.ToString("D"),
                _request.RequestId.Value.ToString("D"),
                _request.SessionId,
                _request.ParentSessionId,
                sequence,
                AgentRuntimeKinds.Muse,
                type,
                _timeProvider.GetUtcNow(),
                payload);
            _events.Writer.TryWrite(value);
        }

        private static Dictionary<string, object?> AuthSnapshotPayload(string? diagnostic)
        {
            var payload = ProviderAuthClassifier.SnapshotPayload(AgentRuntimeKinds.Muse, diagnostic);
            payload["statusReason"] = LoginReason;
            payload["reason"] = LoginReason;
            return payload;
        }

        private static string NormalizeSessionMethod(string name)
            => name switch
            {
                "tokenUsage" => "session.usage",
                "contextUsage" => "session.context_usage",
                "modelChanged" => "session.model_changed",
                "approvalModeChanged" => "session.approval_mode_changed",
                "branchChanged" => "session.branch_changed",
                "goalChanged" => "session.goal_changed",
                "todoListChanged" => "session.todo_list_changed",
                _ => "session.notification",
            };

        private static string ToSnakeCase(string camel)
        {
            if (camel.Length == 0)
            {
                return "unknown";
            }

            var builder = new StringBuilder(camel.Length + 4);
            foreach (var character in camel)
            {
                if (char.IsUpper(character))
                {
                    builder.Append('_').Append(char.ToLowerInvariant(character));
                }
                else
                {
                    builder.Append(character);
                }
            }

            return builder.ToString();
        }

        private static Dictionary<string, object?> ToPayload(
            string method,
            JsonElement parameters,
            Dictionary<string, object?>? extra)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["method"] = method,
            };
            if (parameters.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in parameters.EnumerateObject())
                {
                    payload[property.Name] = property.Value.Clone();
                }
            }
            else if (parameters.ValueKind is not JsonValueKind.Undefined)
            {
                payload["params"] = parameters.Clone();
            }

            if (extra is not null)
            {
                foreach (var pair in extra)
                {
                    payload[pair.Key] = pair.Value;
                }
            }

            return payload;
        }

        private static TaskCompletionSource NewSettled()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
