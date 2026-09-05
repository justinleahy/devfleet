using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Runtime.Claude.Hooks;
using PiCommandCenter.Node.Security;


namespace PiCommandCenter.Node.Runtime.Claude;


/// <summary>
/// Official Claude Code adapter (SPEC §26, §28): launches
/// <c>claude -p &lt;prompt&gt; --output-format stream-json --verbose --settings &lt;trusted&gt;
/// --permission-mode dontAsk</c>, captures <c>system/init</c> session_id, and normalizes
/// stream-json. Never inspects credentials or transcript files.
/// </summary>
public sealed class ClaudeCodeRuntimeAdapter : IAgentRuntimeAdapter
{
    private readonly ClaudeCodeOptions _options;
    private readonly IOfficialAgentProcessFactory _processFactory;
    private readonly ClaudeHookSettingsInstaller? _hookInstaller;
    private readonly string _nodeId;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ClaudeCodeRuntimeAdapter> _logger;
    private readonly ConcurrentDictionary<string, ClaudeCodeSession> _sessions = new();

    public ClaudeCodeRuntimeAdapter(
        IOptions<NodeOptions> nodeOptions,
        IOptions<ClaudeCodeOptions> claudeOptions,
        IOfficialAgentProcessFactory processFactory,
        TimeProvider timeProvider,
        ILogger<ClaudeCodeRuntimeAdapter> logger,
        ClaudeHookSettingsInstaller? hookInstaller = null)
    {
        ArgumentNullException.ThrowIfNull(nodeOptions);
        ArgumentNullException.ThrowIfNull(claudeOptions);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = claudeOptions.Value;
        _nodeId = nodeOptions.Value.Id.ToString("D");
        _hookInstaller = hookInstaller;
    }

    public string RuntimeKind => AgentRuntimeKinds.ClaudeCode;

    public AgentRuntimeCapabilities Capabilities { get; } = new(
        SupportsStreamingEvents: true,
        SupportsSendInput: false,
        SupportsCancel: true,
        SupportsSnapshot: true,
        SupportsChildSpawn: false,
        SupportsPlanTools: false);

    public async Task<AgentSessionHandle> StartAsync(
        AgentStartRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Mode != AgentRuntimeMode.Child)
        {
            throw new NotSupportedException(
                $"Claude Code adapter supports child mode only; got '{request.Mode}'.");
        }

        if (request.ParentSessionId is null)
        {
            throw new ArgumentException(
                "A child session requires a parent session id.", nameof(request));
        }

        if (!ClaudeCodeProfiles.IsSupported(request.RuntimeProfile))
        {
            throw new NotSupportedException(
                $"Claude Code profile '{request.RuntimeProfile}' is not supported. "
                + $"Use '{ClaudeCodeProfiles.ReadOnly}' or '{ClaudeCodeProfiles.ReservedWrite}'.");
        }

        if (request.RuntimeProfile == ClaudeCodeProfiles.ReservedWrite
            && request.Authorization is null)
        {
            throw new InvalidOperationException(
                "Claude reserved-write requires an acquired reservation lease.");
        }

        string settingsPath;
        ClaudeHookInstallResult? install = null;
        if (_hookInstaller is not null)
        {
            var grant = request.Authorization;
            var hookContext = new ClaudeHookSessionContext(
                request.SessionId,
                grant?.LeaseId ?? Guid.Empty,
                grant?.FencingToken ?? 0,
                request.WorkingDirectory);
            install = _hookInstaller.Install(request.RuntimeProfile, hookContext);
            settingsPath = install.SettingsPath;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(_options.SettingsPath) || !File.Exists(_options.SettingsPath))
            {
                throw new InvalidOperationException(
                    "Claude Code requires a trusted application-owned settings file (--settings).");
            }

