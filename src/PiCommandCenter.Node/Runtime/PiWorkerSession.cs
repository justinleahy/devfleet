using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// One worker process bound to one agent session: writes strict NDJSON requests to stdin,
/// parses stdout frames only, correlates responses by message id, converts <c>event</c>
/// frames to <see cref="NormalizedAgentEvent"/>s on an async channel, drains stderr into a
/// bounded log buffer, routes custom-tool <c>request</c> frames through the injected
/// <see cref="IPiOrchestrationRequestHandler"/>, and synthesizes
/// <c>session.failed</c>/<c>session.closed</c> when the process crashes.
/// </summary>
public sealed class PiWorkerSession : IAsyncDisposable
{
    private readonly PiOrchestrationContext _identity;
    private readonly IPiWorkerProcess _process;
    private readonly IPiOrchestrationRequestHandler _orchestration;
    private readonly TimeSpan _requestTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _heartbeatStaleAfter;
    private readonly ILogger _logger;

    private readonly Channel<NormalizedAgentEvent> _events =
        Channel.CreateUnbounded<NormalizedAgentEvent>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = true,
        });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<PiEnvelope>> _pending = new();
    private readonly ConcurrentDictionary<long, Task> _workerCallbacks = new();
    private readonly ConcurrentQueue<string> _stderrTail = new();
    internal const int MaxStderrLines = 200;
    private readonly CancellationTokenSource _disposeCts = new();
    private Task _stdoutPump = Task.CompletedTask;

    private long _lastSequenceSeen;
    private long _nextMessageId;
    private long _nextWorkerCallbackId;
    private DateTimeOffset? _lastHeartbeatAt;
    private int _disconnectedEmitted;
    private bool _isStreaming;
    private string? _lastEventType;
    private string? _providerSessionId;
    private bool _closedGracefully;
    private bool _failed;
    private int _exitHandled;

    public PiWorkerSession(
        PiOrchestrationContext identity,
        IPiWorkerProcess process,
        IPiOrchestrationRequestHandler orchestration,
        TimeSpan requestTimeout,
        TimeProvider timeProvider,
        ILogger logger,
        TimeSpan? heartbeatStaleAfter = null)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _process = process ?? throw new ArgumentNullException(nameof(process));
        _orchestration = orchestration ?? throw new ArgumentNullException(nameof(orchestration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (requestTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        }

        _requestTimeout = requestTimeout;
        _heartbeatStaleAfter = heartbeatStaleAfter is { } stale && stale > TimeSpan.Zero
            ? stale
            : TimeSpan.FromSeconds(30);
    }

    public string SessionId => _identity.SessionId;

    public string? ProviderSessionId => _providerSessionId;

    public DateTimeOffset? LastHeartbeatAt => _lastHeartbeatAt;

    /// <summary>Normalized events emitted by the worker plus node-synthesized lifecycle events.</summary>
    public ChannelReader<NormalizedAgentEvent> Events => _events.Reader;

    /// <summary>Last lines of stderr diagnostics, oldest first, bounded to the most recent.</summary>
    public IReadOnlyList<string> StderrTail => _stderrTail.ToArray();

    /// <summary>
    /// Sends <c>session.start</c> and waits for the correlated response. Must be called once
    /// before any other protocol operation; frame pumps start immediately so early events are
    /// not lost.
    /// </summary>
    public async Task StartAsync(
        string workingDirectory,
        string agentDataDirectory,
        string? model,
        string? systemPrompt,
        AgentRuntimeMode mode,
        string? parentSessionId,
        CancellationToken cancellationToken)
    {
        if (mode == AgentRuntimeMode.Child && string.IsNullOrWhiteSpace(parentSessionId))
        {
            throw new ArgumentException(
                "A child session start requires a parent session id.", nameof(parentSessionId));
        }

        _stdoutPump = StartPumps();

        var payload = new Dictionary<string, object?>
        {
            ["cwd"] = workingDirectory,
            ["agentDir"] = agentDataDirectory,
            ["mode"] = mode is AgentRuntimeMode.Child ? "child" : "root",
        };
        if (!string.IsNullOrWhiteSpace(model))
        {
            payload["model"] = model;
        }

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            payload["systemPrompt"] = systemPrompt;
        }

        if (mode is AgentRuntimeMode.Child)
        {
            payload["parentSessionId"] = parentSessionId;
        }

        var response = await RequestAsync("session.start", payload, cancellationToken)
            .ConfigureAwait(false);
        if (response.Payload is JsonElement body
            && body.ValueKind == JsonValueKind.Object
            && body.TryGetProperty("sdkSessionId", out var sdkSessionId)
            && sdkSessionId.ValueKind == JsonValueKind.String)
        {
            _providerSessionId = sdkSessionId.GetString();
        }
    }

    /// <summary>Streams normalized events until the session ends.</summary>
    public IAsyncEnumerable<NormalizedAgentEvent> ReadAllEventsAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Sends <c>session.input</c>; the worker acknowledges immediately and queues the prompt.</summary>
    public Task SendInputAsync(string text, CancellationToken cancellationToken)
        => RequestAsync("session.input", new Dictionary<string, object?> { ["text"] = text },
                cancellationToken)
            .AwaitCompletion();

    /// <summary>Sends <c>session.cancel</c> to abort the running turn.</summary>
    public Task CancelAsync(CancellationToken cancellationToken)
        => RequestAsync("session.cancel", null, cancellationToken).AwaitCompletion();

    /// <summary>Builds a snapshot from a live <c>session.snapshot</c> round trip plus local tracking.</summary>
    public async Task<AgentRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var exited = _process.Exited.IsCompleted;
        var disconnected = !exited && IsHeartbeatStale();
        AgentLiveness liveness = exited
            ? AgentLiveness.Exited
            : disconnected
                ? AgentLiveness.Disconnected
                : AgentLiveness.Online;
        var reason = exited
            ? "worker process exited"
            : disconnected
                ? "Last heartbeat exceeded the disconnect threshold"
                : "worker online";

        if (!exited && !disconnected)
        {
            try
            {
                var response = await RequestAsync("session.snapshot", null, cancellationToken)
                    .ConfigureAwait(false);
                if (response.Payload is JsonElement body && body.ValueKind == JsonValueKind.Object)
                {
                    _isStreaming = body.TryGetProperty("isStreaming", out var streaming)
                        && streaming.ValueKind == JsonValueKind.True;
                    if (body.TryGetProperty("seq", out var seq) && seq.ValueKind == JsonValueKind.Number
                        && seq.TryGetInt64(out var lastSeq))
                    {
                        UpdateLastSequence(lastSeq);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Snapshot request for session {SessionId} failed.", SessionId);
                liveness = AgentLiveness.Disconnected;
                reason = "snapshot unavailable: " + ex.Message;
                await EmitDisconnectedAsync(reason).ConfigureAwait(false);
            }
        }
        else if (disconnected)
        {
            await EmitDisconnectedAsync(reason).ConfigureAwait(false);
        }

        var activity = _failed
            ? AgentActivity.Idle
            : liveness == AgentLiveness.Disconnected
                ? AgentActivity.Responding
                : _isStreaming
                    ? AgentActivity.Responding
                    : _lastEventType is "turn.completed"
                        ? AgentActivity.Idle
                        : AgentActivity.Reasoning;
        var workState = _failed
            ? AgentWorkState.Failed
            : _isStreaming
                ? AgentWorkState.Executing
                : AgentWorkState.Starting;

        return new AgentRuntimeSnapshot(
            SessionId,
            AgentRuntimeKinds.Pi,
            liveness,
            activity,
            _failed ? AgentAttention.Error : AgentAttention.None,
            workState,
            reason,
            _lastEventType,
            _providerSessionId,
            _lastSequenceSeen,
            _lastHeartbeatAt);
    }

    /// <summary>
    /// Closes the session gracefully via <c>goodbye</c>; falls back to killing the process tree
    /// when the worker does not exit in time.
    /// </summary>
    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_process.Exited.IsCompleted)
        {
            return;
        }

        try
        {
            await RequestAsync("goodbye", null, cancellationToken).ConfigureAwait(false);
            _closedGracefully = true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Graceful goodbye for session {SessionId} failed; killing.", SessionId);
        }

        var grace = Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None);
        if (await Task.WhenAny(_process.Exited, grace).ConfigureAwait(false) != _process.Exited)
        {
            await _process.KillTreeAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops callback dispatch and waits for every already-dispatched worker request. Returns
    /// false when the bounded drain cannot be proven.
    /// </summary>
    public async Task<bool> DrainCallbacksAsync(CancellationToken cancellationToken)
    {
        _disposeCts.Cancel();
        try
        {
            await DrainCallbacksCoreAsync()
                .WaitAsync(_requestTimeout, _timeProvider, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException)
        {
            _logger.LogWarning(
                ex,
                "Callback drain for Pi session {SessionId} was not proven within {Timeout}.",
                SessionId,
                _requestTimeout);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Callback drain for Pi session {SessionId} failed.",
                SessionId);
            return false;
        }
    }

    private async Task DrainCallbacksCoreAsync()
    {
        await _stdoutPump.ConfigureAwait(false);
        await Task.WhenAll(_workerCallbacks.Values).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        FailEvents("session disposed");
        await _process.DisposeAsync().ConfigureAwait(false);
        _disposeCts.Dispose();
    }

    private Task StartPumps()
    {
        var stdout = PumpStdoutAsync(_disposeCts.Token);
        var stderr = PumpStderrAsync(_disposeCts.Token);
        var exit = PumpExitAsync();
        var heartbeat = PumpHeartbeatWatchAsync(_disposeCts.Token);
        _ = Task.WhenAll(stdout, stderr, exit, heartbeat);
        return stdout;
    }

    private bool IsHeartbeatStale()
    {
        var last = _lastHeartbeatAt;
        if (last is null)
        {
            return false;
        }

        return _timeProvider.GetUtcNow() - last.Value > _heartbeatStaleAfter;
    }

    private async Task PumpHeartbeatWatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _timeProvider);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_failed || _closedGracefully || _process.Exited.IsCompleted)
                {
                    return;
                }

                if (IsHeartbeatStale())
                {
                    await EmitDisconnectedAsync("Last heartbeat exceeded the disconnect threshold")
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private Task EmitDisconnectedAsync(string reason)
    {
        if (Interlocked.Exchange(ref _disconnectedEmitted, 1) != 0)
        {
            return Task.CompletedTask;
        }

        return EmitSyntheticAsync(
            "session.disconnected",
            new Dictionary<string, object?> { ["reason"] = reason });
    }

    private async Task PumpStdoutAsync(CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(_process.Stdout);
        var decoder = new PiFrameDecoder();
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var consumed = buffer.Start;

                try
                {
                    var remaining = buffer;
                    while (true)
                    {
                        if (remaining.PositionOf((byte)'\n') is not { } newline)
                        {
                            break;
                        }

                        HandleFrame(PiProtocol.Decode(remaining.Slice(0, newline).ToArray()));
                        consumed = remaining.GetPosition(1, newline);
                        remaining = remaining.Slice(consumed);
                    }
                }
                catch (PiFrameException ex)
                {
                    // Strict framing: a bad frame never takes the protocol stream down. Bytes of
                    // the bad frame stay buffered and are skipped on the next read boundary.
                    _logger.LogWarning(
                        "Discarding bad protocol frame from session {SessionId}: {Error} (buffered={Length})",
                        SessionId, ex.Message, buffer.Length);
                }

                reader.AdvanceTo(consumed, buffer.End);
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
            _logger.LogWarning(ex, "Protocol stdout pump for session {SessionId} failed.", SessionId);
            await reader.CompleteAsync(ex).ConfigureAwait(false);
        }
    }


    private async Task PumpStderrAsync(CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(_process.Stderr);
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var line = Encoding.UTF8.GetString(result.Buffer);
                reader.AdvanceTo(result.Buffer.End);
                foreach (var piece in line.Split('\n'))
                {
                    if (piece.Length == 0)
                    {
                        continue;
                    }

                    var sanitized = DiagnosticSanitizer.SanitizeLine(piece);
                    _stderrTail.Enqueue(sanitized);
                    while (_stderrTail.Count > MaxStderrLines)
                    {
                        _stderrTail.TryDequeue(out _);
                    }

                    _logger.LogInformation(
                        "Pi worker {SessionId} stderr: {Line}", SessionId, sanitized);
                }

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
            _logger.LogDebug(ex, "Stderr pump for session {SessionId} ended.", SessionId);
            await reader.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task PumpExitAsync()
    {
        var exitCode = await _process.Exited.ConfigureAwait(false);
        FailPendingRequests($"Pi worker exited with code {exitCode}.");
        if (_closedGracefully)
        {
            FailEvents(null);
            return;
        }

        var tail = DiagnosticSanitizer.Sanitize(
            string.Join(" | ", _stderrTail.TakeLast(8)),
            2048);
        if (ProviderAuthClassifier.IsMissing(tail))
        {
            await EmitSyntheticAsync(
                "session.snapshot",
                ProviderAuthClassifier.SnapshotPayload(AgentRuntimeKinds.Pi, tail)).ConfigureAwait(false);
            await EmitSyntheticAsync("session.closed", new Dictionary<string, object?>
            {
                ["reason"] = "provider_auth_required",
            }).ConfigureAwait(false);
            FailEvents(null);
            return;
        }

        _failed = true;
        await EmitSyntheticAsync(
            "session.failed",
            new Dictionary<string, object?>
            {
                ["reason"] = $"Pi worker exited unexpectedly with code {exitCode}.",
                ["exitCode"] = exitCode,
                ["stderrTail"] = tail,
            }).ConfigureAwait(false);
        await EmitSyntheticAsync("session.closed", new Dictionary<string, object?>
        {
            ["reason"] = "worker_crashed",
        }).ConfigureAwait(false);
        FailEvents(null);
    }

    private void HandleFrame(PiEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case PiFrameKinds.Response:
                HandleResponse(envelope);
                break;
            case PiFrameKinds.Event:
                HandleEvent(envelope);
                break;
            case PiFrameKinds.Request:
                TrackWorkerRequest(envelope);
                break;
            case PiFrameKinds.Heartbeat:
                _lastHeartbeatAt = _timeProvider.GetUtcNow();
                break;
            case PiFrameKinds.Hello:
                _logger.LogDebug("Pi worker {SessionId} sent hello.", SessionId);
                break;
            case PiFrameKinds.Goodbye:
                _logger.LogInformation("Pi worker {SessionId} sent goodbye.", SessionId);
                break;
        }
    }

    private void TrackWorkerRequest(PiEnvelope envelope)
    {
        var callbackId = Interlocked.Increment(ref _nextWorkerCallbackId);
        var dispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = HandleTrackedWorkerRequestAsync(
            callbackId,
            envelope,
            dispatch.Task);
        if (!_workerCallbacks.TryAdd(callbackId, callback))
        {
            throw new InvalidOperationException($"Duplicate worker callback id {callbackId}.");
        }

        dispatch.SetResult();
    }

    private async Task HandleTrackedWorkerRequestAsync(
        long callbackId,
        PiEnvelope envelope,
        Task dispatch)
    {
        await dispatch.ConfigureAwait(false);
        try
        {
            await HandleWorkerRequestAsync(envelope).ConfigureAwait(false);
        }
        finally
        {
            _workerCallbacks.TryRemove(callbackId, out _);
        }
    }

    private void HandleResponse(PiEnvelope envelope)
    {
        if (_pending.TryRemove(envelope.MessageId, out var pending))
        {
            pending.TrySetResult(envelope);
        }
        else
        {
            _logger.LogDebug(
                "Response {MessageId} for session {SessionId} has no pending request.",
                envelope.MessageId, SessionId);
        }
    }

    private void HandleEvent(PiEnvelope envelope)
    {
        var occurredAt = _timeProvider.GetUtcNow();
        IReadOnlyDictionary<string, object?> payload = new Dictionary<string, object?>();
        if (envelope.Payload is JsonElement body && body.ValueKind == JsonValueKind.Object)
        {
            var mutable = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in body.EnumerateObject())
            {
                if (property.Name is "seq" && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt64(out var seq))
                {
                    UpdateLastSequence(seq);
                    continue;
                }

                if (property.Name is "timestamp" && property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt64(out var millis))
                {
                    occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(millis);
                    continue;
                }

                mutable[property.Name] = property.Value.Clone();
            }


            payload = mutable;
        }

        _lastEventType = envelope.Type;
        if (envelope.Type is "turn.started")
        {
            _isStreaming = true;
        }
        else if (envelope.Type is "turn.completed" or "turn.failed" or "agent.completed"
                 or "session.closed" or "session.failed")
        {
            _isStreaming = false;
        }

        var value = new NormalizedAgentEvent(
            envelope.ProtocolVersion,
            envelope.MessageId,
            _identity.NodeId,
            _identity.ProjectId,
            _identity.RequestId,
            _identity.SessionId,
            _identity.ParentSessionId,
            Volatile.Read(ref _lastSequenceSeen),
            AgentRuntimeKinds.Pi,
            envelope.Type,
            occurredAt,
            payload);

        if (!_events.Writer.TryWrite(value))
        {
            _logger.LogWarning("Event channel for session {SessionId} rejected an event.", SessionId);
        }
    }

    /// <summary>
    /// Emits one orchestration event (custom-tool persistence) onto the normalized channel with
    /// a node-assigned sequence number.
    /// </summary>
    public Task EmitOrchestrationEventAsync(
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
        => EmitSyntheticAsync(type, payload, cancellationToken);

    private async Task HandleWorkerRequestAsync(PiEnvelope envelope)
    {
        PiToolResponse toolResponse;
        try
        {
            toolResponse = await _orchestration
                .HandleAsync(_identity, envelope.Type, envelope.Payload, _disposeCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Orchestration handler failed for request {RequestType} on session {SessionId}.",
                envelope.Type, SessionId);
            toolResponse = PiToolResponse.Failure("handler_error", ex.Message);
        }

        object payload = toolResponse.Ok
            ? new Dictionary<string, object?> { ["ok"] = true, ["result"] = toolResponse.Result }
            : new Dictionary<string, object?>
            {
                ["ok"] = false,
                ["error"] = new Dictionary<string, object?>
                {
                    ["code"] = toolResponse.ErrorCode,
                    ["message"] = toolResponse.ErrorMessage,
                },
            };

        var response = new PiEnvelope(
            PiProtocol.Version,
            envelope.MessageId,
            PiFrameKinds.Response,
            envelope.SessionId,
            envelope.Type,
            JsonSerializer.SerializeToElement(payload));
        await WriteFrameAsync(response, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<PiEnvelope> RequestAsync(
        string type,
        IReadOnlyDictionary<string, object?>? payload,
        CancellationToken cancellationToken)
    {
        var messageId = NextMessageId();
        var completion = new TaskCompletionSource<PiEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[messageId] = completion;
        try
        {
            var envelope = new PiEnvelope(
                PiProtocol.Version,
                messageId,
                PiFrameKinds.Request,
                SessionId,
                type,
                payload is null ? null : JsonSerializer.SerializeToElement(payload));
            await WriteFrameAsync(envelope, cancellationToken).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _disposeCts.Token);
            timeout.CancelAfter(_requestTimeout);
            var winner = await Task.WhenAny(
                completion.Task,
                Task.Delay(Timeout.Infinite, timeout.Token)).ConfigureAwait(false);
            if (winner != completion.Task)
            {
                _pending.TryRemove(messageId, out _);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"Protocol request '{type}' ({messageId}) timed out after {_requestTimeout}.");
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(messageId, out _);
        }
    }

    private async Task EmitSyntheticAsync(
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken = default)
    {
        var sequence = Interlocked.Increment(ref _lastSequenceSeen);
        var value = new NormalizedAgentEvent(
            PiProtocol.Version,
            $"{SessionId}-synthetic-{sequence}",
            _identity.NodeId,
            _identity.ProjectId,
            _identity.RequestId,
            _identity.SessionId,
            _identity.ParentSessionId,
            sequence,
            AgentRuntimeKinds.Pi,
            type,
            _timeProvider.GetUtcNow(),
            payload);
        await _events.Writer.WriteAsync(value, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task WriteFrameAsync(PiEnvelope envelope, CancellationToken cancellationToken)
    {
        var frame = PiProtocol.Encode(envelope);
        await _process.Stdin.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _process.Stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void FailPendingRequests(string reason)
    {
        foreach (var entry in _pending)
        {
            if (_pending.TryRemove(entry.Key, out var pending))
            {
                pending.TrySetException(new InvalidOperationException(
                    $"Protocol request failed: {reason}."));
            }
        }
    }

    private void UpdateLastSequence(long sequence)
    {
        var current = Volatile.Read(ref _lastSequenceSeen);
        while (sequence > current
               && Interlocked.CompareExchange(ref _lastSequenceSeen, sequence, current) != current)
        {
            current = Volatile.Read(ref _lastSequenceSeen);
        }
    }

    private string NextMessageId()
    {
        var id = Interlocked.Increment(ref _nextMessageId);
        return $"{SessionId}-req-{id}";
    }

    private void FailEvents(string? reason)
    {
        if (Interlocked.Exchange(ref _exitHandled, 1) == 1)
        {
            return;
        }

        if (reason is not null)
        {
            _logger.LogDebug("Session {SessionId} event stream ended: {Reason}", SessionId, reason);
        }

        _events.Writer.TryComplete();
    }
}
internal static class PiWorkerSessionExtensions
{
    /// <summary>Awaits completion of a request while surfacing only real failures.</summary>
    public static async Task AwaitCompletion(this Task<PiEnvelope> request)
    {
        await request.ConfigureAwait(false);
    }
}
