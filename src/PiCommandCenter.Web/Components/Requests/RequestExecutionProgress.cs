using System.Text.Json;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;

namespace PiCommandCenter.Web.Components.Requests;

/// <summary>
/// One durable event rendered as a short operator sentence. The label is derived from the event
/// type and a bounded set of payload fields only; response, thinking, and delta text is never read.
/// </summary>
public sealed record ProgressFact(
    long Sequence,
    DateTimeOffset OccurredAt,
    string EventType,
    string Label,
    string? SessionId);

/// <summary>
/// What one still-running agent session reports it is doing, straight from its projection. Both
/// text fields are null when the runtime reported nothing; nothing is inferred from silence.
/// </summary>
public sealed record AgentOperation(
    string SessionId,
    string AgentName,
    string Role,
    AgentActivity Activity,
    string? Operation,
    string? StatusReason);

/// <summary>
/// The honest "what is happening right now" read model of one request, reduced from the request
/// projection, its session projections, and its persisted event stream.
/// </summary>
/// <remarks>
/// Deliberately absent: percentage complete, ETA, and step counts. Nothing in the fleet reports a
/// unit of total work, so any such number would be invented. What is reported instead is durable:
/// which phase the control plane recorded, whether the request is still running, how long it has
/// been running, how recently a durable fact arrived, how many events and tool calls were
/// persisted, what each live agent says it is doing, and the newest few lifecycle facts.
/// </remarks>
public sealed record RequestExecutionProgress(
    string Phase,
    string Status,
    bool IsRunning,
    bool IsTerminal,
    TimeSpan Elapsed,
    DateTimeOffset ElapsedSince,
    DateTimeOffset? LastActivityAt,
    int EventCount,
    int ToolCallCount,
    IReadOnlyList<AgentOperation> Operations,
    IReadOnlyList<ProgressFact> Facts)
{
    /// <summary>Coarse duration text: seconds under a minute, then minutes, hours, and days.</summary>
    public string ElapsedText
    {
        get
        {
            var elapsed = Elapsed < TimeSpan.Zero ? TimeSpan.Zero : Elapsed;
            if (elapsed.TotalSeconds < 60)
            {
                return $"{(int)elapsed.TotalSeconds}s";
            }

            if (elapsed.TotalMinutes < 60)
            {
                return $"{elapsed.Minutes}m {elapsed.Seconds:00}s";
            }

            if (elapsed.TotalHours < 24)
            {
                return $"{(int)elapsed.TotalHours}h {elapsed.Minutes:00}m";
            }

            return $"{(int)elapsed.TotalDays}d {elapsed.Hours:00}h";
        }
    }

    /// <summary>True when no agent session reports an operation worth naming.</summary>
    public bool HasOperations => Operations.Count > 0;
}

/// <summary>
/// Reduces one request, its sessions, and its persisted events into
/// <see cref="RequestExecutionProgress"/>. The reducer is pure and total: it never throws on a
/// malformed payload, never invents a value a payload omitted, and never reads model-authored
/// response or thinking text.
/// </summary>
/// <remarks>
/// Payload lookups are provider-neutral. A field is read from the payload root and, failing that,
/// from a nested <c>data</c> object, because the Pi worker wraps every normalized event body as
/// <c>{ seq, timestamp, data }</c> while other runtimes write the fields flat. Names match
/// case-insensitively so PascalCase and camelCase payloads read identically.
/// </remarks>
public static class RequestExecutionProgressReader
{
    /// <summary>Newest facts kept; the full stream stays available in the forensic timeline.</summary>
    public const int FactCap = 5;

    /// <summary>Hard cap on any payload-derived detail spliced into a label.</summary>
    private const int DetailCap = 120;

    private const string PhaseChangedType = "request.phase_changed";

