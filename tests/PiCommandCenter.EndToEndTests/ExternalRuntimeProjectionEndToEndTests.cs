using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Infrastructure.Security;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Transport;
using PiCommandCenter.Node;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.Runtime.Claude;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// No-network fake runtimes: normalized events land in the Node spool and project through
/// the Control Plane event sink into AgentSessions.
/// </summary>
public sealed class ExternalRuntimeProjectionEndToEndTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-ext-e2e", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Claude_fake_runtime_events_spool_and_project()
    {
        var nodeId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var process = new MemoryOfficialProcess();
        var settings = Path.Combine(_root, "claude-settings.json");
        File.WriteAllText(settings, "{}");
        var adapter = new ClaudeCodeRuntimeAdapter(
            Options.Create(new NodeOptions { Id = nodeId }),
            Options.Create(new ClaudeCodeOptions
            {
                SettingsPath = settings,
                StartTimeoutSeconds = 8,
            }),
            new FixedOfficialFactory(process),
            TimeProvider.System,
            NullLogger<ClaudeCodeRuntimeAdapter>.Instance);

        var request = new AgentStartRequest(
            "claude-e2e-1",
            new ProjectId(projectId),
            new WorkRequestId(requestId),
            "pi-root-1",
            "claude-reviewer",
            "reviewer",
            _root,
            "Review",
            AgentRuntimeMode.Child,
            "claude-code/default");

        var start = adapter.StartAsync(request, CancellationToken.None);
        await process.WriteStdoutAsync(
            """{"type":"system","subtype":"init","session_id":"claude-prov-e2e"}""");
        var handle = await start;
        Assert.Equal("claude-prov-e2e", handle.ProviderSessionId);

        await process.WriteStdoutAsync("""{"type":"permission_denial","tool_name":"Bash","reason":"denied"}""");
        await process.WriteStdoutAsync("this is not json");
        await process.WriteStdoutAsync(
            """{"type":"result","result":"ok","usage":{"input_tokens":2,"output_tokens":1}}""");
        process.Complete(0);

        var events = await CollectUntilAsync(
            adapter.WatchAsync(handle.SessionId, CancellationToken.None),
            list => list.Any(e => e.Type == "result.completed"),
            TimeSpan.FromSeconds(8));

        Assert.Contains(events, e => e.Type == "session.registered");
        Assert.Contains(events, e => e.Type == "permission_denial");
        Assert.Contains(events, e => e.Type == "runtime.malformed_line");
        Assert.Contains(events, e => e.Type == "result.completed");

        await PersistAndProjectAsync(nodeId, projectId, requestId, events, "claude-e2e-1", "claude-code");
    }

    [Fact]
    public async Task Antigravity_fake_runtime_events_spool_and_project()
    {
        var nodeId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var process = new MemoryAntigravityProcess();
        var adapter = new AntigravityRuntimeAdapter(
            Options.Create(new NodeOptions { Id = nodeId }),
            Options.Create(new AntigravityOptions { StartTimeoutSeconds = 8 }),
            new FixedAntigravityFactory(process),
            TimeProvider.System,
            NullLogger<AntigravityRuntimeAdapter>.Instance);

        var request = new AgentStartRequest(
            "agy-e2e-1",
            new ProjectId(projectId),
            new WorkRequestId(requestId),
            "pi-root-1",
            "agy-reviewer",
            "reviewer",
            _root,
            "Review the diff",
            AgentRuntimeMode.Child,
            "antigravity/default");

        var start = adapter.StartAsync(request, CancellationToken.None);
        await process.WaitForPromptAsync(TimeSpan.FromSeconds(5));
        var watch = CollectUntilAsync(
            adapter.WatchAsync("agy-e2e-1", CancellationToken.None),
            list => list.Any(e => e.Type == "turn.completed"),
            TimeSpan.FromSeconds(8));
        await process.WriteStdoutAsync(
            """{"event":"init","conversation_id":"agy-conv-e2e"}""");
        await process.WriteStdoutAsync(
            """{"event":"step_update","step_type":"user_input","state":"DONE"}""");
        await process.WriteStdoutAsync(
            """{"event":"result","status":"SUCCESS","response":"looks good","usage":{"inputTokens":4}}""");
        var handle = await start;
        Assert.Equal("agy-conv-e2e", handle.ProviderSessionId);
        var events = await watch;

        Assert.Contains(events, e => e.Type == "session.registered");
        Assert.Contains(events, e => e.Type == "turn.started");
        Assert.Contains(events, e => e.Type == "turn.completed");

        await PersistAndProjectAsync(nodeId, projectId, requestId, events, "agy-e2e-1", "antigravity");
    }

    private async Task PersistAndProjectAsync(
        Guid nodeId,
        Guid projectId,
        Guid requestId,
        IReadOnlyList<NormalizedAgentEvent> events,
        string sessionId,
        string runtime)
    {
        var spoolPath = Path.Combine(_root, runtime + "-spool.db");
        await using var spool = new SqliteNodeEventSpool(Options.Create(new NodeOptions
        {
            Id = nodeId,
            EventSpoolPath = spoolPath,
        }));

        var messages = events.Select((e, i) => ToMessage(e, nodeId, projectId, requestId, runtime)).ToList();
        foreach (var message in messages)
        {
            await spool.AppendAsync(message, CancellationToken.None);
        }

        var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
        Assert.Equal(messages.Count, pending.Count);
        Assert.Equal(messages.Select(m => m.EventId), pending.Select(p => p.EventId));

        var sqlitePath = Path.Combine(_root, runtime + "-cp.db");
        File.Create(sqlitePath).Dispose();
        var (passwordFile, credentialFile) = AuthTestMaterial.WriteTo(Path.Combine(_root, "auth"));
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={sqlitePath}");
            builder.UseTestAuthFiles(passwordFile, credentialFile);
        });
        using (var migrate = factory.Services.CreateScope())
        {
            migrate.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.Migrate();
        }

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();


        var sink = new NodeEventSink(TimeProvider.System, db, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var dtos = pending.Select(ToDto).ToList();
        var ack = await sink.AppendAsync(new EventBatch(dtos));
        Assert.Equal(dtos.Select(d => d.EventId), ack.EventIds);

        Assert.Equal(dtos.Count, db.SessionEvents.Count(e => e.SessionId == sessionId));
        var projection = await db.AgentSessions.SingleAsync(s => s.Id == sessionId);
        Assert.Equal(runtime, projection.Runtime);
        Assert.Equal(runtime + "/" + AgentModelSelector.DefaultModelId, projection.Model);
        Assert.Equal(sessionId, projection.Id);
        Assert.False(string.IsNullOrWhiteSpace(projection.ProviderSessionId));
    }

    private static NodeEventMessage ToMessage(
        NormalizedAgentEvent e,
        Guid nodeId,
        Guid projectId,
        Guid requestId,
        string runtime)
    {
        var payload = new Dictionary<string, object?>(e.Payload)
        {
            ["runtime"] = runtime,
            ["parentSessionId"] = e.ParentSessionId,
            ["agentName"] = e.Payload.TryGetValue("agentName", out var n) ? n : "agent",
            ["role"] = e.Payload.TryGetValue("role", out var r) ? r : "reviewer",
            ["model"] = e.Payload.TryGetValue("model", out var m) ? m : runtime + "/" + AgentModelSelector.DefaultModelId,
        };
        if (!payload.ContainsKey("providerSessionId") || payload["providerSessionId"] is null)
        {
            payload["providerSessionId"] = e.Payload.TryGetValue("providerSessionId", out var id) ? id : null;
        }

        return new NodeEventMessage(
            e.EventId,
            nodeId,
            projectId,
            requestId,
            e.SessionId,
            e.Sequence,
            e.Type,
            e.OccurredAt,
            JsonSerializer.Serialize(payload));
    }

    private static NodeEventDto ToDto(NodeEventMessage m) => new(
        m.EventId, m.NodeId, m.ProjectId, m.RequestId, m.SessionId, m.Sequence, m.Type, m.OccurredAt, m.PayloadJson);

    private static async Task<List<NormalizedAgentEvent>> CollectUntilAsync(
        IAsyncEnumerable<NormalizedAgentEvent> watch,
        Func<List<NormalizedAgentEvent>, bool> ready,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var collected = new List<NormalizedAgentEvent>();
        try
        {
            await foreach (var item in watch.WithCancellation(cts.Token))
            {
                collected.Add(item);
                if (ready(collected))
                {
                    return collected;
                }
            }
        }
        catch (OperationCanceledException) when (!ready(collected))
        {
            throw new TimeoutException("Events: " + string.Join(",", collected.Select(e => e.Type)));
        }

        if (!ready(collected))
        {
            throw new TimeoutException("Events: " + string.Join(",", collected.Select(e => e.Type)));
        }

        return collected;
    }

    private sealed class FixedOfficialFactory(MemoryOfficialProcess process) : IOfficialAgentProcessFactory
    {
        public IOfficialAgentProcess Start(OfficialProcessStartRequest request) => process;
    }

    private sealed class FixedAntigravityFactory(MemoryAntigravityProcess process) : IAntigravityProcessFactory
    {
        public IAntigravityProcess Start(AntigravityProcessStartInfo startInfo) => process;
    }

    private sealed class MemoryOfficialProcess : IOfficialAgentProcess
    {
        private readonly Pipe _stdout = new();
        private readonly Pipe _stderr = new();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 4242;
        public Stream Stdout => _stdout.Reader.AsStream();
        public Stream Stderr => _stderr.Reader.AsStream();
        public Task<int> Exited => _exited.Task;

        public async Task WriteStdoutAsync(string line)
        {
            await _stdout.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await _stdout.Writer.FlushAsync();
        }

        public void Complete(int code)
        {
            _stdout.Writer.Complete();
            _stderr.Writer.Complete();
            _exited.TrySetResult(code);
        }

        public Task SignalAsync(int signal, CancellationToken cancellationToken)
        {
            Complete(128 + signal);
            return Task.CompletedTask;
        }

        public Task KillTreeAsync(CancellationToken cancellationToken)
        {
            Complete(137);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Complete(0);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryAntigravityProcess : IAntigravityProcess
    {
        private readonly Pipe _stdin = new();
        private readonly Pipe _stdout = new();
        private readonly Pipe _stderr = new();
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 4343;
        public Stream Stdin => _stdin.Writer.AsStream();
        public Stream Stdout => _stdout.Reader.AsStream();
        public Stream Stderr => _stderr.Reader.AsStream();
        public Task<int> Exited => _exited.Task;

        public async Task WaitForPromptAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            var result = await _stdin.Reader.ReadAsync(cts.Token);
            _stdin.Reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
        }

        public async Task WriteStdoutAsync(string line)
        {
            await _stdout.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await _stdout.Writer.FlushAsync();
        }

        public void Complete(int code)
        {
            _stdout.Writer.Complete();
            _stderr.Writer.Complete();
            _exited.TrySetResult(code);
        }

        public Task InterruptAsync(CancellationToken cancellationToken)
        {
            Complete(130);
            return Task.CompletedTask;
        }

        public Task TerminateAsync(CancellationToken cancellationToken)
        {
            Complete(143);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Complete(0);
            return ValueTask.CompletedTask;
        }
    }
}
