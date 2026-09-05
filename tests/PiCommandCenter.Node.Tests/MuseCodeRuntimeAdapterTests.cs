using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Node.Runtime.Muse;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Muse adapter tests against an in-memory MSP host. No OS process, provider network,
/// credentials, or model calls are involved.
/// </summary>
public sealed class MuseCodeRuntimeAdapterTests
{
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(5);

    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "devfleet-muse-tests", "ws");

    [Fact]
    public void Capabilities_describe_a_read_only_streaming_runtime()
    {
        var adapter = CreateAdapter(new FakeMuseProcessFactory());

        Assert.Equal(AgentRuntimeKinds.Muse, adapter.RuntimeKind);
        Assert.True(adapter.Capabilities.SupportsStreamingEvents);
        Assert.True(adapter.Capabilities.SupportsSendInput);
        Assert.True(adapter.Capabilities.SupportsCancel);
        Assert.True(adapter.Capabilities.SupportsSnapshot);
        Assert.False(adapter.Capabilities.SupportsChildSpawn);
        Assert.False(adapter.Capabilities.SupportsPlanTools);
    }

    [Fact]
    public async Task Write_authorization_is_rejected_before_any_host_launch()
    {
        var factory = new FakeMuseProcessFactory(new FakeMuseHost());
        var adapter = CreateAdapter(factory);

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartAsync(
            MakeRequest(authorization: new AgentRuntimeAuthorizationContext(Guid.NewGuid(), 7)),
            CancellationToken.None));

        Assert.Empty(factory.Starts);
    }

    [Fact]
    public async Task Foreign_runtime_selectors_are_rejected_before_any_host_launch()
    {
        var factory = new FakeMuseProcessFactory(new FakeMuseHost());
        var adapter = CreateAdapter(factory);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.StartAsync(MakeRequest(model: "codex/default"), CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            adapter.StartAsync(MakeRequest(model: "antigravity/gemini-3-pro"), CancellationToken.None));

        Assert.Empty(factory.Starts);
    }

    [Fact]
    public void Model_argument_is_the_selector_suffix_or_omitted_for_default()
    {
        Assert.Null(MuseCodeRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("muse/default")));
        Assert.Equal(
            "llama-4-maverick",
            MuseCodeRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("muse/llama-4-maverick")));
        Assert.Equal(
            "vendor/nested-id",
            MuseCodeRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("muse/vendor/nested-id")));
        Assert.Throws<NotSupportedException>(() =>
            MuseCodeRuntimeAdapter.ResolveModelArgument(AgentModelSelector.Parse("claude-code/default")));
    }

    [Fact]
    public async Task Launch_uses_the_read_only_serve_argv_and_the_request_working_directory()
    {
        var host = new FakeMuseHost(processId: 4242);
        var factory = new FakeMuseProcessFactory(host);
        var adapter = CreateAdapter(factory, options => options.Executable = "/opt/muse/bin/muse");

        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);

        var start = Assert.Single(factory.Starts);
        Assert.Equal("/opt/muse/bin/muse", start.Executable);
        Assert.Equal(["serve", "--disable-write", "--disable-shell", "--no-session-log"], start.Arguments.ToArray());
        Assert.Equal(Workspace, start.WorkingDirectory);
        Assert.Equal("muse-sess-1", handle.ProviderSessionId);
        Assert.Equal(AgentRuntimeKinds.Muse, handle.RuntimeKind);
        Assert.Equal(4242, adapter.GetProcessId(handle.SessionId));

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Start_handshakes_then_starts_the_session_and_submits_the_prompt()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));

        var handle = await adapter.StartAsync(MakeRequest(prompt: "Review the change"), CancellationToken.None);

        Assert.Equal(
            ["initialize", "initialized", "session/start", "turn/start"],
            host.ReceivedMethods.ToArray());

        var initialize = host.Received[0];
        Assert.Equal("devfleet", initialize.GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());
        Assert.Equal("9.9.9", initialize.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString());

        var initialized = host.Received[1];
        Assert.False(initialized.TryGetProperty("id", out _));

        var sessionStart = host.Received[2].GetProperty("params");
        AssertCommandId(sessionStart);
        Assert.Equal(Workspace, sessionStart.GetProperty("workspaceRoot").GetString());
        Assert.Equal("denyUnmatched", sessionStart.GetProperty("approvalMode").GetString());
        Assert.False(sessionStart.TryGetProperty("modelId", out _));

        var turnStart = host.Received[3].GetProperty("params");
        AssertCommandId(turnStart);
        Assert.Equal("muse-sess-1", turnStart.GetProperty("sessionId").GetString());
        var part = Assert.Single(turnStart.GetProperty("input").EnumerateArray());
        Assert.Equal("text", part.GetProperty("type").GetString());
        Assert.Equal("Review the change", part.GetProperty("text").GetString());

        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type == "turn.completed")
                && list.Any(e => e.Type == "turn.submitted"));
        var registered = Assert.Single(events, e => e.Type == "session.registered");
        Assert.Equal("muse-sess-1", PayloadString(registered, "providerSessionId"));
        Assert.Equal("denyUnmatched", PayloadString(registered, "approvalMode"));
        Assert.Contains(events, e => e.Type == "turn.submitted" && PayloadString(e, "disposition") == "started");

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Explicit_model_is_sent_as_the_session_model_id()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));

        var handle = await adapter.StartAsync(MakeRequest(model: "muse/llama-4-maverick"), CancellationToken.None);

        var sessionStart = await host.WaitForRequestAsync("session/start", EventTimeout);
        Assert.Equal("llama-4-maverick", sessionStart.GetProperty("params").GetProperty("modelId").GetString());
        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "session.registered"));
        Assert.Equal("llama-4-maverick", PayloadString(events.Single(e => e.Type == "session.registered"), "modelId"));

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Item_notifications_map_to_message_reasoning_and_tool_events()
    {
        var host = new FakeMuseHost { AutoCompleteTurns = false };
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);

        await host.NotifyAsync("turn/started", new { sessionId = "muse-sess-1", turnId = "turn-1" });
        await host.NotifyAsync("item/started", new { turnId = "turn-1", item = new { itemId = "m1", kind = "agentMessage", status = "inProgress" } });
        await host.NotifyAsync("item/delta", new { turnId = "turn-1", itemId = "m1", field = "text", delta = "hello" });
        await host.NotifyAsync("item/completed", new { turnId = "turn-1", item = new { itemId = "m1", kind = "agentMessage", status = "completed" } });
        await host.NotifyAsync("item/started", new { turnId = "turn-1", item = new { itemId = "r1", kind = "reasoning" } });
        await host.NotifyAsync("item/completed", new { turnId = "turn-1", item = new { itemId = "r1", kind = "reasoning", status = "completed" } });
        await host.NotifyAsync("item/started", new { turnId = "turn-1", item = new { itemId = "t1", kind = "toolCall", tool = "read_file", status = "inProgress" } });
        await host.NotifyAsync("item/completed", new { turnId = "turn-1", item = new { itemId = "t1", kind = "toolCall", tool = "read_file", status = "completed" } });
        await host.NotifyAsync("item/started", new { turnId = "turn-1", item = new { itemId = "t2", kind = "toolCall", tool = "run_shell" } });
        await host.NotifyAsync("item/completed", new { turnId = "turn-1", item = new { itemId = "t2", kind = "toolCall", tool = "run_shell", status = "failed" } });
        await host.NotifyAsync("item/started", new { turnId = "turn-1", item = new { itemId = "x1", kind = "futureKind" } });
        await host.NotifyAsync("turn/completed", new { turnId = "turn-1", terminal = "completed", futureProp = 1 });

        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        Assert.Contains(events, e => e.Type == "turn.started");
        Assert.Contains(events, e => e.Type == "message.started" && PayloadString(e, "itemId") == "m1");
        var delta = Assert.Single(events, e => e.Type == "message.delta");
        Assert.Equal("hello", PayloadString(delta, "delta"));
        Assert.Equal("agentMessage", PayloadString(delta, "kind"));
        Assert.Contains(events, e => e.Type == "message.completed" && PayloadString(e, "itemId") == "m1");
        Assert.Contains(events, e => e.Type == "reasoning.started");
        Assert.Contains(events, e => e.Type == "reasoning.completed");
        var toolStarted = Assert.Single(events, e => e.Type == "tool.started" && PayloadString(e, "itemId") == "t1");
        Assert.Equal("read_file", PayloadString(toolStarted, "tool"));
        Assert.Contains(events, e => e.Type == "tool.completed" && PayloadString(e, "itemId") == "t1");
        Assert.Contains(events, e => e.Type == "tool.failed" && PayloadString(e, "itemId") == "t2");
        Assert.Contains(events, e => e.Type == "item.started" && PayloadString(e, "kind") == "futureKind");
        var completed = Assert.Single(events, e => e.Type == "turn.completed");
        Assert.Equal("completed", PayloadString(completed, "terminal"));
        Assert.True(completed.Payload.ContainsKey("futureProp"));

        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Reviewing, snapshot.WorkState);
        Assert.Equal(AgentActivity.Idle, snapshot.Activity);
        Assert.Equal(AgentLiveness.Online, snapshot.Liveness);
        Assert.Equal("muse-sess-1", snapshot.ProviderSessionId);

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Session_input_approval_and_unknown_notifications_are_normalized_not_rejected()
    {
        var host = new FakeMuseHost { AutoCompleteTurns = false };
        var nodeId = Guid.NewGuid();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host), nodeId: nodeId);
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);

        await host.NotifyAsync("session/tokenUsage", new { sessionId = "muse-sess-1", inputTokens = 10 });
        await host.NotifyAsync("session/contextUsage", new { sessionId = "muse-sess-1", used = 0.2 });
        await host.NotifyAsync("session/modelChanged", new { sessionId = "muse-sess-1", modelId = "other" });
        await host.NotifyAsync("session/futureThing", new { sessionId = "muse-sess-1" });
        await host.NotifyAsync("view/gap", new { sessionId = "muse-sess-1", dropped = 3 });
        await host.NotifyAsync("telemetry/ping", new { at = 1 });
        await host.NotifyAsync("approval/requested", new { turnId = "turn-1", approvalId = "a1" });
        await host.NotifyAsync("turn/retryScheduled", new { turnId = "turn-1", delayMs = 5 });
        await host.NotifyAsync("userInput/requested", new { turnId = "turn-1", question = "continue?" });

        var untilInput = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "input.requested"));
        var blocked = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Blocked, blocked.WorkState);
        Assert.Equal(AgentAttention.InputRequired, blocked.Attention);
        Assert.Equal(AgentActivity.Idle, blocked.Activity);

        await host.NotifyAsync("userInput/settled", new { turnId = "turn-1" });
        await host.NotifyAsync("turn/completed", new { turnId = "turn-1", terminal = "completed" });
        var rest = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));
        var events = untilInput.Concat(rest).ToList();

        var usage = Assert.Single(events, e => e.Type == "session.usage");
        Assert.Equal(10, ((JsonElement)usage.Payload["inputTokens"]!).GetInt32());
        Assert.Contains(events, e => e.Type == "session.context_usage");
        Assert.Contains(events, e => e.Type == "session.model_changed");
        Assert.Contains(events, e => e.Type == "session.notification" && PayloadString(e, "method") == "session/futureThing");
        Assert.Contains(events, e => e.Type == "runtime.gap");
        Assert.Contains(events, e => e.Type == "runtime.notification" && PayloadString(e, "method") == "telemetry/ping");
        Assert.Contains(events, e => e.Type == "approval.requested");
        Assert.Contains(events, e => e.Type == "turn.retry_scheduled");
        Assert.Contains(events, e => e.Type == "input.settled");
        Assert.DoesNotContain(events, e => e.Type is "session.failed" or "session.closed");

        var released = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Reviewing, released.WorkState);
        Assert.Equal(AgentAttention.None, released.Attention);

        Assert.All(events, e =>
        {
            Assert.Equal(AgentRuntimeKinds.Muse, e.Runtime);
            Assert.Equal(handle.SessionId, e.SessionId);
            Assert.Equal(nodeId.ToString("D"), e.NodeId);
        });
        var sequences = events.Select(e => e.Sequence).ToArray();
        Assert.Equal(sequences.OrderBy(s => s).ToArray(), sequences);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Server_requests_are_declined_with_method_not_found_and_the_session_continues()
    {
        var host = new FakeMuseHost { AutoCompleteTurns = false };
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);

        await host.WriteRawAsync("""{"jsonrpc":"2.0","id":"srv-1","method":"userInput/request","params":{"question":"?"}}""");

        var reply = await host.WaitForAsync(
            frame => frame.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() == "srv-1",
            EventTimeout);
        Assert.Equal(-32601, reply.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(reply.TryGetProperty("result", out _));

        await host.NotifyAsync("turn/completed", new { turnId = "turn-1", terminal = "completed" });
        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));
        Assert.DoesNotContain(events, e => e.Type is "session.failed" or "session.closed");
        Assert.Equal(0, host.TerminateCalls);

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Turn_failure_is_reported_and_login_failures_block_for_input_instead()
    {
        var failingHost = new FakeMuseHost { AutoCompleteTurns = false };
        var failing = CreateAdapter(new FakeMuseProcessFactory(failingHost));
        var failed = await failing.StartAsync(MakeRequest(sessionId: "muse-fail"), CancellationToken.None);
        await failingHost.NotifyAsync("turn/completed", new
        {
            turnId = "turn-1",
            terminal = "failed",
            error = new { kind = "modelError", message = "model exploded" },
        });
        var failedEvents = await CollectUntilAsync(failing, failed.SessionId, list => list.Any(e => e.Type == "turn.failed"));
        Assert.Equal("modelError", PayloadString(failedEvents.Single(e => e.Type == "turn.failed"), "errorKind"));
        Assert.DoesNotContain(failedEvents, e => e.Type == "session.snapshot");
        var failedSnapshot = await failing.GetSnapshotAsync(failed.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Failed, failedSnapshot.WorkState);
        Assert.Equal(AgentAttention.Error, failedSnapshot.Attention);

        var authHost = new FakeMuseHost { AutoCompleteTurns = false };
        var auth = CreateAdapter(new FakeMuseProcessFactory(authHost));
        var blocked = await auth.StartAsync(MakeRequest(sessionId: "muse-auth"), CancellationToken.None);
        await authHost.NotifyAsync("turn/completed", new
        {
            turnId = "turn-1",
            terminal = "failed",
            error = new { kind = "authRequired", message = "Unauthorized: run muse login first" },
        });
        var authEvents = await CollectUntilAsync(auth, blocked.SessionId, list => list.Any(e => e.Type == "turn.failed"));
        var snapshotEvent = Assert.Single(authEvents, e => e.Type == "session.snapshot");
        Assert.Equal("InputRequired", PayloadString(snapshotEvent, "attention"));
        Assert.Equal("Blocked", PayloadString(snapshotEvent, "workState"));
        Assert.Contains("muse login", PayloadString(snapshotEvent, "reason"), StringComparison.OrdinalIgnoreCase);
        var authSnapshot = await auth.GetSnapshotAsync(blocked.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Blocked, authSnapshot.WorkState);
        Assert.Equal(AgentAttention.InputRequired, authSnapshot.Attention);
        Assert.Contains("muse login", authSnapshot.StatusReason, StringComparison.OrdinalIgnoreCase);

        await failing.CloseSessionAsync(failed.SessionId, CancellationToken.None);
        await auth.CloseSessionAsync(blocked.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Later_input_is_another_turn_start_on_the_same_provider_session()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        await adapter.SendAsync(handle.SessionId, new AgentInput("second turn"), CancellationToken.None);

        var second = await host.WaitForRequestAsync("turn/start", EventTimeout, occurrence: 2);
        Assert.Equal("muse-sess-1", second.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("second turn", second.GetProperty("params").GetProperty("input")[0].GetProperty("text").GetString());
        var events = await CollectUntilAsync(
            adapter,
            handle.SessionId,
            list => list.Any(e => e.Type == "turn.completed")
                && list.Any(e => e.Type == "turn.submitted" && PayloadString(e, "turnId") == "turn-2"));
        Assert.Contains(events, e => e.Type == "turn.submitted" && PayloadString(e, "turnId") == "turn-2");

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            adapter.SendAsync("no-such-session", new AgentInput("x"), CancellationToken.None));

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_sends_turn_cancel_and_waits_for_the_turn_to_settle()
    {
        var host = new FakeMuseHost { AutoCompleteTurns = false };
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);

        var inFlight = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentLiveness.Online, inFlight.Liveness);
        Assert.Equal(AgentActivity.Responding, inFlight.Activity);
        Assert.Equal(AgentWorkState.Executing, inFlight.WorkState);

        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);

        var cancel = (await host.WaitForRequestAsync("turn/cancel", EventTimeout)).GetProperty("params");
        AssertCommandId(cancel);
        Assert.Equal("muse-sess-1", cancel.GetProperty("sessionId").GetString());
        Assert.Equal("turn-1", cancel.GetProperty("turnId").GetString());
        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.cancelled"));
        Assert.DoesNotContain(events, e => e.Type is "session.cancelled" or "session.closed");
        Assert.Equal(0, host.TerminateCalls);
        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Cancelled, snapshot.WorkState);
        Assert.Equal(AgentLiveness.Online, snapshot.Liveness);

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Cancel_terminates_the_host_when_the_turn_never_settles()
    {
        var host = new FakeMuseHost { AutoCompleteTurns = false, AutoCancelTurns = false };
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host), options => options.CancelGraceSeconds = 1);
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);

        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);

        Assert.Equal(1, host.TerminateCalls);
        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "session.closed"));
        Assert.Contains(events, e => e.Type == "session.cancelled");
        Assert.DoesNotContain(events, e => e.Type == "session.failed");
        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentLiveness.Exited, snapshot.Liveness);
    }

    [Fact]
    public async Task Cancel_without_an_open_turn_sends_nothing()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        await adapter.CancelAsync(handle.SessionId, CancellationToken.None);

        Assert.DoesNotContain("turn/cancel", host.ReceivedMethods);
        Assert.Equal(0, host.TerminateCalls);

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);
    }

    [Fact]
    public async Task Malformed_frame_fails_the_session_closed()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        await host.WriteRawAsync("this is not a json-rpc frame");

        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "session.closed"));
        var failed = Assert.Single(events, e => e.Type == "session.failed");
        Assert.Contains("protocol fault", PayloadString(failed, "reason"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.TerminateCalls);
        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentLiveness.Exited, snapshot.Liveness);
        Assert.Equal(AgentWorkState.Failed, snapshot.WorkState);
        Assert.Equal(AgentAttention.Error, snapshot.Attention);
    }

    [Fact]
    public async Task Oversized_frame_fails_closed_but_a_frame_at_the_limit_is_accepted()
    {
        const int maxLineBytes = 384;
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host), options => options.MaxLineBytes = maxLineBytes);
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        await host.WriteRawAsync(PaddedNotification("future/ping", maxLineBytes));
        var accepted = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "runtime.notification"));
        Assert.Equal("future/ping", PayloadString(accepted.Single(e => e.Type == "runtime.notification"), "method"));
        Assert.DoesNotContain(accepted, e => e.Type == "session.failed");

        await host.WriteRawAsync(PaddedNotification("future/ping", maxLineBytes + 1));
        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "session.closed"));
        var failed = Assert.Single(events, e => e.Type == "session.failed");
        Assert.Contains("exceeded", PayloadString(failed, "reason"), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, host.TerminateCalls);
    }

    [Fact]
    public async Task Host_exit_with_a_login_diagnostic_blocks_for_input_not_failure()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        await host.WriteStderrAsync($"error: not signed in (token {jwt}); run `muse login` to continue");
        await host.ExitAsync(1);

        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "session.closed"));
        var snapshotEvent = Assert.Single(events, e => e.Type == "session.snapshot");
        Assert.Equal("InputRequired", PayloadString(snapshotEvent, "attention"));
        Assert.Equal("Blocked", PayloadString(snapshotEvent, "workState"));
        Assert.Contains("muse login", PayloadString(snapshotEvent, "reason"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, e => e.Type == "session.failed");
        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentWorkState.Blocked, snapshot.WorkState);
        Assert.Equal(AgentAttention.InputRequired, snapshot.Attention);
        Assert.Equal(AgentLiveness.Exited, snapshot.Liveness);
        var tail = string.Join('\n', adapter.GetStderrTail(handle.SessionId));
        Assert.DoesNotContain(jwt, tail, StringComparison.Ordinal);
        Assert.DoesNotContain(jwt, PayloadString(snapshotEvent, "diagnostic") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_host_exit_is_a_session_failure()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "turn.completed"));

        await host.WriteStderrAsync("fatal: host crashed");
        await host.ExitAsync(2);

        var events = await CollectUntilAsync(adapter, handle.SessionId, list => list.Any(e => e.Type == "session.closed"));
        var failed = Assert.Single(events, e => e.Type == "session.failed");
        Assert.Equal(2, Assert.IsType<int>(failed.Payload["exitCode"]));
        Assert.Contains("host crashed", PayloadString(failed, "stderrTail"), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type == "session.snapshot");
        var snapshot = await adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None);
        Assert.Equal(AgentLiveness.Exited, snapshot.Liveness);
        Assert.Equal(AgentWorkState.Failed, snapshot.WorkState);
    }

    [Fact]
    public async Task Close_unsubscribes_then_terminates_without_failure_events()
    {
        var host = new FakeMuseHost();
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));
        var handle = await adapter.StartAsync(MakeRequest(), CancellationToken.None);
        var collected = new List<NormalizedAgentEvent>();
        var stream = adapter.WatchAsync(handle.SessionId, CancellationToken.None);
        var watch = Task.Run(async () =>
        {
            await foreach (var item in stream)
            {
                collected.Add(item);
            }
        });

        await adapter.CloseSessionAsync(handle.SessionId, CancellationToken.None);

        var unsubscribe = await host.WaitForRequestAsync("view/unsubscribe", EventTimeout);
        Assert.Equal("muse-sess-1", unsubscribe.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal(1, host.TerminateCalls);
        await watch.WaitAsync(EventTimeout);
        Assert.Contains(collected, e => e.Type == "session.closed");
        Assert.DoesNotContain(collected, e => e.Type is "session.failed" or "session.cancelled" or "session.snapshot");
        Assert.Null(adapter.GetProcessId(handle.SessionId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => adapter.GetSnapshotAsync(handle.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task Unsupported_schema_version_fails_start_closed_before_session_start()
    {
        var host = new FakeMuseHost();
        host.Handlers["initialize"] = static (h, id, _) => h.RespondAsync(id, new { schema = new { version = 2 } });
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));

        await Assert.ThrowsAsync<NotSupportedException>(() => adapter.StartAsync(MakeRequest(), CancellationToken.None));

        Assert.DoesNotContain("session/start", host.ReceivedMethods);
        Assert.True(host.Exited.IsCompleted);
        Assert.Null(adapter.GetProcessId("muse-session-1"));
    }

    [Fact]
    public async Task Session_start_login_error_fails_start_with_login_guidance()
    {
        var host = new FakeMuseHost();
        host.Handlers["session/start"] = static (h, id, _) =>
            h.FailAsync(id, -32000, "Not signed in. Run `muse login` first.", "authRequired");
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartAsync(MakeRequest(), CancellationToken.None));

        Assert.Contains("muse login", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("turn/start", host.ReceivedMethods);
        Assert.True(host.Exited.IsCompleted);
    }

    [Fact]
    public async Task Session_start_without_a_session_id_fails_start_closed()
    {
        var host = new FakeMuseHost();
        host.Handlers["session/start"] = static (h, id, _) => h.RespondAsync(id, new { session = new { modelId = "x" } });
        var adapter = CreateAdapter(new FakeMuseProcessFactory(host));

        await Assert.ThrowsAsync<InvalidOperationException>(() => adapter.StartAsync(MakeRequest(), CancellationToken.None));

        Assert.DoesNotContain("turn/start", host.ReceivedMethods);
        Assert.True(host.Exited.IsCompleted);
    }

    private static MuseCodeRuntimeAdapter CreateAdapter(
        FakeMuseProcessFactory factory,
        Action<MuseCodeOptions>? configure = null,
        Guid? nodeId = null)
    {
        var options = new MuseCodeOptions
        {
            Executable = "muse-fake",
            StartTimeoutSeconds = 5,
            RequestTimeoutSeconds = 5,
            CancelGraceSeconds = 2,
        };
        configure?.Invoke(options);
        return new MuseCodeRuntimeAdapter(
            Options.Create(new NodeOptions { Id = nodeId ?? Guid.NewGuid(), AgentVersion = "9.9.9" }),
            Options.Create(options),
            factory,
            TimeProvider.System,
            NullLogger<MuseCodeRuntimeAdapter>.Instance);
    }

    private static AgentStartRequest MakeRequest(
        string model = "muse/default",
        string? sessionId = null,
        string prompt = "Review the change",
        AgentRuntimeAuthorizationContext? authorization = null)
        => new(
            sessionId ?? "muse-session-1",
            new ProjectId(Guid.NewGuid()),
            new WorkRequestId(Guid.NewGuid()),
            "pi-root-1",
            "Reviewer",
            "reviewer",
            Workspace,
            prompt,
            AgentRuntimeMode.Child,
            model,
            authorization);

    private static void AssertCommandId(JsonElement parameters)
    {
        var commandId = Guid.Parse(parameters.GetProperty("commandId").GetString()!);
        Assert.Equal(7, commandId.Version);
    }

    private static string PaddedNotification(string method, int byteLength)
    {
        var prefix = "{\"jsonrpc\":\"2.0\",\"method\":\"" + method + "\",\"params\":{\"pad\":\"";
        const string suffix = "\"}}";
        var padding = byteLength - Encoding.UTF8.GetByteCount(prefix) - Encoding.UTF8.GetByteCount(suffix);
        var frame = prefix + new string('x', padding) + suffix;
        Assert.Equal(byteLength, Encoding.UTF8.GetByteCount(frame));
        return frame;
    }

    private static async Task<List<NormalizedAgentEvent>> CollectUntilAsync(
        MuseCodeRuntimeAdapter adapter,
        string sessionId,
        Func<List<NormalizedAgentEvent>, bool> ready)
    {
        using var cts = new CancellationTokenSource(EventTimeout);
        var collected = new List<NormalizedAgentEvent>();
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
            throw new TimeoutException("Events: " + string.Join(",", collected.Select(e => e.Type)));
        }

        if (!ready(collected))
        {
            throw new TimeoutException("Events: " + string.Join(",", collected.Select(e => e.Type)));
        }

        return collected;
    }

    private static string? PayloadString(NormalizedAgentEvent e, string key)
        => e.Payload.TryGetValue(key, out var value)
            ? value switch
            {
                string s => s,
                JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
                _ => value?.ToString(),
            }
            : null;
}