    /// <summary>
    /// Reduces the request, its sessions, and its events (oldest first, as the store returns them)
    /// as observed at <paramref name="observedAt"/>. Elapsed time runs to the observation time
    /// while the request is live and freezes at the last durable timestamp once it is terminal, so
    /// a finished request reads the same on every later render.
    /// </summary>
    public static RequestExecutionProgress Read(
        WorkRequestDto request,
        IReadOnlyList<AgentSessionDto> sessions,
        IReadOnlyList<SessionEventDto> events,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(events);

        var status = (WorkRequestStatus)request.Status;
        var terminal = status is WorkRequestStatus.Completed
            or WorkRequestStatus.Failed
            or WorkRequestStatus.Cancelled;

        var since = Anchor(request, sessions);
        var lastActivity = LastActivity(sessions, events);
        var recordedPhase = (string?)null;
        var toolCalls = 0;

        var facts = new List<ProgressFact>(FactCap);
        for (var index = events.Count - 1; index >= 0; index--)
        {
            var evt = events[index];
            if (IsToolStart(evt.Type))
            {
                toolCalls++;
            }

            // The newest recorded phase outranks the request status name, which lags a phase
            // change that has not yet been projected onto the request row.
            var wantsPhase = recordedPhase is null
                && string.Equals(evt.Type, PhaseChangedType, StringComparison.Ordinal);
            if (facts.Count == FactCap && !wantsPhase)
            {
                continue;
            }

            using var document = Parse(evt.PayloadJson);
            var root = document?.RootElement is { ValueKind: JsonValueKind.Object } element
                ? element
                : (JsonElement?)null;
            var data = root is { } scope
                && TryGetObjectProperty(scope, "data", out var nested)
                && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : (JsonElement?)null;

            if (wantsPhase && Text(root, data, "phase") is { } reported && !Blank(reported))
            {
                recordedPhase = Clip(reported);
            }

            if (facts.Count < FactCap && Label(evt.Type, root, data) is { } label)
            {
                facts.Add(new ProgressFact(evt.Sequence, evt.OccurredAt, evt.Type, label, evt.SessionId));
            }
        }

        var phase = recordedPhase ?? request.BlockedPhaseName ?? request.StatusName;

        var elapsedTo = terminal
            ? Later(request.UpdatedAt, lastActivity ?? request.UpdatedAt)
            : observedAt;
        var elapsed = elapsedTo - since;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        return new RequestExecutionProgress(
            phase,
            request.StatusName,
            IsRunning: !terminal,
            IsTerminal: terminal,
            elapsed,
            since,
            lastActivity,
            events.Count,
            toolCalls,
            Operations(sessions),
            facts);
    }

    /// <summary>
    /// Where elapsed time is measured from: the first agent session's start once execution began,
    /// otherwise the moment the request was queued, so a waiting request reports its queue age.
    /// </summary>
    private static DateTimeOffset Anchor(WorkRequestDto request, IReadOnlyList<AgentSessionDto> sessions)
    {
        DateTimeOffset? earliest = null;
        foreach (var session in sessions)
        {
            if (earliest is null || session.StartedAt < earliest)
            {
                earliest = session.StartedAt;
            }
        }

        return earliest ?? request.CreatedAt;
    }

    /// <summary>
    /// The newest durable timestamp across persisted events, heartbeats, and session ends. Null
    /// when nothing has been persisted yet; the request row's own update time is not activity.
    /// </summary>
    private static DateTimeOffset? LastActivity(
        IReadOnlyList<AgentSessionDto> sessions,
        IReadOnlyList<SessionEventDto> events)
    {
        DateTimeOffset? latest = null;
        foreach (var evt in events)
        {
            if (latest is null || evt.OccurredAt > latest)
            {
                latest = evt.OccurredAt;
            }
        }

        foreach (var session in sessions)
        {
            if (session.LastHeartbeatAt is { } beat && (latest is null || beat > latest))
            {
                latest = beat;
            }

            if (session.EndedAt is { } ended && (latest is null || ended > latest))
            {
                latest = ended;
            }
        }

        return latest;
    }

    private static DateTimeOffset Later(DateTimeOffset left, DateTimeOffset right) =>
        left >= right ? left : right;

    /// <summary>
    /// What each still-live session reports doing. A session that has ended, exited, or reached a
    /// terminal work state is excluded, as is an idle session with no reported operation.
    /// </summary>
    private static IReadOnlyList<AgentOperation> Operations(IReadOnlyList<AgentSessionDto> sessions)
    {
        var operations = new List<AgentOperation>();
        foreach (var session in sessions)
        {
            if (session.EndedAt is not null
                || session.Liveness == AgentLiveness.Exited
                || session.WorkState is AgentWorkState.Completed
                    or AgentWorkState.Failed
                    or AgentWorkState.Cancelled)
            {
                continue;
            }

            var operation = Blank(session.CurrentOperation) ? null : Clip(session.CurrentOperation!);
            if (session.Activity == AgentActivity.Idle && operation is null)
            {
                continue;
            }

            operations.Add(new AgentOperation(
                session.Id,
                session.AgentName,
                session.Role,
                session.Activity,
                operation,
                Blank(session.StatusReason) ? null : Clip(session.StatusReason)));
        }

        return operations;
    }

    /// <summary>Canonical tool start (SPEC §22.2); one per observed tool call.</summary>
    private static bool IsToolStart(string type) =>
        string.Equals(type, "tool.started", StringComparison.Ordinal);

