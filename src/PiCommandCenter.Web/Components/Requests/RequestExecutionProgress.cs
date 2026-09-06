using System.Text.Json;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;

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

        // Lifecycle events plus bounded command-progress. None of them carries a phase,
        // and the phase above is only ever read from request.phase_changed, so a rejected
        // precondition, intermediate check, and command progress all leave the reported phase alone.
        "verification.started" => Suffix("Verification started", VerificationDetail(root, data)),
        "verification.command.started" => Suffix(
            "Verification command started",
            Text(root, data, "commandId", "command_id")),
        "verification.completed" => Suffix("Verification completed", VerificationDetail(root, data)),
        "verification.failed" => Suffix("Verification failed", VerificationDetail(root, data)),
        "verification.rejected" => Suffix(
            "Verification did not start; the phase is unchanged",
            VerificationDetail(root, data)),
        "verification.intermediate" => Suffix(
            "Intermediate project checks ran",
            VerificationDetail(root, data)),
        "verification.cancelled" => Suffix("Verification cancelled", VerificationDetail(root, data)),

        _ => null,
    };

    private static string? Tool(JsonElement? root, JsonElement? data)
    {
        var name = Text(root, data, "tool", "toolName", "tool_name");
        return Blank(name) ? null : Clip(name!);
    }

    /// <summary>
    /// The bounded operator detail of a verification event: the coordinator's own summary, then
    /// its error code, then the profile or command a legacy payload named. Never the fingerprint,
    /// which is opaque to an operator and already shown by the Verification section.
    /// </summary>
    private static string? VerificationDetail(JsonElement? root, JsonElement? data) =>
        Text(root, data, "summary")
        ?? Text(root, data, "errorCode")
        ?? Text(root, data, "profileId", "profile", "command");

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

/// <summary>
/// One persisted deterministic verification run as the Request page renders it: the stored row
/// plus whether it belongs to the current repository fingerprint and policy revision or is
/// retained history from an earlier attempt.
/// </summary>
public sealed record VerificationRow(VerificationRunDto Run, bool IsCurrent)
{
    /// <summary>The node reported the command as still executing.</summary>
    public bool IsRunning => Run.Status == VerificationRunStatus.Running;

    /// <summary>The node recorded a failure or a timeout for this command.</summary>
    public bool IsRed =>
        Run.Status is VerificationRunStatus.Failed or VerificationRunStatus.TimedOut;

    /// <summary>
    /// An optional command finished red. It is reported as a warning: an optional command never
    /// blocks completion, so rendering it as failed would overstate the recorded fact.
    /// </summary>
    public bool IsWarning => IsRed && !Run.Mandatory;

    /// <summary>
    /// A mandatory command of the current fingerprint finished red, which is what the completion
    /// gate blocks on. A red row from a superseded fingerprint is history and blocks nothing.
    /// </summary>
    public bool IsBlocking => IsRed && Run.Mandatory && IsCurrent;
}

/// <summary>
/// The latest admitted final verification attempt, taken from <c>verification.started</c>.
/// Older stored rows become history as soon as this event is persisted; only a matching
/// final terminal event closes the attempt.
/// </summary>
public sealed record AdmittedCommandProgress(
    string CommandId,
    string RunKind,
    bool Mandatory,
    int TimeoutSeconds,
    DateTimeOffset StartedAt,
    DateTimeOffset EventTime);

public sealed record AdmittedFinalVerification(
    string Fingerprint,
    string PolicyRevision,
    DateTimeOffset StartedAt,
    bool IsOpen,
    AdmittedCommandProgress? Command = null);