/// <summary>Hands scripted in-memory hosts to the adapter in order and records every launch.</summary>
internal sealed class FakeMuseProcessFactory : IMuseProcessFactory
{
    private readonly Queue<FakeMuseHost> _hosts;

    public FakeMuseProcessFactory(params FakeMuseHost[] hosts)
    {
        _hosts = new Queue<FakeMuseHost>(hosts);
    }

    public List<MuseProcessStartInfo> Starts { get; } = [];

    public IMuseProcess Start(MuseProcessStartInfo startInfo)
    {
        Starts.Add(startInfo);
        if (_hosts.Count == 0)
        {
            throw new InvalidOperationException("No fake Muse host is scripted for this launch.");
        }

        return _hosts.Dequeue();
    }
}

/// <summary>
/// In-memory MSP host: newline JSON-RPC over pipes with scripted per-method handlers.
/// Records every frame the client writes so tests can assert the wire contract.
/// </summary>
internal sealed class FakeMuseHost : IMuseProcess
{
    public delegate Task RequestHandler(FakeMuseHost host, JsonElement id, JsonElement parameters);

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly Pipe _stdin = new();
    private readonly Pipe _stdout = new();
    private readonly Pipe _stderr = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _lock = new();
    private readonly List<JsonElement> _received = [];
    private TaskCompletionSource _pulse = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _turnCount;
    private int _terminateCalls;

