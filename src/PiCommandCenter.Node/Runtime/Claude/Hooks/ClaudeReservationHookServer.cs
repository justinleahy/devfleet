using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;

namespace PiCommandCenter.Node.Runtime.Claude.Hooks;

/// <summary>
/// Loopback HTTP endpoint the host-owned hook executable calls. Fail-closed: unknown
/// sessions, timeouts, and evaluator denials return HTTP 403 so curl -f exits 2.
/// </summary>
public sealed class ClaudeReservationHookServer : IHostedService, IDisposable
{
    private readonly ClaudeReservationHookEvaluator _evaluator;
    private readonly ConcurrentDictionary<string, ClaudeHookSessionContext> _sessions = new(StringComparer.Ordinal);
    private HttpListener? _listener;
    private CancellationTokenSource? _run;
    private Task? _loop;
    private int _started;

    public ClaudeReservationHookServer(ClaudeReservationHookEvaluator evaluator)
    {
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
    }

    /// <summary>Base URL such as <c>http://127.0.0.1:port/pcc-claude-hook</c>.</summary>
    public string BaseUrl { get; private set; } = "";

    public void Register(ClaudeHookSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _sessions[context.SessionId] = context;
    }

    public void Unregister(string sessionId)
    {
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    public bool TryGet(string sessionId, out ClaudeHookSessionContext? context)
        => _sessions.TryGetValue(sessionId, out context);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureStarted();
        return Task.CompletedTask;
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        var port = ClaudeHookSettingsInstaller.AllocateLoopbackPort();
        var prefix = $"http://127.0.0.1:{port}/pcc-claude-hook/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();

        _listener = listener;
        BaseUrl = prefix.TrimEnd('/');
        _run = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_run.Token));
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _run?.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }

        if (_loop is not null)
        {
            try
            {
                await _loop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    public void Dispose()
    {
        _run?.Cancel();
        _listener?.Close();
        _run?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested || !listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context, cancellationToken), cancellationToken);
        }
    }

    internal async Task HandleAsync(HttpListenerContext http, CancellationToken cancellationToken)
    {
        try
        {
            var path = http.Request.Url?.AbsolutePath ?? "";
            var sessionId = http.Request.QueryString["sessionId"] ?? "";
            using var reader = new StreamReader(http.Request.InputStream, http.Request.ContentEncoding);
            var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

            _sessions.TryGetValue(sessionId, out var session);
            ClaudeHookDecision decision;
            if (path.EndsWith("/post", StringComparison.OrdinalIgnoreCase))
            {
                decision = _evaluator.EvaluatePost(body, session);
                await WriteAsync(http, 200, decision.StdoutJson).ConfigureAwait(false);
                return;
            }

            if (!path.EndsWith("/pre", StringComparison.OrdinalIgnoreCase))
            {
                decision = ClaudeHookDecision.Deny("unknown hook endpoint");
                await WriteAsync(http, 403, decision.StdoutJson).ConfigureAwait(false);
                return;
            }

            decision = await _evaluator.EvaluatePreAsync(body, session, cancellationToken).ConfigureAwait(false);
            await WriteAsync(http, decision.Allow ? 200 : 403, decision.StdoutJson).ConfigureAwait(false);
        }
        catch (Exception)
        {
            try
            {
                await WriteAsync(http, 403, ClaudeHookDecision.DenyJson("hook server failure")).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task WriteAsync(HttpListenerContext http, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.ContentLength64 = bytes.Length;
        await http.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        http.Response.Close();
    }
}
