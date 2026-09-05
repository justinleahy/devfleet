using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Focused start-flow contract: AgentStartRequest prompt, mode, parent session id, and model
/// selector plus the configured system prompt must reach the worker's <c>session.start</c>/
/// <c>session.input</c> frames.
/// </summary>
public sealed class PiRuntimeAdapterStartFlowTests
{
    [Fact]
    public async Task Root_start_flows_prompt_and_metadata_then_sends_first_input()
    {
        var result = await StartRootAsync(
            MakeRequest(mode: AgentRuntimeMode.Root, prompt: "Implement the reservation service"));

        Assert.Equal("root", result.StartPayload?.GetProperty("mode").GetString());
        Assert.False(result.StartPayload?.TryGetProperty("parentSessionId", out _));
        Assert.False(result.StartPayload?.TryGetProperty("systemPrompt", out _));
        Assert.Equal("Implement the reservation service", result.InputText);
        Assert.Equal("prov-root-1", result.Handle.ProviderSessionId);
    }

    [Fact]
    public async Task Child_start_carries_child_mode_and_parent_session_id()
    {
        var result = await StartRootAsync(
            MakeRequest(mode: AgentRuntimeMode.Child, prompt: "Review the diff", parentSessionId: "pi-root-1"));

        Assert.Equal("child", result.StartPayload?.GetProperty("mode").GetString());
        Assert.Equal("pi-root-1", result.StartPayload?.GetProperty("parentSessionId").GetString());
        Assert.Equal("Review the diff", result.InputText);
    }

    [Fact]
    public async Task System_prompt_option_flows_into_session_start()
    {
        var options = DefaultOptions();
        options.SystemPrompt = "You are a read-only root orchestrator.";

        var result = await StartRootAsync(MakeRequest(AgentRuntimeMode.Root, "do work"), options);

        Assert.Equal("You are a read-only root orchestrator.", result.StartPayload?.GetProperty("systemPrompt").GetString());
    }