    public FakeMuseHost(int processId = 4242)
    {
        Id = processId;
        Stdin = _stdin.Writer.AsStream();
        Stdout = _stdout.Reader.AsStream();
        Stderr = _stderr.Reader.AsStream();
        Handlers = new Dictionary<string, RequestHandler>(StringComparer.Ordinal)
        {
            ["initialize"] = static (host, id, _) => host.RespondAsync(id, new
            {
                schema = new { version = 1, fingerprint = MuseProtocol.KnownFingerprint },
                serverInfo = new { name = "muse", version = "1.0.3" },
            }),
            ["session/start"] = static (host, id, parameters) => host.RespondAsync(id, new
            {
                session = new
                {
                    sessionId = host.SessionId,
                    modelId = MuseProtocol.GetString(parameters, "modelId") ?? "muse-default",
                    providerId = "meta",
                },
            }),
            ["turn/start"] = static (host, id, parameters) => host.StartTurnAsync(id, parameters),
            ["turn/cancel"] = static (host, id, parameters) => host.CancelTurnAsync(id, parameters),
            ["view/unsubscribe"] = static (host, id, _) => host.RespondAsync(id, new { }),
            ["model/list"] = static (host, id, _) => host.RespondAsync(id, new { models = host.Models }),
        };
        _ = Task.Run(ReadLoopAsync);
    }

