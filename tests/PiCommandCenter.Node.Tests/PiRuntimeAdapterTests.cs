using System.Buffers;
using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Node.Quiescence;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

public sealed class PiRuntimeAdapterTests
{
    [Fact]
    public async Task Isolating_worker_is_registered_for_request_until_session_closes()
    {
        var registry = new AssignmentProcessRegistry();
        var identity = new AssignmentProcessIdentity(901, 12_345, 901, 901, "pi-worker");
        await using var harness = new Harness(isolating: true, identity, registry);
        var request = MakeRequest();

        var handle = await harness.Adapter.StartAsync(request, CancellationToken.None);
        await harness.WaitUntilInputSeenAsync();

        var snapshot = registry.Snapshot(request.RequestId.Value);
        Assert.Single(snapshot);
        Assert.Equal(identity, snapshot[0]);

        await harness.Adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);

        Assert.Empty(registry.Snapshot(request.RequestId.Value));
        var stop = await registry.StopAsync(
            request.RequestId.Value,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.False(stop.Proven);
    }

    [Fact]
    public async Task Non_isolating_worker_leaves_recovery_unproven()
    {
        var registry = new AssignmentProcessRegistry();
        await using var harness = new Harness(isolating: false, identity: null, registry);
        var request = MakeRequest();

        var handle = await harness.Adapter.StartAsync(request, CancellationToken.None);
        await harness.WaitUntilInputSeenAsync();

        Assert.Empty(registry.Snapshot(request.RequestId.Value));

        await harness.Adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);

        var stop = await registry.StopAsync(
            request.RequestId.Value,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        Assert.False(stop.Proven);
        Assert.Equal(AssignmentProcessStopResult.ProcessStopUnproven, stop.BlockerCode);
    }

    private static AgentStartRequest MakeRequest()
        => new(
            PiRuntimeAdapter.RootSessionIdPrefix + Guid.NewGuid().ToString("N"),
            ProjectId.New(),
            WorkRequestId.New(),
            parentSessionId: null,
            "agent-" + Guid.NewGuid().ToString("N")[..8],
            "root",
            "/tmp/picc-runtime-adapter",
            "do the work",
            AgentRuntimeMode.Root,
            "codex/gpt-5.6-sol");

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TaskCompletionSource _inputSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task _pump;

        public Harness(
            bool isolating,
            AssignmentProcessIdentity? identity,
            AssignmentProcessRegistry registry)
        {
            Process = isolating
                ? new IsolatingPiProcess(identity!)
                : new FakePiProcess();
            Adapter = new PiRuntimeAdapter(
                Options.Create(new NodeOptions { Id = Guid.NewGuid(), HeartbeatSeconds = 5 }),
                Options.Create(new PiWorkerOptions
                {
                    WorkerPath = "/tmp/pi-worker-index.ts",
                    NodeExecutable = "node",
                    AgentDataDirectory = "/tmp/picc-agent-data",
                    RequestTimeoutSeconds = 5,
                }),
                new FakeFactory(Process),
                new NoopOrchestration(),
                TimeProvider.System,
                NullLogger<PiRuntimeAdapter>.Instance,
                new RequestAdmissionGate(TimeProvider.System),
                gitService: null,
                processes: registry);
            _pump = PumpFramesAsync();
        }

        public PiRuntimeAdapter Adapter { get; }

        public FakePiProcess Process { get; }

        public async Task WaitUntilInputSeenAsync()
        {
            var winner = await Task.WhenAny(_inputSeen.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (winner != _inputSeen.Task)
            {
                throw new TimeoutException("session.input with the start prompt never arrived.");
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Process.DisposeAsync();
            try
            {
                await _pump.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (TimeoutException)
            {
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
                Respond(request, new Dictionary<string, object?>
                {
                    ["sdkSessionId"] = "prov-root-1",
                    ["mode"] = "root",
                });
            }
            else if (request.Type is "session.input")
            {
                Respond(request, new Dictionary<string, object?> { ["queued"] = "prompt" });
                _inputSeen.TrySetResult();
            }
            else if (request.Type is "goodbye")
            {
                Respond(request, new Dictionary<string, object?> { ["bye"] = true });
                Process.Exit.TrySetResult(0);
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

    private class FakePiProcess : IPiWorkerProcess
    {
        private readonly Pipe _stdin = new();
        private readonly Pipe _stdout = new();
        private readonly Stream _stdoutWriter;

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

    private sealed class IsolatingPiProcess : FakePiProcess, IAssignmentProcessIsolation
    {
        public IsolatingPiProcess(AssignmentProcessIdentity identity)
        {
            Identity = identity;
        }

        public AssignmentProcessIdentity? Identity { get; }

        public Task<AssignmentProcessStopResult> StopIsolatedAsync(CancellationToken cancellationToken)
            => Task.FromResult(AssignmentProcessStopResult.Stopped([Identity!]));
    }
}
