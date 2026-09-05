using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// Bounded newline-delimited JSON-RPC 2.0 client over one <see cref="IMuseProcess"/>'s stdio
/// (MSP v1). Correlates responses to requests, hands notifications to the owner, answers
/// unsolicited server requests with <c>methodNotFound</c>, and fails closed — pending
/// requests faulted and the host terminated — on any malformed or oversize frame.
/// </summary>
internal sealed class MuseHostClient : IAsyncDisposable
{
    internal const int MethodNotFoundCode = -32601;

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly byte[] NewLine = [(byte)'\n'];

    private readonly IMuseProcess _process;
    private readonly int _maxLineBytes;
    private readonly int _maxStderrLines;
    private readonly Action<string, JsonElement> _onNotification;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentQueue<string> _stderrTail = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    private long _nextId;
    private string? _fault;
    private bool _stdinClosed;
    private Task? _stdoutPump;
    private Task? _stderrPump;

    public MuseHostClient(
        IMuseProcess process,
        int maxLineBytes,
        int maxStderrLines,
        Action<string, JsonElement> onNotification,
        ILogger logger)
    {
        _process = process;
        _maxLineBytes = maxLineBytes;
        _maxStderrLines = maxStderrLines;
        _onNotification = onNotification;
        _logger = logger;
    }

    public int ProcessId => _process.Id;

    public Task<int> Exited => _process.Exited;

    /// <summary>Non-null once a protocol fault closed the client.</summary>
    public string? Fault => Volatile.Read(ref _fault);

    public IReadOnlyList<string> StderrTail => _stderrTail.ToArray();

    public void Start()
    {
        _stdoutPump = PumpStdoutAsync(_lifetime.Token);
        _stderrPump = PumpStderrAsync(_lifetime.Token);
    }