    /// <summary>
    /// The operator sentence for one event type, or null for an event this section does not
    /// narrate (heartbeats, snapshots, deltas, mail, reservations, and repository facts, all of
    /// which have their own sections). Both the canonical <c>tool.progress</c> type and the
    /// historical <c>tool.updated</c> spelling are accepted.
    /// </summary>
    private static string? Label(string type, JsonElement? root, JsonElement? data) => type switch
    {
        "request.claimed" => "Request claimed by a node",
        "request.phase_changed" => Text(root, data, "phase") is { } phase && !Blank(phase)
            ? $"Phase changed to {Clip(phase)}"
            : "Phase changed",
        "request.blocked" => Suffix("Request blocked", Text(root, data, "reason", "detail")),
        "request.completed" => "Request completed",
        "request.failed" => Suffix("Request failed", Text(root, data, "error")),
        "request.cancelled" => "Request cancelled",

        "session.registered" => Suffix("Agent session registered", Text(root, data, "role")),
        "session.disconnected" => "Agent session disconnected",
        "session.closed" => "Agent session closed",
        "session.completed" => "Agent session completed",
        "session.failed" => Suffix("Agent session failed", Text(root, data, "error")),
        "session.cancelled" => "Agent session cancelled",

        "turn.submitted" => "Turn submitted to the runtime",
        "turn.started" => Suffix("Turn started", Text(root, data, "operation")),
        "turn.completed" => "Turn completed",
        "message.started" => "Agent began composing a response",
        "message.completed" => "Agent finished a response",

        "tool.started" => Tool(root, data) is { } started
            ? $"Started {started}"
            : "Started a tool",
        "tool.progress" or "tool.updated" => Tool(root, data) is { } running
            ? $"{running} reported progress"
            : "A tool reported progress",
        "tool.completed" => Tool(root, data) is { } done
            ? $"{done} completed"
            : "A tool completed",
        "tool.failed" => Suffix(
            Tool(root, data) is { } failed ? $"{failed} failed" : "A tool failed",
            Text(root, data, "error")),

        "child.requested" => Suffix("Child agent requested", Text(root, data, "role", "childRole")),
        "child.started" => Suffix("Child agent started", Text(root, data, "role", "childRole")),
        "child.status_changed" => Suffix("Child agent status changed", Text(root, data, "status", "state")),
        "child.completed" => "Child agent completed",
        "child.failed" => Suffix("Child agent failed", Text(root, data, "error")),
        "child.cancelled" => "Child agent cancelled",

        "verification.started" => Suffix("Verification started", Profile(root, data)),
        "verification.completed" => Suffix("Verification completed", Profile(root, data)),
        "verification.failed" => Suffix("Verification failed", Profile(root, data)),

        _ => null,
    };

    private static string? Tool(JsonElement? root, JsonElement? data)
    {
        var name = Text(root, data, "tool", "toolName", "tool_name");
        return Blank(name) ? null : Clip(name!);
    }

    private static string? Profile(JsonElement? root, JsonElement? data) =>
        Text(root, data, "profileId", "profile", "command");

    private static string Suffix(string label, string? detail) =>
        Blank(detail) ? label : $"{label} \u2014 {Clip(detail!)}";

    private static bool Blank(string? value) => string.IsNullOrWhiteSpace(value);

    private static string Clip(string value)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= DetailCap
            ? trimmed
            : string.Concat(trimmed.AsSpan(0, DetailCap), "\u2026");
    }

    private static JsonDocument? Parse(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the first of <paramref name="names"/> present at the payload root, then inside the
    /// runtime's nested <c>data</c> object. Only strings, numbers, and booleans are returned, so a
    /// nested object or array can never leak as text.
    /// </summary>
    private static string? Text(JsonElement? root, JsonElement? data, params string[] names) =>
        Text(root, names) ?? Text(data, names);

    private static string? Text(JsonElement? scope, string[] names)
    {
        if (scope is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var name in names)
        {
            if (TryGetObjectProperty(element, name, out var exact))
            {
                return Scalar(exact);
            }
        }

        foreach (var property in element.EnumerateObject())
        {
            string propertyName;
            try
            {
                propertyName = property.Name;
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            foreach (var name in names)
            {
                if (string.Equals(propertyName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return Scalar(property.Value);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a named property without letting incomplete UTF-16 in any object property name
    /// or value escape as <see cref="InvalidOperationException"/>.
    /// </summary>
    private static bool TryGetObjectProperty(JsonElement element, string name, out JsonElement value)
    {
        try
        {
            return element.TryGetProperty(name, out value);
        }
        catch (InvalidOperationException)
        {
            value = default;
            return false;
        }
    }

    private static string? Scalar(JsonElement value)
    {
        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
                _ => null,
            };
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