            settingsPath = _options.SettingsPath;
        }

        var arguments = new List<string>
        {
            "-p",
            request.Prompt,
            "--output-format",
            "stream-json",
            "--verbose",
            "--settings",
            settingsPath,
            "--setting-sources",
            string.Empty,
            "--permission-mode",
            "dontAsk",
        };
        if (request.Model is not null)
        {
            arguments.Add("--model");
            arguments.Add(request.Model);
        }

        var process = _processFactory.Start(new OfficialProcessStartRequest(
            _options.Executable,
            arguments,
            request.WorkingDirectory,
            ExtraEnvironment: null));

        var session = new ClaudeCodeSession(
            request,
            _nodeId,
            process,
            _options,
            _timeProvider,
            _logger,
            _hookInstaller);

        _sessions[request.SessionId] = session;
        try
        {
            await session.WaitForInitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _sessions.TryRemove(request.SessionId, out _);
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        _logger.LogInformation(
            "Started Claude Code session {SessionId} (provider {ProviderSessionId}, pid {ProcessId}).",
            request.SessionId, session.ProviderSessionId, session.ProcessId);

        return new AgentSessionHandle(
            request.SessionId,
            session.ProviderSessionId,
            RuntimeKind,
            _timeProvider.GetUtcNow());
    }

    public IAsyncEnumerable<NormalizedAgentEvent> WatchAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        return RequireSession(sessionId).ReadAllEventsAsync(cancellationToken);
    }

    public Task SendAsync(string sessionId, AgentInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        _ = RequireSession(sessionId);
        return Task.FromException(new NotSupportedException(
            "Claude Code -p sessions do not accept mid-run input."));
    }

    public Task CancelAsync(string sessionId, CancellationToken cancellationToken)
        => RequireSession(sessionId).CancelAsync(cancellationToken);

    public async Task CloseSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            _hookInstaller?.Uninstall(sessionId);
            return;
        }

        try
        {
            await session.CancelAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Best-effort terminate before dispose.
        }
        finally
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    public Task<AgentRuntimeSnapshot> GetSnapshotAsync(
        string sessionId,
        CancellationToken cancellationToken)
        => Task.FromResult(RequireSession(sessionId).GetSnapshot());

    public IReadOnlyList<string> GetStderrTail(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.StderrTail : [];

    public int? GetProcessId(string sessionId)
        => _sessions.TryGetValue(sessionId, out var session) ? session.ProcessId : null;

    private ClaudeCodeSession RequireSession(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session;
        }

        throw new KeyNotFoundException($"Unknown or stopped Claude Code session '{sessionId}'.");
    }
}

internal sealed class ClaudeCodeSession : IAsyncDisposable
{
    private readonly AgentStartRequest _request;
    private readonly string _nodeId;
    private readonly IOfficialAgentProcess _process;
    private readonly ClaudeCodeOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly ClaudeHookSettingsInstaller? _hookInstaller;
    private readonly Channel<NormalizedAgentEvent> _events = Channel.CreateUnbounded<NormalizedAgentEvent>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private readonly TaskCompletionSource<string?> _init = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _disposeCts = new();
    private long _sequence;
    private int _malformedCount;
    private string? _providerSessionId;
    private string? _lastEventType;
    private bool _failed;
    private bool _cancelled;
    private bool _closed;
    private bool _authBlocked;

    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;


    public ClaudeCodeSession(
        AgentStartRequest request,
        string nodeId,
        IOfficialAgentProcess process,
        ClaudeCodeOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        ClaudeHookSettingsInstaller? hookInstaller)
    {
        _request = request;
        _nodeId = nodeId;
        _process = process;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _hookInstaller = hookInstaller;
        ProcessId = process.Id;
        _stdoutPump = PumpStdoutAsync(_disposeCts.Token);
        _stderrPump = PumpStderrAsync(_disposeCts.Token);
        _ = PumpExitAsync();
    }

    public int ProcessId { get; }

    public string? ProviderSessionId => _providerSessionId;

    public IReadOnlyList<string> StderrTail => _stderrTail.ToArray();