/// <summary>
/// The Verification section of one request, split into the four concepts the operator has to keep
/// apart: independent agent review sessions, the automatic baseline, the Project's selected
/// project checks, and child-requested intermediate history.
/// </summary>
/// <remarks>
/// Deliberately absent: any executable, argv, or environment this page did not read from a
/// persisted row or a bounded <c>verification.command.started</c> fact. Timeout budget is taken
/// only from that progress event. The success sentences are only produced when the stored
/// mandatory rows of the current fingerprint are all green, so a stale green attempt can never
/// read as current success. An open admitted start with no command rows yet is in-progress, not
/// success.
/// </remarks>
public sealed record RequestVerificationView(
    IReadOnlyList<AgentSessionDto> ReviewSessions,
    IReadOnlyList<VerificationRow> Baseline,
    IReadOnlyList<VerificationRow> ProjectChecks,
    IReadOnlyList<VerificationRow> History,
    IReadOnlyList<VerificationRow> Intermediate,
    string? Fingerprint,
    string? PolicyRevision,
    string? BaselineSuccess,
    string? ProjectChecksSuccess,
    int WarningCount,
    bool HasBlockingFailure,
    AdmittedFinalVerification? Admitted = null)
{
    /// <summary>True when no baseline or project-check row exists for the current fingerprint.</summary>
    public bool HasCurrentRuns => Baseline.Count > 0 || ProjectChecks.Count > 0;

    /// <summary>
    /// True while a final <c>verification.started</c> has admitted a fingerprint that no matching
    /// terminal event has closed. Command rows may still be absent.
    /// </summary>
    public bool IsAdmittedInProgress => Admitted is { IsOpen: true };

    /// <summary>The current row a node reports as still executing, or null when none is.</summary>
    public VerificationRow? Running
    {
        get
        {
            foreach (var row in Baseline)
            {
                if (row.IsRunning)
                {
                    return row;
                }
            }

            foreach (var row in ProjectChecks)
            {
                if (row.IsRunning)
                {
                    return row;
                }
            }

            return null;
        }
    }
}

/// <summary>
/// Reduces persisted verification runs and agent sessions into <see cref="RequestVerificationView"/>.
/// Pure and total: it reads stored rows and admitted events only, and reports no success, warning,
/// or blocker that a stored row does not prove.
/// </summary>
public static class RequestVerificationViewReader
{
    /// <summary>The child roles whose sessions are independent agent verification (SPEC §18).</summary>
    private static readonly string[] ReviewRoles = ["reviewer", "verifier"];

    /// <summary>
    /// Splits <paramref name="runs"/> by <see cref="VerificationRunKind"/> and by fingerprint.
    /// A final <c>verification.started</c> admits the current fingerprint and policy immediately;
    /// without that event the newest <c>Baseline</c> or <c>ProjectCheck</c> row defines them.
    /// Every other final row is history, and <c>Intermediate</c> rows are always history because
    /// they never affect phase, attention, or the completion gate.
    /// </summary>
    public static RequestVerificationView Read(
        IReadOnlyList<VerificationRunDto> runs,
        IReadOnlyList<AgentSessionDto> sessions,
        IReadOnlyList<SessionEventDto>? events = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(sessions);

        var admitted = ReadAdmitted(events);
        var current = CurrentAttempt(runs, admitted);
        var baseline = new List<VerificationRow>();
        var projectChecks = new List<VerificationRow>();
        var history = new List<VerificationRow>();
        var intermediate = new List<VerificationRow>();

        for (var index = runs.Count - 1; index >= 0; index--)
        {
            var run = runs[index];
            if (run.RunKind == VerificationRunKind.Intermediate)
            {
                intermediate.Add(new VerificationRow(run, IsCurrent: false));
                continue;
            }

            if (!IsAttempt(run, current))
            {
                history.Add(new VerificationRow(run, IsCurrent: false));
                continue;
            }

            var row = new VerificationRow(run, IsCurrent: true);
            (run.RunKind == VerificationRunKind.Baseline ? baseline : projectChecks).Add(row);
        }

        // Final rows read oldest first inside their group: a command sequence is easier to follow
        // in the order the node ran it. History and intermediate history read newest first.
        baseline.Reverse();
        projectChecks.Reverse();

        return new RequestVerificationView(
            ReviewSessions(sessions),
            baseline,
            projectChecks,
            history,
            intermediate,
            current?.Fingerprint ?? admitted?.Fingerprint,
            current?.PolicyRevision ?? admitted?.PolicyRevision,
            admitted is { IsOpen: true } ? null : BaselineSuccess(baseline),
            admitted is { IsOpen: true } ? null : ProjectChecksSuccess(projectChecks),
            Warnings(baseline) + Warnings(projectChecks),
            Blocks(baseline) || Blocks(projectChecks),
            admitted);
    }