    [Fact]
    public async Task Codex_selector_maps_to_the_Pi_openai_codex_model()
    {
        var result = await StartRootAsync(
            MakeRequest(AgentRuntimeMode.Child, "review", "pi-root-1", "codex/gpt-6-astra"));

        Assert.Equal("openai-codex/gpt-6-astra", result.StartPayload?.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Codex_default_selector_is_sent_provider_qualified_so_the_worker_stays_on_openai_codex()
    {
        var result = await StartRootAsync(
            MakeRequest(AgentRuntimeMode.Child, "review", "pi-root-1", "codex/default"));

        Assert.Equal("openai-codex/default", result.StartPayload?.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Pi_backed_provider_selector_is_sent_as_the_exact_sdk_provider_model()
    {
        var result = await StartRootAsync(
            MakeRequest(AgentRuntimeMode.Child, "review", "pi-root-1", "zai/glm-4.7"));

        Assert.Equal("zai/glm-4.7", result.StartPayload?.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Non_pi_selector_is_rejected_before_the_worker_starts()
    {
        var harness = new Harness(DefaultOptions());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => harness.Adapter.StartAsync(
                MakeRequest(AgentRuntimeMode.Child, "review", "pi-root-1", "claude-code/fable-5-1"),
                CancellationToken.None));
        Assert.Null(harness.StartPayload);
    }

    [Fact]
    public async Task Adapter_rejects_child_start_without_parent_session_id()
    {
        var harness = new Harness(DefaultOptions());
        await Assert.ThrowsAsync<ArgumentException>(
            () => harness.Adapter.StartAsync(
                MakeRequest(AgentRuntimeMode.Child, "orphan"), CancellationToken.None));
    }

    private static AgentStartRequest MakeRequest(
        AgentRuntimeMode mode,
        string prompt,
        string? parentSessionId = null,
        string model = "codex/default")
        => new(
            (mode is AgentRuntimeMode.Root
                ? PiRuntimeAdapter.RootSessionIdPrefix
                : PiRuntimeAdapter.ChildSessionIdPrefix) + Guid.NewGuid().ToString("N"),
            ProjectId.New(),
            WorkRequestId.New(),
            parentSessionId,
            "agent-" + Guid.NewGuid().ToString("N")[..8],
            mode is AgentRuntimeMode.Root ? "root" : "reviewer",
            "/tmp/picc-start-flow",
            prompt,
            mode,
            model);

    private static PiWorkerOptions DefaultOptions() => new()
    {
        WorkerPath = "/tmp/pi-worker-index.ts",
        NodeExecutable = "node",
        AgentDataDirectory = "/tmp/picc-agent-data",
        RequestTimeoutSeconds = 5,
    };

    private static async Task<Result> StartRootAsync(
        AgentStartRequest request,
        PiWorkerOptions? options = null)
    {
        var harness = new Harness(options ?? DefaultOptions());
        var handle = await harness.Adapter.StartAsync(request, CancellationToken.None);
        await harness.WaitUntilInputSeenAsync();
        await harness.Adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
        return new Result(handle, harness);
    }

    private sealed record Result(AgentSessionHandle Handle, Harness Harness)
    {
        public JsonElement? StartPayload => Harness.StartPayload;

        public string? InputText => Harness.InputText;
    }

    private sealed class Harness
    {
        private readonly TaskCompletionSource _inputSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pump;

        public Harness(PiWorkerOptions options)
        {
            Process = new FakePiProcess();
            Adapter = new PiRuntimeAdapter(
                Options.Create(new NodeOptions { Id = Guid.NewGuid(), HeartbeatSeconds = 5 }),
                Options.Create(options),
                new FakeFactory(Process),
                new NoopOrchestration(),
                TimeProvider.System,
                NullLogger<PiRuntimeAdapter>.Instance);
            _pump = PumpFramesAsync();
        }

        public PiRuntimeAdapter Adapter { get; }

        public FakePiProcess Process { get; }

        public JsonElement? StartPayload { get; private set; }

        public string? InputText { get; private set; }

        public async Task WaitUntilInputSeenAsync()
        {
            var winner = await Task.WhenAny(_inputSeen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (winner != _inputSeen.Task)
            {
                throw new TimeoutException("session.input with the start prompt never arrived.");
            }
        }

        private async Task PumpFramesAsync()
        {
            var reader = PipeReader.Create(Process.StdinReader);
            try
            {
                while (true)
                {
                    var result = await reader.ReadAsync(CancellationToken.None);
                    var buffer = result.Buffer;
                    while (buffer.PositionOf((byte)'\n') is { } newline)
                    {
                        var line = Encoding.UTF8.GetString(buffer.Slice(0, newline).ToArray());
                        HandleFrame(line);
                        buffer = buffer.Slice(buffer.GetPosition(1, newline));
                    }

                    reader.AdvanceTo(buffer.Start, buffer.End);
                    if (result.IsCompleted)
                    {
                        break;
                    }
                }
            }
            catch (Exception) when (!_inputSeen.Task.IsCompleted)
            {
            }
        }

        private void HandleFrame(string line)
        {
            PiEnvelope request;
            try
            {
                request = PiProtocol.Decode(line);
            }
            catch (PiFrameException)
            {
                return;
            }

            if (request.Type is "session.start")
            {
                StartPayload = request.Payload;
                Respond(request, new Dictionary<string, object?>
                {
                    ["sdkSessionId"] = "prov-root-1",
                    ["mode"] = request.Payload?.GetProperty("mode").GetString() ?? "root",
                });
            }
            else if (request.Type is "session.input")
            {
                InputText = request.Payload?.GetProperty("text").GetString();
                Respond(request, new Dictionary<string, object?> { ["queued"] = "prompt" });
                _inputSeen.TrySetResult();
            }
            else if (request.Type is "goodbye")
            {
                Respond(request, new Dictionary<string, object?> { ["bye"] = true });
            }
        }

        private void Respond(PiEnvelope request, IReadOnlyDictionary<string, object?> payload)
        {
            var response = new PiEnvelope(
                PiProtocol.Version,
                request.MessageId,
                PiFrameKinds.Response,
                request.SessionId,
                request.Type,
                JsonSerializer.SerializeToElement(payload));
            Process.WriteStdout(PiProtocol.Encode(response));
        }

        private sealed class FakeFactory(FakePiProcess process) : IPiWorkerProcessFactory
        {
            public IPiWorkerProcess Start(string nodeExecutable, string workerPath, string workingDirectory)
                => process;
        }

        private sealed class NoopOrchestration : IPiOrchestrationRequestHandler
        {
            public Task<PiToolResponse> HandleAsync(
                PiOrchestrationContext context,
                string requestType,
                JsonElement? payload,
                CancellationToken cancellationToken)
                => Task.FromResult(PiToolResponse.Success());
        }
    }

    private sealed class FakePiProcess : IPiWorkerProcess
    {
        private readonly Pipe _stdin = new();
        private readonly Pipe _stdout = new();
        private readonly Stream _stdoutWriter = default!;

        public FakePiProcess()
        {
            Stdin = _stdin.Writer.AsStream();
            Stdout = _stdout.Reader.AsStream();
            _stdoutWriter = _stdout.Writer.AsStream();
            var stderr = new Pipe();
            stderr.Writer.Complete();
            Stderr = stderr.Reader.AsStream();
        }

        public Stream StdinReader => _stdin.Reader.AsStream();

        public Stream Stdin { get; }

        public Stream Stdout { get; }

        public Stream Stderr { get; }

        public TaskCompletionSource<int> Exit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<int> Exited => Exit.Task;

        public Task KillTreeAsync(CancellationToken cancellationToken)
        {
            Exit.TrySetResult(137);
            return Task.CompletedTask;
        }

        public void WriteStdout(byte[] frame)
        {
            _stdoutWriter.Write(frame, 0, frame.Length);
            _stdoutWriter.Flush();
        }

        public ValueTask DisposeAsync()
        {
            _stdin.Writer.Complete();
            _stdout.Writer.Complete();
            return ValueTask.CompletedTask;
        }
    }
}