    public async Task WaitForInitAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(_options.StartTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeout.Token);
        try
        {
            _providerSessionId = await _init.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested
                                                 && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "Claude Code did not emit system/init session_id before the start timeout.");
        }
    }

    public IAsyncEnumerable<NormalizedAgentEvent> ReadAllEventsAsync(CancellationToken cancellationToken)
        => _events.Reader.ReadAllAsync(cancellationToken);

    public async Task CancelAsync(CancellationToken cancellationToken)
    {
        _cancelled = true;
        await _process.SignalAsync(OfficialAgentProcessFactory.SigInt, cancellationToken)
            .ConfigureAwait(false);

        var grace = Task.Delay(_options.CancelGraceMilliseconds, CancellationToken.None);
        if (await Task.WhenAny(_process.Exited, grace).ConfigureAwait(false) != _process.Exited)
        {
            await _process.SignalAsync(OfficialAgentProcessFactory.SigTerm, cancellationToken)
                .ConfigureAwait(false);
            var second = Task.Delay(_options.CancelGraceMilliseconds, CancellationToken.None);
            if (await Task.WhenAny(_process.Exited, second).ConfigureAwait(false) != _process.Exited)
            {
                await _process.KillTreeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public AgentRuntimeSnapshot GetSnapshot()
    {
        var exited = _process.Exited.IsCompleted;
        var liveness = exited ? AgentLiveness.Exited : AgentLiveness.Online;
        var activity = _lastEventType switch
        {
            "tool.started" or "tool.progress" => AgentActivity.RunningTool,
            "message.delta" => AgentActivity.Responding,
            _ when !exited => AgentActivity.Reasoning,
            _ => AgentActivity.Idle,
        };
        var workState = _cancelled
            ? AgentWorkState.Cancelled
            : _authBlocked
                ? AgentWorkState.Blocked
                : _failed
                    ? AgentWorkState.Failed
                    : exited
                        ? AgentWorkState.Completed
                        : AgentWorkState.Executing;
        var attention = _authBlocked
            ? AgentAttention.InputRequired
            : _failed
                ? AgentAttention.Error
                : AgentAttention.None;
        var reason = _cancelled
            ? "cancelled"
            : _authBlocked
                ? ProviderAuthClassifier.NativeLoginReason(AgentRuntimeKinds.ClaudeCode)
                : _failed
                    ? "process failed"
                    : exited
                        ? "exited"
                        : $"process {ProcessId} online";

        return new AgentRuntimeSnapshot(
            _request.SessionId,
            AgentRuntimeKinds.ClaudeCode,
            liveness,
            activity,
            attention,
            workState,
            reason,
            _lastEventType,
            _providerSessionId,
            Volatile.Read(ref _sequence),
            LastHeartbeatAt: _timeProvider.GetUtcNow());
    }

    public async ValueTask DisposeAsync()
    {
        _disposeCts.Cancel();
        _events.Writer.TryComplete();
        await _process.DisposeAsync().ConfigureAwait(false);
        _hookInstaller?.Uninstall(_request.SessionId);
        _disposeCts.Dispose();
    }

    private async Task PumpStdoutAsync(CancellationToken cancellationToken)
    {
        var reader = PipeReader.Create(_process.Stdout);
        try
        {
            while (true)
            {
                var result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = result.Buffer;
                var consumed = buffer.Start;
                var remaining = buffer;
                while (remaining.PositionOf((byte)'\n') is { } newline)
                {
                    var lineBuffer = remaining.Slice(0, newline);
                    HandleLine(Encoding.UTF8.GetString(lineBuffer.ToArray()));
                    consumed = remaining.GetPosition(1, newline);
                    remaining = remaining.Slice(consumed);
                }

                reader.AdvanceTo(consumed, buffer.End);
                if (result.IsCompleted)
                {
                    if (!remaining.IsEmpty)
                    {
                        HandleLine(Encoding.UTF8.GetString(remaining.ToArray()));
                    }

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
            _logger.LogWarning(ex, "Claude stdout pump for {SessionId} failed.", _request.SessionId);
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
                var text = Encoding.UTF8.GetString(result.Buffer);
                reader.AdvanceTo(result.Buffer.End);
                foreach (var piece in text.Split('\n'))
                {
                    if (piece.Length == 0)
                    {
                        continue;
                    }

                    var sanitized = DiagnosticSanitizer.SanitizeLine(piece);
                    _stderrTail.Enqueue(sanitized);
                    while (_stderrTail.Count > _options.MaxStderrLines)
                    {
                        _stderrTail.TryDequeue(out _);
                    }
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
            _logger.LogDebug(ex, "Claude stderr pump for {SessionId} ended.", _request.SessionId);
            await reader.CompleteAsync(ex).ConfigureAwait(false);
        }
    }

    private async Task PumpExitAsync()
    {
        var exitCode = await _process.Exited.ConfigureAwait(false);
        try
        {
            await _stdoutPump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        try
        {
            await _stderrPump.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        if (!_init.Task.IsCompleted)
        {
            _init.TrySetException(new InvalidOperationException(
                $"Claude Code exited with code {exitCode} before system/init."));
        }

        if (_cancelled)
        {
            Emit("session.cancelled", new Dictionary<string, object?>
            {
                ["exitCode"] = exitCode,
                ["processId"] = ProcessId,
            });
            Emit("session.closed", new Dictionary<string, object?>
            {
                ["reason"] = "cancelled",
                ["exitCode"] = exitCode,
            });
        }
        else if (exitCode != 0)
        {
            var tail = string.Join(" | ", _stderrTail.TakeLast(8));
            if (ProviderAuthClassifier.IsMissing(tail))
            {
                _authBlocked = true;
                Emit("session.snapshot", ProviderAuthClassifier.SnapshotPayload(AgentRuntimeKinds.ClaudeCode, tail));
                Emit("session.closed", new Dictionary<string, object?>
                {
                    ["reason"] = "provider_auth_required",
                    ["exitCode"] = exitCode,
                });
            }
            else
            {
                _failed = true;
                Emit("session.failed", new Dictionary<string, object?>
                {
                    ["reason"] = $"Claude Code exited with code {exitCode}.",
                    ["exitCode"] = exitCode,
                    ["stderrTail"] = tail,
                    ["processId"] = ProcessId,
                });
                Emit("session.closed", new Dictionary<string, object?>
                {
                    ["reason"] = "worker_crashed",
                    ["exitCode"] = exitCode,
                });
            }
        }
        else if (!_closed)
        {
            Emit("session.closed", new Dictionary<string, object?>
            {
                ["reason"] = "completed",
                ["exitCode"] = 0,
                ["processId"] = ProcessId,
            });
        }

        _events.Writer.TryComplete();
    }

    private void HandleLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        if (Encoding.UTF8.GetByteCount(line) > _options.MaxLineBytes)
        {
            EmitMalformed(line);
            return;
        }

        var parsed = ClaudeStreamJsonNormalizer.Parse(line);
        if (parsed.IsMalformed)
        {
            EmitMalformed(line);
            return;
        }

        if (parsed.ProviderSessionId is not null)
        {
            _providerSessionId ??= parsed.ProviderSessionId;
        }

        if (parsed.Type == "session.started")
        {
            var payload = new Dictionary<string, object?>(parsed.Payload, StringComparer.Ordinal)
            {
                ["processId"] = ProcessId,
                ["profile"] = _request.RuntimeProfile,
                ["runtimeKind"] = AgentRuntimeKinds.ClaudeCode,
                ["providerSessionId"] = _providerSessionId,
            };
            Emit("session.registered", payload);
            Emit("session.started", payload);
            _init.TrySetResult(_providerSessionId);
            return;
        }

        if (parsed.Type == "result.completed")
        {
            Emit(parsed.Type, parsed.Payload);
            Emit("session.closed", new Dictionary<string, object?>
            {
                ["reason"] = "completed",
                ["processId"] = ProcessId,
            });
            _closed = true;
            return;
        }

        Emit(parsed.Type, parsed.Payload);
    }

    private void EmitMalformed(string line)
    {
        var count = Interlocked.Increment(ref _malformedCount);
        if (count > _options.MaxMalformedEvents)
        {
            return;
        }

        Emit("runtime.malformed_line", new Dictionary<string, object?>
        {
            ["preview"] = DiagnosticSanitizer.SanitizeLine(
                line.Length > 256 ? line[..256] : line,
                256),
            ["length"] = line.Length,
        });
    }

    private void Emit(string type, IReadOnlyDictionary<string, object?> payload)
    {
        var sequence = Interlocked.Increment(ref _sequence);
        _lastEventType = type;
        var value = new NormalizedAgentEvent(
            ProtocolVersion.Current,
            $"{_request.SessionId}:{sequence}",
            _nodeId,
            _request.ProjectId.Value.ToString("D"),
            _request.RequestId.Value.ToString("D"),
            _request.SessionId,
            _request.ParentSessionId,
            sequence,
            AgentRuntimeKinds.ClaudeCode,
            type,
            _timeProvider.GetUtcNow(),
            payload);
        _events.Writer.TryWrite(value);
    }
}