    public Dictionary<string, RequestHandler> Handlers { get; }

    public string SessionId { get; set; } = "muse-sess-1";

    /// <summary>When true, every <c>turn/start</c> streams one message and completes the turn.</summary>
    public bool AutoCompleteTurns { get; set; } = true;

    /// <summary>When true, <c>turn/cancel</c> settles the turn as cancelled.</summary>
    public bool AutoCancelTurns { get; set; } = true;

    public List<object> Models { get; set; } = [new { modelId = "llama-a" }, new { modelId = "llama-b" }];

    public int TerminateCalls => Volatile.Read(ref _terminateCalls);

    public IReadOnlyList<JsonElement> Received
    {
        get
        {
            lock (_lock)
            {
                return _received.ToArray();
            }
        }
    }

    /// <summary>Method of every frame the client wrote, in order; client responses appear as <c>&lt;response&gt;</c>.</summary>
    public IReadOnlyList<string> ReceivedMethods
        => Received.Select(frame => MuseProtocol.GetString(frame, "method") ?? "<response>").ToArray();

    public int Id { get; }

    public Stream Stdin { get; }

    public Stream Stdout { get; }

    public Stream Stderr { get; }

    public Task<int> Exited => _exited.Task;

    public Task TerminateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _terminateCalls);
        return ExitAsync(143);
    }

    public async ValueTask DisposeAsync()
    {
        await ExitAsync(0);
        _stdin.Reader.CancelPendingRead();
    }

    public Task RespondAsync(JsonElement id, object result)
        => WriteFrameAsync(new { jsonrpc = "2.0", id, result });

    public Task FailAsync(JsonElement id, int code, string message, string? kind = null)
        => WriteFrameAsync(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code, message, data = kind is null ? null : new { kind } },
        });

    public Task NotifyAsync(string method, object? parameters)
        => WriteFrameAsync(new { jsonrpc = "2.0", method, @params = parameters });

    public async Task WriteRawAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (_exited.Task.IsCompleted)
            {
                return;
            }

            await _stdout.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task WriteStderrAsync(string line)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (_exited.Task.IsCompleted)
            {
                return;
            }

            await _stderr.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Simulates process exit: completes stdout/stderr and resolves <see cref="Exited"/>.</summary>
    public async Task ExitAsync(int exitCode)
    {
        await _writeGate.WaitAsync();
        try
        {
            if (!_exited.TrySetResult(exitCode))
            {
                return;
            }

            await _stdout.Writer.CompleteAsync();
            await _stderr.Writer.CompleteAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public Task<JsonElement> WaitForRequestAsync(string method, TimeSpan timeout, int occurrence = 1)
        => WaitForAsync(frame => MuseProtocol.GetString(frame, "method") == method, timeout, occurrence);

    public async Task<JsonElement> WaitForAsync(Func<JsonElement, bool> predicate, TimeSpan timeout, int occurrence = 1)
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            Task pulse;
            lock (_lock)
            {
                var matches = 0;
                foreach (var frame in _received)
                {
                    if (predicate(frame) && ++matches == occurrence)
                    {
                        return frame;
                    }
                }

                pulse = _pulse.Task;
            }

            try
            {
                await pulse.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Host never received the expected frame. Received: "
                    + string.Join(",", ReceivedMethods));
            }
        }
    }

    private async Task StartTurnAsync(JsonElement id, JsonElement parameters)
    {
        var turnId = $"turn-{Interlocked.Increment(ref _turnCount)}";
        var sessionId = MuseProtocol.GetString(parameters, "sessionId");
        await RespondAsync(id, new { turnId, disposition = "started" });
        if (!AutoCompleteTurns)
        {
            return;
        }

        var itemId = turnId + "-msg";
        await NotifyAsync("turn/started", new { sessionId, turnId });
        await NotifyAsync("item/started", new { sessionId, turnId, item = new { itemId, kind = "agentMessage", status = "inProgress" } });
        await NotifyAsync("item/delta", new { sessionId, turnId, itemId, field = "text", delta = "hello" });
        await NotifyAsync("item/completed", new { sessionId, turnId, item = new { itemId, kind = "agentMessage", status = "completed" } });
        await NotifyAsync("turn/completed", new { sessionId, turnId, terminal = "completed", reason = "done" });
    }

    private async Task CancelTurnAsync(JsonElement id, JsonElement parameters)
    {
        await RespondAsync(id, new { });
        if (!AutoCancelTurns)
        {
            return;
        }

        await NotifyAsync("turn/completed", new
        {
            sessionId = MuseProtocol.GetString(parameters, "sessionId"),
            turnId = MuseProtocol.GetString(parameters, "turnId"),
            terminal = "cancelled",
        });
    }

    private Task WriteFrameAsync(object frame)
        => WriteRawAsync(JsonSerializer.Serialize(frame, Wire));

    private async Task ReadLoopAsync()
    {
        try
        {
            using var reader = new StreamReader(_stdin.Reader.AsStream(), new UTF8Encoding(false));
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                JsonElement frame;
                using (var document = JsonDocument.Parse(line))
                {
                    frame = document.RootElement.Clone();
                }

                Record(frame);
                var method = MuseProtocol.GetString(frame, "method");
                if (method is null)
                {
                    continue;
                }

                var hasId = frame.TryGetProperty("id", out var id) && id.ValueKind != JsonValueKind.Null;
                var parameters = frame.TryGetProperty("params", out var value) ? value : default;
                if (Handlers.TryGetValue(method, out var handler))
                {
                    await handler(this, hasId ? id : default, parameters);
                }
                else if (hasId)
                {
                    await FailAsync(id, MuseHostClient.MethodNotFoundCode, "method not found", "methodNotFound");
                }
            }
        }
        catch (Exception)
        {
            // The client closed stdin or the host exited; nothing further to serve.
        }
    }

    private void Record(JsonElement frame)
    {
        lock (_lock)
        {
            _received.Add(frame);
            var pulse = _pulse;
            _pulse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            pulse.TrySetResult();
        }
    }
}
