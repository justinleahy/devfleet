using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Node.Runtime.Claude;

namespace PiCommandCenter.Node.Tests;

public class ClaudeCodeRuntimeAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-claude-tests", Guid.NewGuid().ToString("N"))).FullName;

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

    private static string FakeClaudePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.mjs");

    private ClaudeCodeRuntimeAdapter CreateAdapter()
    {
        var settings = Path.Combine(_root, "settings.json");
        File.WriteAllText(settings, """{"permissions":{"defaultMode":"dontAsk"}}""");
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(FakeClaudePath, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        }

        return new ClaudeCodeRuntimeAdapter(
            Options.Create(new NodeOptions { Id = Guid.NewGuid() }),
            Options.Create(new ClaudeCodeOptions
            {
                Executable = FakeClaudePath,
                SettingsPath = settings,
                StartTimeoutSeconds = 10,
                CancelGraceMilliseconds = 400,
                MaxLineBytes = 65_536,
                MaxMalformedEvents = 8,
                MaxStderrLines = 20,
            }),
            new OfficialAgentProcessFactory(),
            TimeProvider.System,
            NullLogger<ClaudeCodeRuntimeAdapter>.Instance);
    }

    private AgentStartRequest Request(
        string profile = ClaudeCodeProfiles.ReadOnly,
        string? cwd = null,
        AgentRuntimeAuthorizationContext? authorization = null)
        => new(
            sessionId: "claude-child-1",
            projectId: ProjectId.New(),
            requestId: WorkRequestId.New(),
            parentSessionId: "pi-root-1",
            agentName: "claude-reviewer",
            role: "reviewer",
            workingDirectory: cwd ?? _root,
            prompt: "Review the change",
            mode: AgentRuntimeMode.Child,
            runtimeProfile: profile,
            authorization: authorization);

    private static async Task<List<PiCommandCenter.Domain.Sessions.NormalizedAgentEvent>> CollectAsync(
        IAsyncEnumerable<PiCommandCenter.Domain.Sessions.NormalizedAgentEvent> watch,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var events = new List<PiCommandCenter.Domain.Sessions.NormalizedAgentEvent>();
        try
        {
            await foreach (var item in watch.WithCancellation(cts.Token))
            {
                events.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
        }

        return events;
    }

    [Fact]
    public void Capabilities_match_the_headless_print_contract()
    {
        var adapter = CreateAdapter();
        Assert.Equal(AgentRuntimeKinds.ClaudeCode, adapter.RuntimeKind);
        Assert.True(adapter.Capabilities.SupportsStreamingEvents);
        Assert.False(adapter.Capabilities.SupportsSendInput);
        Assert.True(adapter.Capabilities.SupportsCancel);
        Assert.True(adapter.Capabilities.SupportsSnapshot);
        Assert.False(adapter.Capabilities.SupportsChildSpawn);
        Assert.False(adapter.Capabilities.SupportsPlanTools);
    }

    [Fact]
    public async Task Start_launches_exact_argv_cwd_and_does_not_invent_credential_env()
    {
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(), CancellationToken.None);

        Assert.Equal("claude-session-fake-1", handle.ProviderSessionId);
        Assert.Equal(AgentRuntimeKinds.ClaudeCode, handle.RuntimeKind);
        Assert.NotNull(adapter.GetProcessId(handle.SessionId));

        var capturePath = Path.Combine(_root, "claude-capture.json");
        var json = await WaitForFileAsync(capturePath);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var argv = doc.RootElement.GetProperty("argv").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(FakeClaudePath, argv[0]);
        Assert.Equal(
            new[]
            {
                "-p", "Review the change", "--output-format", "stream-json", "--verbose", "--settings",
                Path.Combine(_root, "settings.json"), "--setting-sources", string.Empty,
                "--permission-mode", "dontAsk",
            },
            argv.Skip(1).ToArray());
        Assert.Equal(_root, doc.RootElement.GetProperty("cwd").GetString());

        var envKeys = doc.RootElement.GetProperty("envKeys").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", envKeys);
        Assert.False(argv.Contains("--bare"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".claude")));
    }

    [Fact]
    public async Task Watch_normalizes_init_retry_tool_delta_result_and_usage()
    {
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(
            Request(ClaudeCodeProfiles.ReservedWrite, authorization: new AgentRuntimeAuthorizationContext(Guid.NewGuid(), 1)),
            CancellationToken.None);
        var events = await CollectAsync(adapter.WatchAsync(handle.SessionId, CancellationToken.None), TimeSpan.FromSeconds(8));

        Assert.Contains(events, e => e.Type == "session.registered");
        Assert.Contains(events, e => e.Type == "session.started");
        Assert.Contains(events, e => e.Type == "runtime.retry");
        Assert.Contains(events, e => e.Type == "tool.started");
        Assert.Contains(events, e => e.Type == "tool.completed");
        Assert.Contains(events, e => e.Type == "message.delta");
        var result = Assert.Single(events, e => e.Type == "result.completed");
        Assert.True(result.Payload.ContainsKey("usage"));
        Assert.Contains(events, e => e.Type == "session.closed");
        Assert.All(events, e => Assert.Equal(AgentRuntimeKinds.ClaudeCode, e.Runtime));

        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentRuntimeKinds.ClaudeCode, snapshot.RuntimeKind);
        Assert.Equal("claude-session-fake-1", snapshot.ProviderSessionId);
    }

    [Fact]
    public async Task Malformed_and_unknown_events_are_tolerated()
    {
        File.WriteAllText(Path.Combine(_root, "fake-scenario"), "malformed");
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(), CancellationToken.None);
        var events = await CollectAsync(adapter.WatchAsync(handle.SessionId, CancellationToken.None), TimeSpan.FromSeconds(8));

        Assert.Contains(events, e => e.Type == "runtime.malformed_line");
        Assert.Contains(events, e => e.Type == "mystery_event");
        Assert.Contains(events, e => e.Type == "result.completed");
    }

    [Fact]
    public async Task Crash_after_init_synthesizes_failed_and_closed()
    {
        File.WriteAllText(Path.Combine(_root, "fake-scenario"), "crash");
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(), CancellationToken.None);
        var events = await CollectAsync(adapter.WatchAsync(handle.SessionId, CancellationToken.None), TimeSpan.FromSeconds(8));
        Assert.Contains(events, e => e.Type == "session.failed");
        Assert.Contains(events, e => e.Type == "session.closed");
    }

    [Fact]
    public async Task Auth_missing_emits_blocked_input_required_not_generic_failure()
    {
        File.WriteAllText(Path.Combine(_root, "fake-scenario"), "auth");
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(), CancellationToken.None);
        var events = await CollectAsync(adapter.WatchAsync(handle.SessionId, CancellationToken.None), TimeSpan.FromSeconds(8));
        var snapshot = events.First(e => e.Type == "session.snapshot");
        Assert.Equal("InputRequired", snapshot.Payload["attention"]?.ToString());
        Assert.Equal("Blocked", snapshot.Payload["workState"]?.ToString());
        Assert.Contains("claude login", snapshot.Payload["reason"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, e => e.Type == "session.failed");
        var snap = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(PiCommandCenter.Domain.Sessions.AgentAttention.InputRequired, snap.Attention);
        Assert.Equal(PiCommandCenter.Domain.Sessions.AgentWorkState.Blocked, snap.WorkState);
        var tail = string.Join('\n', adapter.GetStderrTail(handle.SessionId));
        Assert.DoesNotContain("sk-ant-secret", tail, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Cancel_sends_sigint_then_process_exits()
    {
        File.WriteAllText(Path.Combine(_root, "fake-scenario"), "hang");
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(), CancellationToken.None);
        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);
        var events = await CollectAsync(adapter.WatchAsync(handle.SessionId, CancellationToken.None), TimeSpan.FromSeconds(8));
        Assert.Contains(events, e => e.Type is "session.cancelled" or "session.closed");
    }

    [Fact]
    public async Task Unsupported_profile_and_root_mode_are_rejected()
    {
        var adapter = CreateAdapter();
        var badProfile = Request("local-pi");
        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.StartAsync(badProfile, CancellationToken.None));

        var root = new AgentStartRequest(
            "root-1",
            ProjectId.New(),
            WorkRequestId.New(),
            parentSessionId: null,
            agentName: "root",
            role: "root",
            workingDirectory: _root,
            prompt: "plan",
            mode: AgentRuntimeMode.Root,
            runtimeProfile: ClaudeCodeProfiles.ReadOnly);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.StartAsync(root, CancellationToken.None));
    }

    [Fact]
    public async Task Reserved_write_without_authorization_is_refused()
    {
        var adapter = CreateAdapter();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => adapter.StartAsync(Request(ClaudeCodeProfiles.ReservedWrite), CancellationToken.None));
    }

    [Fact]
    public async Task Send_is_not_supported()
    {
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(), CancellationToken.None);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.SendAsync(handle.SessionId, new AgentInput("more"), CancellationToken.None));
    }

    private static async Task<string> WaitForFileAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                return await File.ReadAllTextAsync(path);
            }

            await Task.Delay(20);
        }

        throw new FileNotFoundException(path);
    }
}