    /// <summary>
    /// The latest final <c>verification.started</c> fingerprint and policy. A later matching
    /// <c>verification.completed</c>, <c>verification.failed</c>, or <c>verification.cancelled</c>
    /// closes that lifecycle and clears command progress. <c>verification.command.started</c> is a
    /// bounded progress fact and never admits or closes a lifecycle.
    /// </summary>
    public static AdmittedFinalVerification? ReadAdmitted(IReadOnlyList<SessionEventDto>? events)
    {
        if (events is null || events.Count == 0)
        {
            return null;
        }

        AdmittedFinalVerification? admitted = null;
        foreach (var evt in events.OrderBy(e => e.OccurredAt).ThenBy(e => e.Sequence))
        {
            if (string.Equals(evt.Type, "verification.started", StringComparison.OrdinalIgnoreCase))
            {
                if (TryIdentity(evt.PayloadJson, out var fingerprint, out var policy))
                {
                    admitted = new AdmittedFinalVerification(fingerprint, policy, evt.OccurredAt, IsOpen: true);
                }

                continue;
            }

            if (admitted is not { IsOpen: true })
            {
                continue;
            }

            if (string.Equals(evt.Type, "verification.command.started", StringComparison.OrdinalIgnoreCase)
                && TryCommandProgress(evt, admitted, out var command))
            {
                admitted = admitted with { Command = command };
                continue;
            }

            if (!IsFinalTerminal(evt.Type))
            {
                continue;
            }

            if (TryIdentity(evt.PayloadJson, out var terminalFingerprint, out var terminalPolicy)
                && string.Equals(terminalFingerprint, admitted.Fingerprint, StringComparison.Ordinal)
                && string.Equals(terminalPolicy, admitted.PolicyRevision, StringComparison.Ordinal))
            {
                admitted = admitted with { IsOpen = false, Command = null };
            }
        }

        return admitted;
    }

    private static bool TryCommandProgress(
        SessionEventDto evt,
        AdmittedFinalVerification admitted,
        out AdmittedCommandProgress? command)
    {
        command = null;
        using var document = ParsePayload(evt.PayloadJson);
        if (document is null)
        {
            return false;
        }

        JsonElement? data = null;
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("data", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            data = nested;
        }

        if (!TryIdentity(evt.PayloadJson, out var fingerprint, out var policy)
            || !string.Equals(fingerprint, admitted.Fingerprint, StringComparison.Ordinal)
            || !string.Equals(policy, admitted.PolicyRevision, StringComparison.Ordinal))
        {
            return false;
        }

        var commandId = PayloadText(document.RootElement, data, "commandId", "command_id");
        if (string.IsNullOrWhiteSpace(commandId))
        {
            return false;
        }

        var timeout = PayloadInt(document.RootElement, data, "timeoutSeconds", "timeout_seconds");
        if (timeout is null or <= 0)
        {
            return false;
        }

        command = new AdmittedCommandProgress(
            commandId.Trim(),
            PayloadText(document.RootElement, data, "runKind", "run_kind") ?? string.Empty,
            PayloadBool(document.RootElement, data, "mandatory") ?? false,
            timeout.Value,
            PayloadTime(document.RootElement, data, "startedAt", "started_at") ?? evt.OccurredAt,
            PayloadTime(document.RootElement, data, "eventTime", "event_time") ?? evt.OccurredAt);
        return true;
    }

    private static bool IsFinalTerminal(string type) =>
        type.Equals("verification.completed", StringComparison.OrdinalIgnoreCase)
        || type.Equals("verification.failed", StringComparison.OrdinalIgnoreCase)
        || type.Equals("verification.cancelled", StringComparison.OrdinalIgnoreCase);