    /// <summary>Sends one request and awaits its result, bounded by <paramref name="timeout"/>.</summary>
    public async Task<JsonElement> RequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ThrowIfFaulted(method);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteFrameAsync(
                new { jsonrpc = "2.0", id, method, @params = parameters },
                cancellationToken).ConfigureAwait(false);

            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(timeout);
            return await completion.Task.WaitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Muse host did not answer '{method}' within {timeout.TotalSeconds:0}s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ThrowIfFaulted(method);
        return WriteFrameAsync(new { jsonrpc = "2.0", method, @params = parameters }, cancellationToken);
    }

    /// <summary>Stops reading, closes stdin, and terminates the host (SIGTERM then kill).</summary>
    public async Task TerminateAsync(CancellationToken cancellationToken)
    {
        await CloseStdinAsync().ConfigureAwait(false);
        if (!_process.Exited.IsCompleted)
        {
            await _process.TerminateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseStdinAsync().ConfigureAwait(false);
        try
        {
            if (!_process.Exited.IsCompleted)
            {
                await _process.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Muse host termination during dispose failed.");
        }

        _lifetime.Cancel();
        await AwaitPumpsAsync().ConfigureAwait(false);
        await _process.DisposeAsync().ConfigureAwait(false);
        FailPending("Muse host client disposed.");
        _lifetime.Dispose();
        _writeGate.Dispose();
    }

    /// <summary>Waits briefly for both pumps so the stderr tail is complete after exit.</summary>
    public async Task AwaitPumpsAsync()
    {
        foreach (var pump in new[] { _stdoutPump, _stderrPump })
        {
            if (pump is null)
            {
                continue;
            }

            try
            {
                await pump.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Pump outcome is already reflected in Fault / Closed.
            }
        }
    }

    private void ThrowIfFaulted(string method)
    {
        var fault = Fault;
        if (fault is not null)
        {
            throw new MuseProtocolException($"Muse host is closed ({fault}); cannot send '{method}'.");
        }
    }

    private async Task WriteFrameAsync(object frame, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, WireOptions);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _stdinClosed))
            {
                throw new MuseProtocolException("Muse host stdin is closed.");
            }

            var stdin = _process.Stdin;
            await stdin.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stdin.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new MuseProtocolException("Muse host stdin write failed.", ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new MuseProtocolException("Muse host stdin is closed.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task CloseStdinAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stdinClosed)
            {
                return;
            }

            _stdinClosed = true;
            try
            {
                await _process.Stdin.FlushAsync().ConfigureAwait(false);
                _process.Stdin.Close();
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
        finally
        {
            _writeGate.Release();
        }
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
                var faulted = false;
                while (TryReadLine(ref buffer, out var line))
                {
                    if (!await HandleFrameAsync(line, cancellationToken).ConfigureAwait(false))
                    {
                        faulted = true;
                        break;
                    }
                }

                if (!faulted && buffer.Length > _maxLineBytes)
                {
                    FailClosed($"frame exceeded {_maxLineBytes} bytes");
                    faulted = true;
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (faulted)
                {
                    break;
                }

                if (result.IsCompleted)
                {
                    if (buffer.Length > 0)
                    {
                        await HandleFrameAsync(buffer, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Muse stdout pump failed for host process {ProcessId}.", ProcessId);
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            FailPending(Fault is { } fault ? $"Muse host protocol fault: {fault}" : "Muse host closed its output.");
        }
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var newline = buffer.PositionOf((byte)'\n');
        if (newline is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, newline.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, newline.Value));
        return true;
    }

    /// <summary>Returns false when the frame faulted the client.</summary>
    private async ValueTask<bool> HandleFrameAsync(ReadOnlySequence<byte> line, CancellationToken cancellationToken)
    {
        if (line.Length > _maxLineBytes)
        {
            FailClosed($"frame exceeded {_maxLineBytes} bytes");
            return false;
        }

        if (IsBlank(line))
        {
            return true;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            FailClosed("malformed JSON-RPC frame");
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                FailClosed("JSON-RPC frame is not an object");
                return false;
            }

            var hasId = root.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
            if (root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
            {
                var name = method.GetString()!;
                if (hasId)
                {
                    await RejectServerRequestAsync(id, name, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                var parameters = root.TryGetProperty("params", out var value) ? value.Clone() : default;
                DispatchNotification(name, parameters);
                return true;
            }

            if (!hasId)
            {
                FailClosed("JSON-RPC frame is neither request, response, nor notification");
                return false;
            }

            if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var requestId)
                || !_pending.TryRemove(requestId, out var completion))
            {
                _logger.LogDebug("Muse host answered an unknown request id; ignored.");
                return true;
            }

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                completion.TrySetException(MuseRpcException.FromError(error));
                return true;
            }

            if (root.TryGetProperty("result", out var resultValue))
            {
                completion.TrySetResult(resultValue.Clone());
                return true;
            }

            completion.TrySetException(new MuseProtocolException("Muse host response carried neither result nor error."));
            FailClosed("response without result or error");
            return false;
        }
    }

    private void DispatchNotification(string method, JsonElement parameters)
    {
        try
        {
            _onNotification(method, parameters);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Muse notification handler failed for {Method}.", method);
        }
    }

    private async Task RejectServerRequestAsync(JsonElement id, string method, CancellationToken cancellationToken)
    {
        _logger.LogDebug("Muse host sent unsupported server request {Method}; declined.", method);
        try
        {
            await WriteFrameAsync(
                new
                {
                    jsonrpc = "2.0",
                    id = id.Clone(),
                    error = new
                    {
                        code = MethodNotFoundCode,
                        message = "DevFleet does not serve client-side methods.",
                        data = new { kind = "methodNotFound" },
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (MuseProtocolException)
        {
            // stdin already closed; the host is on its way out.
        }
    }

    private async Task PumpStderrAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            _process.Stderr,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);
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
                while (_stderrTail.Count > _maxStderrLines)
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
            _logger.LogDebug(ex, "Muse stderr pump ended for host process {ProcessId}.", ProcessId);
        }
    }

    private void FailClosed(string reason)
    {
        if (Interlocked.CompareExchange(ref _fault, reason, null) is not null)
        {
            return;
        }

        _logger.LogWarning("Muse host protocol fault ({Reason}); terminating host process {ProcessId}.", reason, ProcessId);
        FailPending($"Muse host protocol fault: {reason}");
        _ = TerminateAfterFaultAsync();
    }

    private async Task TerminateAfterFaultAsync()
    {
        try
        {
            await TerminateAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Muse host termination after fault failed.");
        }
    }

    private void FailPending(string reason)
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var completion))
            {
                completion.TrySetException(new MuseProtocolException(reason));
            }
        }
    }

    private static bool IsBlank(ReadOnlySequence<byte> line)
    {
        foreach (var segment in line)
        {
            foreach (var value in segment.Span)
            {
                if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>Transport or framing failure talking to a Muse host; never carries raw provider output.</summary>
internal class MuseProtocolException : Exception
{
    public MuseProtocolException(string message)
        : base(message)
    {
    }

    public MuseProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A JSON-RPC error response. The host message is sanitized and bounded.</summary>
internal sealed class MuseRpcException : MuseProtocolException
{
    private const int MaxMessageChars = 256;

    public MuseRpcException(int code, string? kind, string hostMessage)
        : base(Describe(code, kind, hostMessage))
    {
        Code = code;
        Kind = kind;
        HostMessage = hostMessage;
    }

    public int Code { get; }

    /// <summary>Stable <c>error.data.kind</c> category when the host supplied one.</summary>
    public string? Kind { get; }

    /// <summary>Sanitized, bounded host message; only ever used for auth classification and display.</summary>
    public string HostMessage { get; }

    public static MuseRpcException FromError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var parsed)
            ? parsed
            : 0;
        var message = error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String
            ? messageValue.GetString()
            : null;
        var kind = error.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("kind", out var kindValue)
            && kindValue.ValueKind == JsonValueKind.String
            ? kindValue.GetString()
            : null;
        return new MuseRpcException(code, kind, DiagnosticSanitizer.Sanitize(message, MaxMessageChars));
    }

    private static string Describe(int code, string? kind, string hostMessage)
        => string.IsNullOrEmpty(hostMessage)
            ? $"Muse host rejected the request ({kind ?? "error"} {code})."
            : $"Muse host rejected the request ({kind ?? "error"} {code}): {hostMessage}";
}
