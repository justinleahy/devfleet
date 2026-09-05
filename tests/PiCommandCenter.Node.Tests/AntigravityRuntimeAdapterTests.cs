using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Node.Runtime.Antigravity;

namespace PiCommandCenter.Node.Tests;

[CollectionDefinition("Antigravity process tests", DisableParallelization = true)]
public sealed class AntigravityProcessTestCollection;

/// <summary>
/// Focused Antigravity adapter tests against a fake official-compatible executable.
/// No provider network, credentials, or model calls.
/// </summary>
[Collection("Antigravity process tests")]
public sealed class AntigravityRuntimeAdapterTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pi-cc-agy-tests", Guid.NewGuid().ToString("N"))).FullName;

    private static string FakeScript => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "TestData", "fake-agy.mjs"));

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
    public void Capabilities_omit_unsupported_controls()
    {
        var adapter = CreateAdapter(_root, Path.Combine(_root, "dump.json"));
        Assert.Equal(AgentRuntimeKinds.Antigravity, adapter.RuntimeKind);
        Assert.True(adapter.Capabilities.SupportsStreamingEvents);
        Assert.True(adapter.Capabilities.SupportsSendInput);
        Assert.True(adapter.Capabilities.SupportsCancel);
        Assert.True(adapter.Capabilities.SupportsSnapshot);
        Assert.False(adapter.Capabilities.SupportsChildSpawn);
        Assert.False(adapter.Capabilities.SupportsPlanTools);
    }

    [Fact]
    public async Task Write_authorization_is_rejected_before_launch()
    {
        var dump = Path.Combine(_root, "dump.json");
        var adapter = CreateAdapter(_root, dump);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.StartAsync(
                MakeRequest(_root, authorization: new AgentRuntimeAuthorizationContext(Guid.NewGuid(), 7)),
                CancellationToken.None));
        Assert.False(File.Exists(dump));
    }

    [Fact]
    public async Task Foreign_runtime_selectors_are_rejected_before_launch()
    {
        var dump = Path.Combine(_root, "dump.json");
        var adapter = CreateAdapter(_root, dump);
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.StartAsync(MakeRequest(_root, "codex/default"), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.StartAsync(MakeRequest(_root, "claude-code/fable-5-1"), CancellationToken.None));
        Assert.False(File.Exists(dump));
    }

    [Fact]
    public void Model_argument_is_the_selector_suffix_or_omitted_for_default()
    {
        Assert.Null(AntigravityRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("antigravity/default")));
        Assert.Equal(
            "gemini-3-pro",
            AntigravityRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("antigravity/gemini-3-pro")));
        Assert.Equal(
            "vendor/nested-id",
            AntigravityRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("antigravity/vendor/nested-id")));
        Assert.Equal(
            ["--input-format", "stream-json", "--output-format", "stream-json"],
            AntigravityRuntimeAdapter.BuildLaunchArguments(null).ToArray());
    }

    [Fact]
    public async Task Read_only_boundary_blocks_workspace_and_symlink_escape_writes()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "repo")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(_root, "sibling")).FullName;
        Directory.CreateSymbolicLink(Path.Combine(cwd, "escape"), sibling);
        Directory.CreateSymbolicLink(Path.Combine(cwd, "proc"), "/proc");
        var escapedTarget = Path.Combine(cwd, "escape", "MUTATED.txt");
        var directTarget = Path.Combine(cwd, "DIRECT.txt");
        var procEscapeTarget = Path.Combine(
            cwd,
            "proc",
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "root",
            sibling.TrimStart(Path.DirectorySeparatorChar),
            "PROC-MUTATED.txt");
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/usr/bin/touch",
            WorkingDirectory = cwd,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(directTarget);
        startInfo.ArgumentList.Add(escapedTarget);
        startInfo.ArgumentList.Add(procEscapeTarget);
        AntigravityReadOnlySandbox.Apply(startInfo, cwd);

        using var process = System.Diagnostics.Process.Start(startInfo);
        Assert.NotNull(process);
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.False(File.Exists(directTarget));
        Assert.False(File.Exists(Path.Combine(sibling, "MUTATED.txt")));
        Assert.False(File.Exists(Path.Combine(sibling, "PROC-MUTATED.txt")));
    }

    [Fact]
    public void Missing_bwrap_fails_actionably()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AntigravityReadOnlySandbox.Apply(
                new System.Diagnostics.ProcessStartInfo { FileName = "agy" },
                _root,
                Path.Combine(_root, "no-such-bwrap")));
        Assert.Contains("BLOCKED", ex.Message, StringComparison.Ordinal);
        Assert.Contains("bwrap", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Launch_uses_stream_json_argv_and_working_directory()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "ws")).FullName;
        var dump = Path.Combine(cwd, "launch.json");
        var adapter = CreateAdapter(cwd, dump);
        var handle = await adapter.StartAsync(
            MakeRequest(cwd, "antigravity/agy-role-model"),
            CancellationToken.None);
        Assert.Equal("agy-conv-1", handle.ProviderSessionId);
        Assert.Equal(AgentRuntimeKinds.Antigravity, handle.RuntimeKind);
        Assert.True(adapter.GetProcessId(handle.SessionId) is > 0);

        using var dumpJson = JsonDocument.Parse(await File.ReadAllTextAsync(dump));
        var argv = dumpJson.RootElement.GetProperty("argv").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(
            ["--input-format", "stream-json", "--output-format", "stream-json", "--model", "agy-role-model"],
            argv);
        Assert.Equal(cwd, dumpJson.RootElement.GetProperty("cwd").GetString());

        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Init_is_exactly_once_and_events_map()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "map")).FullName;
        var dump = Path.Combine(cwd, "init.json");
        var adapter = CreateAdapter(cwd, dump);
        var handle = await adapter.StartAsync(MakeRequest(cwd), CancellationToken.None);

        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type == "turn.completed"),
            TimeSpan.FromSeconds(5));

        Assert.Equal(1, events.Count(e => e.Type == "session.registered"));
        Assert.Contains(events, e => e.Type == "turn.started");
        Assert.Contains(events, e => e.Type == "message.delta");
        Assert.Contains(events, e => e.Type == "message.completed");
        Assert.Contains(events, e => e.Type == "tool.started");
        Assert.Contains(events, e => e.Type == "tool.completed");
        Assert.Contains(events, e => e.Type == "checkpoint");
        Assert.Contains(events, e => e.Type == "mystery_step");
        Assert.Contains(events, e => e.Payload.ContainsKey("subagent_info") || NestedHas(e, "subagent_info"));
        var completed = events.Last(e => e.Type == "turn.completed");
        Assert.True(completed.Payload.ContainsKey("usage"));
        Assert.Equal("SUCCESS", PayloadString(completed, "status"));

        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Second_prompt_waits_for_first_result()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "serial")).FullName;
        var dump = Path.Combine(cwd, "serial.json");
        var adapter = CreateAdapter(cwd, dump);
        var handle = await adapter.StartAsync(MakeRequest(cwd), CancellationToken.None);

        using var watchCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var seen = new List<Domain.Sessions.NormalizedAgentEvent>();
        var watch = Task.Run(async () =>
        {
            await foreach (var item in adapter.WatchAsync(handle.SessionId, watchCts.Token))
            {
                seen.Add(item);
            }
        });

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (seen.Count(e => e.Type == "turn.completed") < 1 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        await adapter.SendAsync(handle.SessionId, new AgentInput("second turn"), CancellationToken.None);
        deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (seen.Count(e => e.Type == "turn.completed") < 2 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(seen.Count(e => e.Type == "turn.completed") >= 2);

        Assert.False(File.Exists(dump + ".overlap"));
        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Unknown_and_malformed_lines_are_tolerated()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "unk")).FullName;
        var dump = Path.Combine(cwd, "unknown.json");
        var adapter = CreateAdapter(cwd, dump, mode: "unknown");
        var handle = await adapter.StartAsync(MakeRequest(cwd), CancellationToken.None);
        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type == "turn.completed"),
            TimeSpan.FromSeconds(5));
        Assert.Contains(events, e => e.Type == "future_event");

        var malformedAdapter = CreateAdapter(cwd, dump + ".m", mode: "malformed");
        var malformed = await malformedAdapter.StartAsync(
            MakeRequest(cwd, sessionId: "agy-malformed-1"),
            CancellationToken.None);
        var malformedEvents = await CollectUntilAsync(
            malformedAdapter,
            malformed.SessionId,
            list => list.Any(e => e.Type == "turn.completed"),
            TimeSpan.FromSeconds(5));
        Assert.Contains(malformedEvents, e => e.Type == "runtime.malformed");

        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);
        await malformedAdapter.CancelAsync(malformed.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Error_result_and_nonzero_exit_are_normalized()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "err")).FullName;
        var dump = Path.Combine(cwd, "err.json");
        var adapter = CreateAdapter(cwd, dump, mode: "error");
        var handle = await adapter.StartAsync(MakeRequest(cwd), CancellationToken.None);
        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type == "turn.failed"),
            TimeSpan.FromSeconds(5));
        Assert.Contains(events, e => e.Type == "turn.failed");
        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentRuntimeKinds.Antigravity, snapshot.RuntimeKind);
        Assert.Equal("agy-conv-1", snapshot.ProviderSessionId);

        var crashAdapter = CreateAdapter(cwd, dump + ".c", mode: "crash");
        var crashed = await crashAdapter.StartAsync(
            MakeRequest(cwd, sessionId: "agy-crash-1"),
            CancellationToken.None);
        var crashEvents = await CollectUntilAsync(
            crashAdapter,
            crashed.SessionId,
            list => list.Any(e => e.Type == "session.failed") && list.Any(e => e.Type == "session.closed"),
            TimeSpan.FromSeconds(5));
        Assert.Contains(crashEvents, e => e.Type == "session.failed");
        Assert.Contains(crashEvents, e => e.Type == "session.closed");
    }

    [Fact]
    public async Task Auth_missing_emits_blocked_input_required_not_generic_failure()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "auth")).FullName;
        var dump = Path.Combine(cwd, "auth.json");
        var adapter = CreateAdapter(cwd, dump, mode: "auth");
        var handle = await adapter.StartAsync(MakeRequest(cwd), CancellationToken.None);
        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type is "session.snapshot" or "session.closed"),
            TimeSpan.FromSeconds(8));
        var snapshot = events.First(e => e.Type == "session.snapshot");
        Assert.Equal("InputRequired", snapshot.Payload["attention"]?.ToString());
        Assert.Equal("Blocked", snapshot.Payload["workState"]?.ToString());
        Assert.Contains("agy login", snapshot.Payload["reason"]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, e => e.Type == "session.failed");
        var snap = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(Domain.Sessions.AgentAttention.InputRequired, snap.Attention);
        Assert.Equal(Domain.Sessions.AgentWorkState.Blocked, snap.WorkState);
        var tail = string.Join('\n', adapter.GetStderrTail(handle.SessionId));
        Assert.DoesNotContain("hunter2", tail, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9", tail, StringComparison.Ordinal);
    }


    [Fact]
    public async Task Cancel_interrupts_a_hanging_turn()
    {
        var cwd = Directory.CreateDirectory(Path.Combine(_root, "hang")).FullName;
        var dump = Path.Combine(cwd, "hang.json");
        var adapter = CreateAdapter(cwd, dump, mode: "hang");
        var handle = await adapter.StartAsync(MakeRequest(cwd), CancellationToken.None);
        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);
        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type is "session.cancelled" or "session.closed"),
            TimeSpan.FromSeconds(5));
        Assert.Contains(events, e => e.Type is "session.cancelled" or "session.closed");
    }

    private AntigravityRuntimeAdapter CreateAdapter(
        string cwd,
        string dump,
        string mode = "happy",
        bool tryWrite = false)
    {
        var nodeOptions = Options.Create(new NodeOptions { Id = Guid.NewGuid() });
        var options = Options.Create(new AntigravityOptions
        {
            Executable = "node",
            StartTimeoutSeconds = 20,
            CancelGraceSeconds = 2,
        });
        return new AntigravityRuntimeAdapter(
            nodeOptions,
            options,
            new ScriptProcessFactory(FakeScript, dump, mode, tryWrite),
            TimeProvider.System,
            NullLogger<AntigravityRuntimeAdapter>.Instance);
    }

    private static AgentStartRequest MakeRequest(
        string cwd,
        string model = "antigravity/default",
        string? sessionId = null,
        AgentRuntimeAuthorizationContext? authorization = null)
        => new(
            sessionId ?? "agy-session-1",
            new ProjectId(Guid.NewGuid()),
            new WorkRequestId(Guid.NewGuid()),
            "pi-root-1",
            "Reviewer",
            "reviewer",
            cwd,
            "Review the change",
            AgentRuntimeMode.Child,
            model,
            authorization);

    private static async Task<List<Domain.Sessions.NormalizedAgentEvent>> CollectUntilAsync(
        AntigravityRuntimeAdapter adapter,
        string sessionId,
        Func<List<Domain.Sessions.NormalizedAgentEvent>, bool> ready,
        TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var collected = new List<Domain.Sessions.NormalizedAgentEvent>();
        try
        {
            await foreach (var item in adapter.WatchAsync(sessionId, cts.Token))
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
            throw new TimeoutException(
                "Events: " + string.Join(",", collected.Select(e => e.Type)));
        }

        if (!ready(collected))
        {
            throw new TimeoutException(
                "Events: " + string.Join(",", collected.Select(e => e.Type)));
        }

        return collected;
    }

    private static string? PayloadString(Domain.Sessions.NormalizedAgentEvent e, string key)
        => e.Payload.TryGetValue(key, out var value)
            ? value switch
            {
                string s => s,
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                _ => value?.ToString(),
            }
            : null;

    private static bool NestedHas(Domain.Sessions.NormalizedAgentEvent e, string key)
    {
        if (e.Payload.TryGetValue("step_update", out var step) && step is JsonElement el
            && el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out _))
        {
            return true;
        }

        return e.Payload.ContainsKey(key);
    }

    private sealed record LaunchDump(string[] Argv, string Cwd);

    private sealed class ScriptProcessFactory : IAntigravityProcessFactory
    {
        private readonly string _script;
        private readonly string _dump;
        private readonly string _mode;
        private readonly bool _tryWrite;
        private readonly AntigravityProcessFactory _inner = new();

        public ScriptProcessFactory(string script, string dump, string mode, bool tryWrite)
        {
            _script = script;
            _dump = dump;
            _mode = mode;
            _tryWrite = tryWrite;
        }

        public IAntigravityProcess Start(AntigravityProcessStartInfo startInfo)
        {
            File.WriteAllText(
                _dump,
                JsonSerializer.Serialize(new
                {
                    argv = startInfo.Arguments,
                    cwd = startInfo.WorkingDirectory,
                }));
            var psi = new AntigravityProcessStartInfo(
                "node",
                new[] { _script }.Concat(startInfo.Arguments).ToArray(),
                startInfo.WorkingDirectory,
                new Dictionary<string, string>
                {
                    ["AGY_TEST_DUMP"] = string.Empty,
                    ["AGY_TEST_MODE"] = _mode,
                    ["AGY_TEST_TRY_WRITE"] = _tryWrite ? "1" : "0",
                });
            return _inner.Start(psi);
        }
    }
}
