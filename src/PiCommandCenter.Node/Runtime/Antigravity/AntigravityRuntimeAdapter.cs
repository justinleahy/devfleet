using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Security;


namespace PiCommandCenter.Node.Runtime.Antigravity;

/// <summary>
/// Official <c>agy</c> adapter (SPEC §27): persistent stream-json process, one outstanding
/// prompt, init/conversation capture, normalized step/result events, unknown-event tolerance,
/// graceful stdin close, SIGINT cancel. Always read-only: the process runs inside
/// <see cref="AntigravityReadOnlySandbox"/> and any write authorization is rejected up front.
/// </summary>
public sealed class AntigravityRuntimeAdapter : IAgentRuntimeAdapter
{
    internal static IReadOnlyList<string> BuildLaunchArguments(string? model)
    {
        var arguments = new List<string>
        {
            "--input-format", "stream-json", "--output-format", "stream-json",
        };
        if (model is not null)
        {
            arguments.Add("--model");
            arguments.Add(model);
        }

        return arguments;
    }

    /// <summary>
    /// <c>--model</c> value for a selector: the provider-native id, or null when the selector asks
    /// for the provider default so <c>agy</c> picks its own. Rejects non-antigravity runtimes.
    /// </summary>
    internal static string? ResolveModelArgument(AgentModelSelector selector)
    {
        if (selector.Runtime != AgentModelSelector.Antigravity)
        {
            throw new NotSupportedException(
                $"Antigravity runtime only accepts '{AgentModelSelector.Antigravity}/<model>' selectors; got '{selector}'.");
        }

        return selector.IsProviderDefault ? null : selector.ModelId;
    }

    private readonly AntigravityOptions _options;
    private readonly IAntigravityProcessFactory _processFactory;
    private readonly string _nodeId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AntigravityRuntimeAdapter> _logger;
    private readonly ConcurrentDictionary<string, AntigravitySession> _sessions = new();

