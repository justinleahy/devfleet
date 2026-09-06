using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Web.Components.Requests;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Covers the reducer behind the request page's "Ongoing progress" section: it must read a live
/// request's durable facts (including Pi's nested payload shape), freeze a terminal request's
/// duration at the last durable timestamp, keep only the newest few facts in newest-first order,
/// and never surface model-authored response or thinking text.
/// </summary>
public class RequestExecutionProgressTests
{
    private static readonly DateTimeOffset Queued = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_live_request_reports_phase_elapsed_tools_and_agent_operations()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(1));
        var sessions = new[]
        {
            Session(
                "sess-root",
                AgentActivity.RunningTool,
                startedAt: Queued.AddSeconds(30),
                currentOperation: "read_file",
                statusReason: "Running read_file"),
        };
        var events = new[]
        {
            Event(1, "session.registered", Queued.AddSeconds(30), """{"role":"implementer"}"""),
            Event(2, "request.phase_changed", Queued.AddSeconds(40), """{"phase":"execute"}"""),
            // Pi wraps every normalized body as { seq, timestamp, data }.
            Event(3, "tool.started", Queued.AddSeconds(50), """{"seq":3,"data":{"toolName":"read_file"}}"""),
            Event(4, "tool.progress", Queued.AddSeconds(70), """{"seq":4,"data":{"toolName":"read_file"}}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            sessions,
            events,
            Queued.AddSeconds(100));

        Assert.True(progress.IsRunning);
        Assert.False(progress.IsTerminal);
        Assert.Equal("execute", progress.Phase);
        Assert.Equal("Executing", progress.Status);
        // Anchored at the first session start, running to the observation time.
        Assert.Equal(TimeSpan.FromSeconds(70), progress.Elapsed);
        Assert.Equal(Queued.AddSeconds(70), progress.LastActivityAt);
        Assert.Equal(4, progress.EventCount);
        Assert.Equal(1, progress.ToolCallCount);

        var operation = Assert.Single(progress.Operations);
        Assert.Equal("sess-root", operation.SessionId);
        Assert.Equal(AgentActivity.RunningTool, operation.Activity);
        Assert.Equal("read_file", operation.Operation);

        // Newest first, and the nested tool name is read out of Pi's `data` object.
        Assert.Equal("read_file reported progress", progress.Facts[0].Label);
        Assert.Equal("Started read_file", progress.Facts[1].Label);
        Assert.Equal("Phase changed to execute", progress.Facts[2].Label);
        Assert.Equal("Agent session registered \u2014 implementer", progress.Facts[3].Label);
    }

    [Fact]
    public void A_terminal_request_freezes_its_duration_at_the_last_durable_fact()
    {
        var completedAt = Queued.AddMinutes(5);
        var request = Request(WorkRequestStatus.Completed, updatedAt: completedAt);
        var sessions = new[]
        {
            Session(
                "sess-root",
                AgentActivity.Idle,
                startedAt: Queued.AddMinutes(1),
                currentOperation: null,
                statusReason: "Session completed",
                liveness: AgentLiveness.Exited,
                workState: AgentWorkState.Completed,
                endedAt: completedAt),
        };
        var events = new[]
        {
            Event(1, "request.completed", completedAt, "{}"),
        };

        var soon = RequestExecutionProgressReader.Read(request, sessions, events, completedAt.AddSeconds(5));
        var muchLater = RequestExecutionProgressReader.Read(request, sessions, events, completedAt.AddDays(3));

        Assert.False(soon.IsRunning);
        Assert.True(soon.IsTerminal);
        Assert.Equal(TimeSpan.FromMinutes(4), soon.Elapsed);
        Assert.Equal(soon.Elapsed, muchLater.Elapsed);
        Assert.Equal("4m 00s", soon.ElapsedText);
        // A terminal request retains its final state and reports no live operation.
        Assert.Empty(soon.Operations);
        Assert.Equal("Request completed", Assert.Single(soon.Facts).Label);
    }

    [Fact]
    public void Only_the_newest_five_narratable_facts_survive_in_newest_first_order()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(2));
        var events = new List<SessionEventDto>
        {
            Event(1, "request.claimed", Queued.AddSeconds(1), "{}"),
            Event(2, "session.heartbeat", Queued.AddSeconds(2), "{}"),
            Event(3, "turn.started", Queued.AddSeconds(3), "{}"),
            Event(4, "tool.started", Queued.AddSeconds(4), """{"tool":"grep"}"""),
            Event(5, "tool.completed", Queued.AddSeconds(5), """{"tool":"grep"}"""),
            Event(6, "child.started", Queued.AddSeconds(6), """{"role":"reviewer"}"""),
            Event(7, "verification.failed", Queued.AddSeconds(7), """{"profileId":"build"}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(10));

        Assert.Equal(RequestExecutionProgressReader.FactCap, progress.Facts.Count);
        Assert.Equal(
            new[]
            {
                "Verification failed \u2014 build",
                "Child agent started \u2014 reviewer",
                "grep completed",
                "Started grep",
                "Turn started",
            },
            progress.Facts.Select(fact => fact.Label).ToArray());
        // Heartbeats are not narrated, but every persisted event is still counted.
        Assert.Equal(7, progress.EventCount);
        // No session yet: elapsed is measured from the moment the request was queued.
        Assert.Equal(TimeSpan.FromSeconds(10), progress.Elapsed);
        Assert.Equal(Queued, progress.ElapsedSince);
    }

    [Fact]
    public void Response_and_thinking_text_never_reaches_the_progress_facts()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(1));
        var events = new[]
        {
            Event(
                1,
                "message.delta",
                Queued.AddSeconds(10),
                """{"seq":1,"data":{"textDelta":"secret answer","thinkingDelta":"private chain"}}"""),
            Event(
                2,
                "message.started",
                Queued.AddSeconds(11),
                """{"seq":2,"data":{"text":"secret answer","message":"private chain"}}"""),
            Event(3, "message.completed", Queued.AddSeconds(12), """{"data":{"text":"secret answer"}}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(20));

        Assert.Equal(
            new[] { "Agent finished a response", "Agent began composing a response" },
            progress.Facts.Select(fact => fact.Label).ToArray());
        foreach (var fact in progress.Facts)
        {
            Assert.DoesNotContain("secret answer", fact.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private chain", fact.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", fact.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Malformed_unicode_payload_scalars_and_names_are_treated_as_absent()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(1));
        var events = new[]
        {
            Event(1, "request.phase_changed", Queued.AddSeconds(10), """{"phase":"\uD800"}"""),
            Event(
                2,
                "tool.started",
                Queued.AddSeconds(11),
                """{"\uD800":"leaked","tool":"build"}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(20));

        Assert.Equal("Executing", progress.Phase);
        Assert.Contains(progress.Facts, fact => fact.Label.Contains("build", StringComparison.Ordinal));
        foreach (var fact in progress.Facts)
        {
            Assert.DoesNotContain("\uD800", fact.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("leaked", fact.Label, StringComparison.Ordinal);
        }
    }

    private static WorkRequestDto Request(WorkRequestStatus status, DateTimeOffset updatedAt) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "Feature",
            1,
            "Normal",
            1,
            "Low",
            (int)status,
            status.ToString(),
            null,
            null,
            "Add progress surface",
            "Show honest progress",
            Queued,
            updatedAt,
            Version: 4);

    private static AgentSessionDto Session(
        string id,
        AgentActivity activity,
        DateTimeOffset startedAt,
        string? currentOperation,
        string statusReason,
        AgentLiveness liveness = AgentLiveness.Online,
        AgentWorkState workState = AgentWorkState.Executing,
        DateTimeOffset? endedAt = null) =>
        new(
            id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "AgentOne",
            "implementer",
            "pi",
            "pi/default",
            "prov-1",
            liveness,
            activity,
            AgentAttention.None,
            workState,
            statusReason,
            currentOperation,
            4242,
            startedAt,
            LastHeartbeatAt: null,
            endedAt);

    private static SessionEventDto Event(
        long sequence,
        string type,
        DateTimeOffset occurredAt,
        string payloadJson) =>
        new($"evt-{sequence}", "sess-root", sequence, type, occurredAt, payloadJson);
}
