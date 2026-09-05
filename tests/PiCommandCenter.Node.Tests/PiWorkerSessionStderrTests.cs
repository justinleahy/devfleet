using System.IO.Pipelines;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Security;

namespace PiCommandCenter.Node.Tests;

public sealed class PiWorkerSessionStderrTests
{
    [Fact]
    public async Task Stderr_tail_redacts_secrets_tokens_and_paths_and_caps_length()
    {
        var stderr = new Pipe();
        var stdout = new Pipe();
        var process = new FakePiProcess(stdout, stderr);
        var session = CreateSession(process);

        var start = session.StartAsync("/tmp/wd", "/tmp/data", model: null, systemPrompt: null, AgentRuntimeMode.Root, parentSessionId: null, CancellationToken.None);
        var secret = "Bearer abcdef.secret.token leaked /home/justin/.pi/token eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.payload.signature\n";
        await stderr.Writer.WriteAsync(Encoding.UTF8.GetBytes(secret));
        await stderr.Writer.CompleteAsync();
        await Task.Delay(150);

        var tail = string.Join('\n', session.StderrTail);
        Assert.DoesNotContain("abcdef.secret.token", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/justin", tail, StringComparison.Ordinal);
        Assert.True(tail.Length <= DiagnosticSanitizer.DefaultMaxChars + 1);

        process.Exit.TrySetResult(1);
        await stdout.Writer.CompleteAsync();
        await Task.Delay(150);
        await session.DisposeAsync();
        _ = start;
    }

    [Fact]
    public async Task Auth_stderr_emits_blocked_snapshot_not_crash()
    {
        var stderr = new Pipe();
        var stdout = new Pipe();
        var process = new FakePiProcess(stdout, stderr);
        var session = CreateSession(process);

        var start = session.StartAsync("/tmp/wd", "/tmp/data", model: null, systemPrompt: null, AgentRuntimeMode.Root, parentSessionId: null, CancellationToken.None);
        await stderr.Writer.WriteAsync(Encoding.UTF8.GetBytes("Error: not logged in. Please log in.\n"));
        await stderr.Writer.CompleteAsync();
        var waited = 0;
        while (session.StderrTail.Count == 0 && waited < 50)
        {
            await Task.Delay(20);
            waited++;
        }

        Assert.NotEmpty(session.StderrTail);
        process.Exit.TrySetResult(1);
        await stdout.Writer.CompleteAsync();

        NormalizedAgentEvent? snapshot = null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await foreach (var evt in session.ReadAllEventsAsync(cts.Token))
        {
            if (evt.Type == "session.snapshot")
            {
                snapshot = evt;
                break;
            }
        }

        Assert.NotNull(snapshot);
        Assert.Equal(nameof(AgentAttention.InputRequired), snapshot.Payload["attention"]?.ToString());
        Assert.Equal("provider_native_login_required", snapshot.Payload["auth"]?.ToString());
        await session.DisposeAsync();
        _ = start;
    }

    private static PiWorkerSession CreateSession(FakePiProcess process)
    {
        var identity = new PiOrchestrationContext(
            "sess-stderr",
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            null,
            (_, _, _) => Task.CompletedTask);
        return new PiWorkerSession(
            identity,
            process,
            new NoopOrchestration(),
            TimeSpan.FromSeconds(5),
            TimeProvider.System,
            NullLogger.Instance);
    }

    private sealed class NoopOrchestration : IPiOrchestrationRequestHandler
    {
        public Task<PiToolResponse> HandleAsync(
            PiOrchestrationContext context,
            string requestType,
            System.Text.Json.JsonElement? payload,
            CancellationToken cancellationToken)
            => Task.FromResult(PiToolResponse.Success());
    }

    private sealed class FakePiProcess(Pipe stdout, Pipe stderr) : IPiWorkerProcess
    {
        public Stream Stdin { get; } = new MemoryStream();
        public Stream Stdout { get; } = stdout.Reader.AsStream();
        public Stream Stderr { get; } = stderr.Reader.AsStream();
        public TaskCompletionSource<int> Exit { get; } = new();
        public Task<int> Exited => Exit.Task;
        public Task KillTreeAsync(CancellationToken cancellationToken)
        {
            Exit.TrySetResult(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
