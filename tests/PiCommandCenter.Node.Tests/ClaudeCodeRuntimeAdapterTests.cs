using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Runtime.Claude;
using PiCommandCenter.Node.Runtime.Claude.Hooks;

namespace PiCommandCenter.Node.Tests;

public class ClaudeCodeRuntimeAdapterTests : IDisposable
{
    private const string DefaultModel = "claude-code/default";

    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-claude-tests", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _hookData = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-claude-hook-data", Guid.NewGuid().ToString("N"))).FullName;
    private ClaudeReservationHookServer? _hookServer;

    public void Dispose()
    {
        _hookServer?.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
            Directory.Delete(_hookData, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static string FakeClaudePath =>
        Path.Combine(AppContext.BaseDirectory, "TestData", "fake-claude.mjs");

    private ClaudeCodeRuntimeAdapter CreateAdapter(bool withHookInstaller = false)
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

        ClaudeHookSettingsInstaller? installer = null;
        if (withHookInstaller)
        {
            _hookServer = new ClaudeReservationHookServer(
                new ClaudeReservationHookEvaluator(new FakeReservationGateway(), new ClaudeHookAuditLog()));
            installer = new ClaudeHookSettingsInstaller(_hookServer, _hookData);
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
            NullLogger<ClaudeCodeRuntimeAdapter>.Instance,
            installer);
    }

    private AgentStartRequest Request(
        string model = DefaultModel,
        AgentRuntimeAuthorizationContext? authorization = null)
        => new(
            sessionId: "claude-child-1",
            projectId: ProjectId.New(),
            requestId: WorkRequestId.New(),
            parentSessionId: "pi-root-1",
            agentName: "claude-reviewer",
            role: "reviewer",
            workingDirectory: _root,
            prompt: "Review the change",
            mode: AgentRuntimeMode.Child,
            model: model,
            authorization: authorization);

    private static AgentRuntimeAuthorizationContext Grant() => new(Guid.NewGuid(), 1);

    private static async Task<List<NormalizedAgentEvent>> CollectAsync(
        IAsyncEnumerable<NormalizedAgentEvent> watch,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var events = new List<NormalizedAgentEvent>();
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

    private async Task<string[]> CapturedArgvAsync()
    {
        var json = await WaitForFileAsync(Path.Combine(_root, "claude-capture.json"));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("argv").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
    }

    private static string[] InstalledAllowList(string[] argv)
    {
        var settingsPath = argv[Array.IndexOf(argv, "--settings") + 1];
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return doc.RootElement.GetProperty("permissions").GetProperty("allow").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
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
        var handle = await adapter.StartAsync(Request(model: "claude-code/fable-5-1"), CancellationToken.None);

        Assert.Equal("claude-session-fake-1", handle.ProviderSessionId);
        Assert.Equal(AgentRuntimeKinds.ClaudeCode, handle.RuntimeKind);
        Assert.NotNull(adapter.GetProcessId(handle.SessionId));

        var json = await WaitForFileAsync(Path.Combine(_root, "claude-capture.json"));
        using var doc = JsonDocument.Parse(json);
        var argv = doc.RootElement.GetProperty("argv").EnumerateArray()
            .Select(e => e.GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(FakeClaudePath, argv[0]);
        Assert.Equal(
            new[]
            {
                "-p", "Review the change", "--output-format", "stream-json", "--verbose", "--settings",
                Path.Combine(_root, "settings.json"), "--setting-sources", string.Empty,
                "--permission-mode", "dontAsk", "--model", "fable-5-1",
            },
            argv.Skip(1).ToArray());
        Assert.Equal(_root, doc.RootElement.GetProperty("cwd").GetString());

        var envKeys = doc.RootElement.GetProperty("envKeys").EnumerateArray().Select(e => e.GetString()).ToHashSet();
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", envKeys);
        Assert.False(argv.Contains("--bare"));
        Assert.False(Directory.Exists(Path.Combine(_root, ".claude")));
    }

    [Fact]
    public async Task Default_model_omits_model_flag()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(Request(), CancellationToken.None);

        var argv = await CapturedArgvAsync();
        Assert.DoesNotContain("--model", argv);
    }

    [Fact]
    public async Task Slashed_model_id_is_forwarded_verbatim()
    {
        var adapter = CreateAdapter();
        await adapter.StartAsync(Request(model: "claude-code/vendor/fable-5-1"), CancellationToken.None);

        var argv = await CapturedArgvAsync();
        Assert.Equal("vendor/fable-5-1", argv[Array.IndexOf(argv, "--model") + 1]);
    }

    [Fact]
    public async Task Non_claude_selector_is_rejected_before_launch()
    {
        var adapter = CreateAdapter();
        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.StartAsync(Request(model: "codex/default"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "claude-capture.json")));
    }

    [Fact]
    public async Task Without_authorization_installed_settings_are_read_only()
    {
        var adapter = CreateAdapter(withHookInstaller: true);
        await adapter.StartAsync(Request(), CancellationToken.None);

        var argv = await CapturedArgvAsync();
        var allow = InstalledAllowList(argv);
        Assert.Equal(new[] { "Read", "Glob", "Grep" }, allow);
    }

    [Fact]
    public async Task Authorization_alone_grants_write_capable_settings()
    {
        var adapter = CreateAdapter(withHookInstaller: true);
        await adapter.StartAsync(Request(authorization: Grant()), CancellationToken.None);

        var argv = await CapturedArgvAsync();
        var allow = InstalledAllowList(argv);
        Assert.Contains("Edit", allow);
        Assert.Contains("Write", allow);
    }

    [Fact]
    public async Task Watch_normalizes_init_retry_tool_delta_result_and_usage()
    {
        var adapter = CreateAdapter();
        var handle = await adapter.StartAsync(Request(authorization: Grant()), CancellationToken.None);
        var events = await CollectAsync(adapter.WatchAsync(handle.SessionId, CancellationToken.None), TimeSpan.FromSeconds(8));

        Assert.Contains(events, e => e.Type == "session.registered");
        var started = Assert.Single(events, e => e.Type == "session.started");
        Assert.Equal(DefaultModel, started.Payload["model"]);
        Assert.False(started.Payload.ContainsKey("profile"));
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
        Assert.Equal(AgentAttention.InputRequired, snap.Attention);
        Assert.Equal(AgentWorkState.Blocked, snap.WorkState);
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
    public async Task Root_mode_is_rejected()
    {
        var adapter = CreateAdapter();
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
            model: DefaultModel);
        await Assert.ThrowsAsync<NotSupportedException>(
            () => adapter.StartAsync(root, CancellationToken.None));
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