    public AntigravityRuntimeAdapter(
        IOptions<NodeOptions> nodeOptions,
        IOptions<AntigravityOptions> options,
        IAntigravityProcessFactory processFactory,
        TimeProvider timeProvider,
        ILogger<AntigravityRuntimeAdapter> logger)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        ArgumentNullException.ThrowIfNull(options);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
        _nodeId = nodeOptions.Value.Id == Guid.Empty
            ? Guid.Empty.ToString("D")
            : nodeOptions.Value.Id.ToString("D");
    }

    public string RuntimeKind => AgentRuntimeKinds.Antigravity;

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

        var process = _processFactory.Start(new AntigravityProcessStartInfo(
            _options.Executable,
            BuildLaunchArguments(model),
            request.WorkingDirectory));

        var session = new AntigravitySession(
            request,
            process,
            _nodeId,
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

    /// <summary>
    /// Antigravity never writes: a reservation grant would imply write intent the sandbox cannot
    /// honour, so the start fails closed instead of silently running read-only.
    /// </summary>
    internal static void RejectWriteAuthorization(AgentStartRequest request)
    {
        if (request.Authorization is not null)
        {
            throw new InvalidOperationException(
                "Antigravity is read-only; a write authorization cannot be honoured "
                + $"(lease {request.Authorization.LeaseId:D}).");
        }
    }

    private AntigravitySession RequireSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new KeyNotFoundException($"Unknown or stopped Antigravity session '{sessionId}'.");
    }

    private sealed class AntigravitySession : IAsyncDisposable
    {
        private readonly AgentStartRequest _request;
        private readonly IAntigravityProcess _process;
        private readonly string _nodeId;
        private readonly AntigravityOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger _logger;
        private readonly Channel<NormalizedAgentEvent> _events = Channel.CreateUnbounded<NormalizedAgentEvent>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = true });
        private readonly ConcurrentQueue<string> _stderrTail = new();
        private readonly SemaphoreSlim _promptGate = new(1, 1);
        private readonly TaskCompletionSource<string?> _init = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly StreamWriter _stdin;

        private long _sequence;
        private int _initCount;
        private int _exitHandled;
        private int _cancelled;
        private bool _turnOpen;
        private bool _failed;
        private bool _authBlocked;
        private Task? _stdoutPump;
        private Task? _stderrPump;


        private bool _closedStdin;
        private string? _lastEventType;
        private string? _currentOperation;

        public AntigravitySession(
            AgentStartRequest request,
            IAntigravityProcess process,
            string nodeId,
            AntigravityOptions options,
            TimeProvider timeProvider,
            ILogger logger)
        {
            _request = request;
            _process = process;
            _nodeId = nodeId;
            _options = options;
            _timeProvider = timeProvider;
            _logger = logger;
            _stdin = new StreamWriter(process.Stdin, new UTF8Encoding(false)) { AutoFlush = true };
            ProcessId = process.Id;
        }

        public int ProcessId { get; }

        public string? ProviderSessionId { get; private set; }

        public IReadOnlyList<string> StderrTail => _stderrTail.ToArray();

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _stdoutPump = PumpStdoutAsync(_lifetime.Token);
            _stderrPump = PumpStderrAsync(_lifetime.Token);
            _ = PumpExitAsync();

            await WritePromptAsync(_request.Prompt, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.StartTimeoutSeconds));
            try
            {
                ProviderSessionId = await _init.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var detail = string.Join(" | ", StderrTail.TakeLast(3));
                throw new TimeoutException(string.IsNullOrEmpty(detail)
                    ? "Antigravity process did not emit init before the start timeout."
                    : $"Antigravity process did not emit init before the start timeout: {detail}");
            }
        }

        public IAsyncEnumerable<NormalizedAgentEvent> ReadAllEventsAsync(CancellationToken cancellationToken)
            => _events.Reader.ReadAllAsync(cancellationToken);

        public async Task SendAsync(string text, CancellationToken cancellationToken)
        {
            await _promptGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (Volatile.Read(ref _turnOpen) && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(15, cancellationToken).ConfigureAwait(false);
                }

                await WritePromptAsync(text, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _promptGate.Release();
            }
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            Volatile.Write(ref _cancelled, 1);
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromSeconds(_options.CancelGraceSeconds));
            await _process.InterruptAsync(grace.Token).ConfigureAwait(false);
            if (!_process.Exited.IsCompleted)
            {
                await _process.TerminateAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public AgentRuntimeSnapshot GetSnapshot()
        {
            var exited = _process.Exited.IsCompleted;
            var cancelled = Volatile.Read(ref _cancelled) == 1;
            AgentLiveness liveness = exited ? AgentLiveness.Exited : AgentLiveness.Online;
            AgentActivity activity = _turnOpen ? AgentActivity.Responding : AgentActivity.Idle;
            AgentWorkState work = _authBlocked
                ? AgentWorkState.Blocked
                : _failed
                    ? AgentWorkState.Failed
                    : cancelled
                        ? AgentWorkState.Cancelled
                        : _turnOpen
                            ? AgentWorkState.Executing
                            : AgentWorkState.Reviewing;
            AgentAttention attention = _authBlocked
                ? AgentAttention.InputRequired
                : _failed
                    ? AgentAttention.Error
                    : AgentAttention.None;
            var reason = _authBlocked
                ? ProviderAuthClassifier.NativeLoginReason(AgentRuntimeKinds.Antigravity)
                : exited
                    ? "agy process exited"
                    : _turnOpen
                        ? "agy turn in flight"
                        : "agy online";

            return new AgentRuntimeSnapshot(
                _request.SessionId,
                AgentRuntimeKinds.Antigravity,
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
            await CloseStdinAsync().ConfigureAwait(false);
            _lifetime.Cancel();
            try
            {
                if (!_process.Exited.IsCompleted)
                {
                    await _process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            await _process.DisposeAsync().ConfigureAwait(false);
            _lifetime.Dispose();
            _promptGate.Dispose();
        }

        private async Task WritePromptAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _turnOpen, true);
            var frame = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["event"] = "user",
                ["message"] = new Dictionary<string, object?> { ["content"] = text },
            });
            await _stdin.WriteLineAsync(frame.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        private async Task CloseStdinAsync()
        {
            if (Volatile.Read(ref _closedStdin))
            {
                return;
            }

            Volatile.Write(ref _closedStdin, true);
            try
            {
                await _stdin.FlushAsync().ConfigureAwait(false);
                _stdin.Close();
            }
            catch (ObjectDisposedException)
            {
            }
            catch (IOException)
            {
            }
        }

        private async Task PumpStdoutAsync(CancellationToken cancellationToken)
        {
            var decoder = new LineDecoder(_options.MaxLineBytes);
            var reader = PipeReader.Create(_process.Stdout);
            try
            {
                while (true)
                {
                    var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    decoder.Append(result.Buffer, out var consumed);
                    foreach (var line in decoder.TakeLines())
                    {
                        HandleLine(line);
                    }

                    reader.AdvanceTo(consumed, result.Buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }

                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await reader.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Antigravity stdout pump failed for {SessionId}.", _request.SessionId);
                await reader.CompleteAsync(ex).ConfigureAwait(false);
            }
        }

        private async Task PumpStderrAsync(CancellationToken cancellationToken)
        {
            using var reader = new StreamReader(_process.Stderr, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    if (line.Length == 0)
                    {
                        continue;
                    }

                    var sanitized = DiagnosticSanitizer.SanitizeLine(line);
                    _stderrTail.Enqueue(sanitized.Length > 4096 ? sanitized[..4096] : sanitized);
                    while (_stderrTail.Count > _options.MaxStderrLines)
                    {
                        _stderrTail.TryDequeue(out _);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Antigravity stderr pump ended for {SessionId}.", _request.SessionId);
            }
        }

        private async Task PumpExitAsync()
        {
            var exitCode = await _process.Exited.ConfigureAwait(false);
            try
            {
                if (_stdoutPump is not null)
                {
                    await _stdoutPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            try
            {
                if (_stderrPump is not null)
                {
                    await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }

            if (Interlocked.Exchange(ref _exitHandled, 1) == 1)
            {
                return;
            }

            Volatile.Write(ref _turnOpen, false);
            if (Volatile.Read(ref _cancelled) == 1)
            {
                Emit("session.cancelled", new Dictionary<string, object?>
                {
                    ["exitCode"] = exitCode,
                    ["reason"] = "SIGINT",
                });
            }
            else if (exitCode != 0)
            {
                var tail = string.Join(" | ", _stderrTail.TakeLast(8));
                if (ProviderAuthClassifier.IsMissing(tail))
                {
                    _authBlocked = true;
                    Emit("session.snapshot", ProviderAuthClassifier.SnapshotPayload(AgentRuntimeKinds.Antigravity, tail));
                }
                else
                {
                    _failed = true;
                    Emit("session.failed", new Dictionary<string, object?>
                    {
                        ["exitCode"] = exitCode,
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

        private void HandleLine(string line)
        {
            JsonElement root;
            try
            {
                using var document = JsonDocument.Parse(line);
                root = document.RootElement.Clone();
            }
            catch (JsonException)
            {
                Emit("runtime.malformed", new Dictionary<string, object?>
                {
                    ["raw"] = DiagnosticSanitizer.SanitizeLine(
                        line.Length > 2048 ? line[..2048] : line,
                        2048),
                });
                return;
            }

            var eventName = GetString(root, "event");
            if (string.IsNullOrEmpty(eventName))
            {
                Emit("runtime.malformed", ToPayload(root, extra: null));
                return;
            }

            CaptureConversation(root);

            if (eventName == "init")
            {
                var count = Interlocked.Increment(ref _initCount);
                CaptureConversation(root.TryGetProperty("init", out var init) ? init : root);
                _init.TrySetResult(ProviderSessionId);
                var payload = ToPayload(root, new Dictionary<string, object?>
                {
                    ["providerSessionId"] = ProviderSessionId,
                    ["processId"] = ProcessId,
                    ["initCount"] = count,
                });
                Emit("session.registered", payload);
                if (count > 1)
                {
                    Emit("runtime.init.duplicate", payload);
                }

                return;
            }

            if (eventName == "step_update")
            {
                HandleStep(root);
                return;
            }

            if (eventName == "result")
            {
                HandleResult(root);
                return;
            }

            Emit(eventName, ToPayload(root, extra: null));
        }

        private void HandleStep(JsonElement root)
        {
            var step = root.TryGetProperty("step_update", out var nested) ? nested : root;
            var stepType = GetString(step, "step_type") ?? GetString(root, "step_type") ?? "unknown";
            var state = GetString(step, "state");
            var textDelta = GetString(step, "text_delta");
            var toolName = GetString(step, "tool_name")
                ?? (step.TryGetProperty("tool_info", out var toolInfo)
                    ? GetString(toolInfo, "name")
                    : null);
            var extra = new Dictionary<string, object?>
            {
                ["step_type"] = stepType,
                ["state"] = state,
                ["text_delta"] = textDelta,
                ["tool"] = toolName,
            };
            if (step.TryGetProperty("subagent_info", out var subagents))
            {
                extra["subagent_info"] = subagents.Clone();
            }

            var payload = ToPayload(root, extra);
            _currentOperation = stepType;

            var type = stepType switch
            {
                "user_input" => "turn.started",
                "agent_response" when !string.IsNullOrEmpty(textDelta) => "message.delta",
                "agent_response" when string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase)
                    => "message.completed",
                "agent_response" => "message.delta",
                "tool" when string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase)
                    && HasToolError(step) => "tool.failed",
                "tool" when string.Equals(state, "DONE", StringComparison.OrdinalIgnoreCase)
                    => "tool.completed",
                "tool" => "tool.started",
                "checkpoint" => "checkpoint",
                _ => stepType,
            };

            if (type == "turn.started")
            {
                Volatile.Write(ref _turnOpen, true);
            }

            Emit(type, payload);
        }

        private void HandleResult(JsonElement root)
        {
            Volatile.Write(ref _turnOpen, false);
            var status = GetString(root, "status") ?? GetNestedString(root, "result", "status");
            var extra = new Dictionary<string, object?>
            {
                ["status"] = status,
                ["response"] = GetString(root, "response") ?? GetNestedString(root, "result", "response"),
            };
            CopyUsage(root, extra);
            var payload = ToPayload(root, extra);

            var type = status?.ToUpperInvariant() switch
            {
                "SUCCESS" => "turn.completed",
                "ERROR" or "INVALID" => "turn.failed",
                "CANCELED" or "CANCELLED" or "INTERRUPTED" => "session.cancelled",
                _ => "result",
            };

            if (type is "turn.failed")
            {
                _failed = true;
            }

            if (type is "session.cancelled")
            {
                Volatile.Write(ref _cancelled, 1);
            }

            Emit(type, payload);
        }

        private void CaptureConversation(JsonElement element)
        {
            var id = GetString(element, "conversation_id")
                ?? (element.TryGetProperty("init", out var init) ? GetString(init, "conversation_id") : null)
                ?? (element.TryGetProperty("result", out var result) ? GetString(result, "conversation_id") : null);
            if (!string.IsNullOrWhiteSpace(id))
            {
                ProviderSessionId = id;
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
                AgentRuntimeKinds.Antigravity,
                type,
                _timeProvider.GetUtcNow(),
                payload);
            _events.Writer.TryWrite(value);
        }

        private static bool HasToolError(JsonElement step)
            => step.TryGetProperty("tool_info", out var info)
                && info.TryGetProperty("error", out var error)
                && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

        private static void CopyUsage(JsonElement root, Dictionary<string, object?> extra)
        {
            if (root.TryGetProperty("usage", out var usage) ||
                (root.TryGetProperty("result", out var result) && result.TryGetProperty("usage", out usage)))
            {
                extra["usage"] = usage.Clone();
            }
        }

        private static Dictionary<string, object?> ToPayload(
            JsonElement root,
            Dictionary<string, object?>? extra)
        {
            var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["raw"] = root.Clone(),
            };
            foreach (var property in root.EnumerateObject())
            {
                payload[property.Name] = property.Value.Clone();
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

        private static string? GetString(JsonElement element, string name)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static string? GetNestedString(JsonElement root, string parent, string child)
            => root.TryGetProperty(parent, out var nested) ? GetString(nested, child) : null;
    }

    private sealed class LineDecoder
    {
        private readonly int _maxBytes;
        private readonly List<string> _ready = [];
        private readonly MemoryStream _partial = new();
        private bool _skipping;

        public LineDecoder(int maxBytes) => _maxBytes = maxBytes;

        public void Append(ReadOnlySequence<byte> buffer, out SequencePosition consumed)
        {
            consumed = buffer.Start;
            foreach (var segment in buffer)
            {
                var span = segment.Span;
                var offset = 0;
                while (offset < span.Length)
                {
                    var relative = span[offset..].IndexOf((byte)'\n');
                    if (relative < 0)
                    {
                        Accept(span[offset..]);
                        offset = span.Length;
                        break;
                    }

                    Accept(span.Slice(offset, relative));
                    FinishLine();
                    offset += relative + 1;
                }
            }

            consumed = buffer.End;
        }

        public List<string> TakeLines()
        {
            var lines = _ready.ToList();
            _ready.Clear();
            return lines;
        }

        private void Accept(ReadOnlySpan<byte> bytes)
        {
            if (_skipping)
            {
                return;
            }

            if (_partial.Length + bytes.Length > _maxBytes)
            {
                _skipping = true;
                _partial.SetLength(0);
                return;
            }

            _partial.Write(bytes);
        }

        private void FinishLine()
        {
            if (_skipping)
            {
                _skipping = false;
                _partial.SetLength(0);
                return;
            }

            if (_partial.Length == 0)
            {
                return;
            }

            _ready.Add(Encoding.UTF8.GetString(_partial.ToArray()));
            _partial.SetLength(0);
        }
    }
}