    private static bool TryIdentity(string payloadJson, out string fingerprint, out string policyRevision)
    {
        fingerprint = string.Empty;
        policyRevision = string.Empty;
        using var document = ParsePayload(payloadJson);
        if (document is null)
        {
            return false;
        }

        JsonElement? data = null;
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("data", out var nested)
            && nested.ValueKind == JsonValueKind.Object)
        {
            data = nested;
        }

        var foundFingerprint = PayloadText(document.RootElement, data, "fingerprint");
        var foundPolicy = PayloadText(document.RootElement, data, "policyRevision", "policy_revision");
        if (string.IsNullOrWhiteSpace(foundFingerprint) || string.IsNullOrWhiteSpace(foundPolicy))
        {
            return false;
        }

        fingerprint = foundFingerprint.Trim();
        policyRevision = foundPolicy.Trim();
        return true;
    }

    private static JsonDocument? ParsePayload(string payloadJson)
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

    private static string? PayloadText(JsonElement root, JsonElement? data, params string[] names)
    {
        return Match(root, names) ?? (data is { } nested ? Match(nested, names) : null);

        static string? Match(JsonElement element, string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                }
            }

            return null;
        }
    }

    private static int? PayloadInt(JsonElement root, JsonElement? data, params string[] names)
    {
        var text = PayloadText(root, data, names);
        if (int.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return MatchNumber(root, names) ?? (data is { } nested ? MatchNumber(nested, names) : null);

        static int? MatchNumber(JsonElement element, string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out var number))
                    {
                        return number;
                    }
                }
            }

            return null;
        }
    }

    private static bool? PayloadBool(JsonElement root, JsonElement? data, params string[] names)
    {
        var text = PayloadText(root, data, names);
        if (bool.TryParse(text, out var parsed))
        {
            return parsed;
        }

        return MatchBool(root, names) ?? (data is { } nested ? MatchBool(nested, names) : null);

        static bool? MatchBool(JsonElement element, string[] names)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        return property.Value.GetBoolean();
                    }
                }
            }

            return null;
        }
    }

    private static DateTimeOffset? PayloadTime(JsonElement root, JsonElement? data, params string[] names)
    {
        var text = PayloadText(root, data, names);
        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// The fingerprint and policy revision of the admitted attempt when one exists; otherwise the
    /// newest final run. Ties on the recorded start time keep the later row in list order.
    /// </summary>
    private static AttemptIdentity? CurrentAttempt(
        IReadOnlyList<VerificationRunDto> runs,
        AdmittedFinalVerification? admitted)
    {
        if (admitted is not null)
        {
            return new AttemptIdentity(admitted.Fingerprint, admitted.PolicyRevision);
        }

        VerificationRunDto? newest = null;
        foreach (var run in runs)
        {
            if (run.RunKind == VerificationRunKind.Intermediate)
            {
                continue;
            }

            if (newest is null || run.StartedAt >= newest.StartedAt)
            {
                newest = run;
            }
        }

        return newest is null
            ? null
            : new AttemptIdentity(newest.Fingerprint, newest.PolicyRevision);
    }

    private static bool IsAttempt(VerificationRunDto run, AttemptIdentity? current) =>
        current is { } identity
        && string.Equals(run.Fingerprint, identity.Fingerprint, StringComparison.Ordinal)
        && string.Equals(run.PolicyRevision, identity.PolicyRevision, StringComparison.Ordinal);

    private readonly record struct AttemptIdentity(string Fingerprint, string PolicyRevision);


    private static IReadOnlyList<AgentSessionDto> ReviewSessions(IReadOnlyList<AgentSessionDto> sessions)
    {
        var found = new List<AgentSessionDto>();
        foreach (var session in sessions)
        {
            foreach (var role in ReviewRoles)
            {
                if (string.Equals(session.Role, role, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(session);
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// <c>Baseline checks passed.</c> only once every mandatory baseline row of the current
    /// fingerprint is green and nothing is still running. An optional row that finished red is a
    /// warning beside this sentence, never a reason to withhold it, and never "all tests passed":
    /// the baseline is not a project's test suite.
    /// </summary>
    private static string? BaselineSuccess(IReadOnlyList<VerificationRow> rows) =>
        AllMandatoryGreen(rows) ? "Baseline checks passed." : null;

    /// <summary>
    /// <c>Project checks passed: {command ids}.</c> naming exactly the green commands of the
    /// current fingerprint, so the sentence stays true for a profile whose optional command
    /// warned.
    /// </summary>
    private static string? ProjectChecksSuccess(IReadOnlyList<VerificationRow> rows)
    {
        if (!AllMandatoryGreen(rows))
        {
            return null;
        }

        var passed = new List<string>();
        foreach (var row in rows)
        {
            if (row.Run.Status == VerificationRunStatus.Passed && !passed.Contains(row.Run.CommandId))
            {
                passed.Add(row.Run.CommandId);
            }
        }

        return $"Project checks passed: {string.Join(", ", passed)}.";
    }

    /// <summary>
    /// True when the group has at least one green row, every mandatory row is green, and no row is
    /// still running. A cancelled or missing mandatory row is therefore not success.
    /// </summary>
    private static bool AllMandatoryGreen(IReadOnlyList<VerificationRow> rows)
    {
        var green = false;
        foreach (var row in rows)
        {
            if (row.IsRunning)
            {
                return false;
            }

            if (row.Run.Status == VerificationRunStatus.Passed)
            {
                green = true;
                continue;
            }

            if (row.Run.Mandatory)
            {
                return false;
            }
        }

        return green;
    }

    private static int Warnings(IReadOnlyList<VerificationRow> rows)
    {
        var count = 0;
        foreach (var row in rows)
        {
            if (row.IsWarning)
            {
                count++;
            }
        }

        return count;
    }

    private static bool Blocks(IReadOnlyList<VerificationRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsBlocking)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Operator copy for which Project-check profile a request is bound to. Assigned work reads the
/// ExecutionAssignment snapshot; completed work names the recorded run; only unassigned work
/// reads the live Project selection.
/// </summary>
public static class RequestVerificationPolicyCopy
{
    public static string ProjectChecksMeta(
        ExecutionAssignmentProjectionDto? assignment,
        string? liveProfileId,
        string? liveProfileRevision,
        IReadOnlyList<VerificationRow> currentProjectChecks,
        bool isAssigned,
        bool isTerminal)
    {
        if (isTerminal)
        {
            foreach (var row in currentProjectChecks)
            {
                if (row.IsCurrent && !string.IsNullOrWhiteSpace(row.Run.ProfileId))
                {
                    return $"{row.Run.ProfileId} \u00b7 recorded run";
                }
            }
        }

        if (isAssigned)
        {
            if (assignment?.TrustedVerificationProfileId is { Length: > 0 } snapshotId)
            {
                return assignment.TrustedVerificationProfileRevision is { Length: > 0 } revision
                    ? $"{snapshotId} \u00b7 revision {Short(revision)} \u00b7 assigned snapshot"
                    : $"{snapshotId} \u00b7 assigned snapshot";
            }

            return "no project checks in assignment snapshot";
        }

        if (liveProfileId is { Length: > 0 })
        {
            return liveProfileRevision is { Length: > 0 } revision
                ? $"{liveProfileId} \u00b7 revision {Short(revision)} \u00b7 current Project selection"
                : $"{liveProfileId} \u00b7 current Project selection";
        }

        return "no profile selected";
    }

    public static string? AssignedProfileId(ExecutionAssignmentProjectionDto? assignment, bool isAssigned) =>
        isAssigned && assignment?.TrustedVerificationProfileId is { Length: > 0 } id ? id : null;

    public static string Short(string value) =>
        value.Length <= 16 ? value : string.Concat(value.AsSpan(0, 16), "\u2026");
}
