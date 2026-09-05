using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Sessions;

/// <summary>
/// An agent session attached to a work request: the aggregate behind the session projection
/// (SPEC §21, §29). The four status dimensions are independent and move only through
/// <see cref="Apply"/> of normalized events — <c>Idle</c> is never inferred from silence;
/// only an explicit <c>turn.completed</c> or <c>session.snapshot</c> may set it. Constructed
/// only through <see cref="Start"/> or rehydration via <see cref="Rehydrate"/>.
/// </summary>
public sealed class AgentSession
{
    private readonly HashSet<string> _seenEventIds = new(StringComparer.Ordinal);

    private AgentSession(
        string id,
        ProjectId projectId,
        WorkRequestId requestId,
        string? parentSessionId,
        string agentName,
        string role,
        string runtime,
        string runtimeProfile,
        string? providerSessionId,
        AgentLiveness liveness,
        AgentActivity activity,
        AgentAttention attention,
        AgentWorkState workState,
        string statusReason,
        string? currentOperation,
        int? processId,
        DateTimeOffset startedAt,
        DateTimeOffset? lastHeartbeatAt,
        DateTimeOffset? endedAt,
        long lastSequence,
        long version)
    {
        Id = id;
        ProjectId = projectId;
        RequestId = requestId;
        ParentSessionId = parentSessionId;
        AgentName = agentName;
        Role = role;
        Runtime = runtime;
        RuntimeProfile = runtimeProfile;
        ProviderSessionId = providerSessionId;
        Liveness = liveness;
        Activity = activity;
        Attention = attention;
        WorkState = workState;
        StatusReason = statusReason;
        CurrentOperation = currentOperation;
        ProcessId = processId;
        StartedAt = startedAt;
        LastHeartbeatAt = lastHeartbeatAt;
        EndedAt = endedAt;
        LastSequence = lastSequence;
        Version = version;
    }

    public string Id { get; }

    public ProjectId ProjectId { get; }

    public WorkRequestId RequestId { get; }

    /// <summary>Null for a root session; the owning parent session id for children.</summary>
    public string? ParentSessionId { get; }

    public string AgentName { get; }

    public string Role { get; }

    /// <summary>Runtime identifier, e.g. <c>pi</c>, <c>claude-code</c>, <c>antigravity</c>.</summary>
    public string Runtime { get; }

    public string RuntimeProfile { get; }

    /// <summary>The runtime's own session identifier, when it has reported one.</summary>
    public string? ProviderSessionId { get; private set; }

    public AgentLiveness Liveness { get; private set; }

    public AgentActivity Activity { get; private set; }

    public AgentAttention Attention { get; private set; }

    public AgentWorkState WorkState { get; private set; }

    /// <summary>Human-readable reason for the current projected status (SPEC §21.7).</summary>
    public string StatusReason { get; private set; }

    /// <summary>What the agent is doing right now, e.g. the running tool's name.</summary>
    public string? CurrentOperation { get; private set; }

    public int? ProcessId { get; private set; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>Highest applied event sequence; strictly increasing per session (SPEC §22.1).</summary>
    public long LastSequence { get; private set; }

    /// <summary>Optimistic concurrency token; incremented on every applied transition.</summary>
    public long Version { get; private set; }

    /// <summary>True once the session has reached an irreversible end (closed, failed, cancelled).</summary>
    public bool IsTerminal => EndedAt is not null;

    /// <summary>
    /// Opens a session in the <see cref="AgentLiveness.Starting"/> /
    /// <see cref="AgentWorkState.Queued"/> state. Throws <see cref="ArgumentException"/> when any
    /// identifier or label is empty.
    /// </summary>
    public static AgentSession Start(
        string id,
        ProjectId projectId,
        WorkRequestId requestId,
        string? parentSessionId,
        string agentName,
        string role,
        string runtime,
        string runtimeProfile,
        DateTimeOffset startedAt)
    {
        var cleanId = CleanRequired(id, nameof(id));
        var cleanName = CleanRequired(agentName, nameof(agentName));
        var cleanRole = CleanRequired(role, nameof(role));
        var cleanRuntime = CleanRequired(runtime, nameof(runtime));
        var cleanProfile = CleanRequired(runtimeProfile, nameof(runtimeProfile));

        return new AgentSession(
            cleanId,
            projectId,
            requestId,
            CleanOptional(parentSessionId, nameof(parentSessionId)),
            cleanName,
            cleanRole,
            cleanRuntime,
            cleanProfile,
            providerSessionId: null,
            AgentLiveness.Starting,
            AgentActivity.Idle,
            AgentAttention.None,
            AgentWorkState.Queued,
            "Starting — agent session requested",
            currentOperation: null,
            processId: null,
            startedAt,
            lastHeartbeatAt: null,
            endedAt: null,
            lastSequence: 0,
            version: 1);
    }

    /// <summary>
    /// Rehydrates a persisted session without mutating timestamps or version. A terminal session
    /// must carry <see cref="EndedAt"/>; a non-terminal one must not.
    /// </summary>
    public static AgentSession Rehydrate(
        string id,
        ProjectId projectId,
        WorkRequestId requestId,
        string? parentSessionId,
        string agentName,
        string role,
        string runtime,
        string runtimeProfile,
        string? providerSessionId,
        AgentLiveness liveness,
        AgentActivity activity,
        AgentAttention attention,
        AgentWorkState workState,
        string statusReason,
        string? currentOperation,
        int? processId,
        DateTimeOffset startedAt,
        DateTimeOffset? lastHeartbeatAt,
        DateTimeOffset? endedAt,
        long lastSequence,
        long version)
    {
        var cleanId = CleanRequired(id, nameof(id));
        var cleanName = CleanRequired(agentName, nameof(agentName));
        var cleanRole = CleanRequired(role, nameof(role));
        var cleanRuntime = CleanRequired(runtime, nameof(runtime));
        var cleanProfile = CleanRequired(runtimeProfile, nameof(runtimeProfile));
        var cleanReason = CleanRequired(statusReason, nameof(statusReason));

        if (endedAt is not null && endedAt.Value < startedAt)
        {
            throw new ArgumentException("EndedAt must not precede StartedAt.", nameof(endedAt));
        }

        if (lastHeartbeatAt is not null && lastHeartbeatAt.Value < startedAt)
        {
            throw new ArgumentException("LastHeartbeatAt must not precede StartedAt.", nameof(lastHeartbeatAt));
        }

        if (lastSequence < 0)
        {
            throw new ArgumentException("Last sequence must not be negative.", nameof(lastSequence));
        }

        return new AgentSession(
            cleanId,
            projectId,
            requestId,
            CleanOptional(parentSessionId, nameof(parentSessionId)),
            cleanName,
            cleanRole,
            cleanRuntime,
            cleanProfile,
            CleanOptional(providerSessionId, nameof(providerSessionId)),
            liveness,
            activity,
            attention,
            workState,
            cleanReason,
            CleanOptional(currentOperation, nameof(currentOperation)),
            processId,
            startedAt,
            lastHeartbeatAt,
            endedAt,
            lastSequence,
            version);
    }

    /// <summary>
    /// Applies one normalized event. Duplicate event ids and events at or below the applied
    /// <see cref="LastSequence"/> are ignored idempotently (SPEC §22.1); unknown event types are
    /// recorded in <see cref="LastSequence"/> but change no status. Stale events (before
    /// <see cref="StartedAt"/>) are ignored. Throws <see cref="InvalidOperationException"/> when a
    /// recognized transition targets a terminal session.
    /// </summary>
    public void Apply(NormalizedAgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event.OccurredAt < StartedAt)
        {
            return;
        }

        if (!_seenEventIds.Add(@event.EventId))
        {
            return;
        }

        if (@event.Sequence <= LastSequence)
        {
            return;
        }

        if (IsTerminal && IsMutatingType(@event.Type))
        {
            throw new InvalidOperationException(
                $"Session '{Id}' is terminal ('{Liveness}' at {EndedAt:O}) and cannot apply '{@event.Type}'.");
        }

        LastSequence = @event.Sequence;

        switch (@event.Type)
        {
            case "session.registered":
                OnRegistered(@event);
                break;
            case "session.heartbeat":
                OnHeartbeat(@event);
                break;
            case "session.snapshot":
                OnSnapshot(@event);
                break;
            case "turn.started":
                Activity = AgentActivity.Reasoning;
                if (WorkState is AgentWorkState.Queued or AgentWorkState.Starting)
                {
                    WorkState = AgentWorkState.Executing;
                }

                StatusReason = Reason(@event, "Turn started");
                CurrentOperation = OptionalText(@event, "operation");
                break;
            case "turn.completed":
                Activity = AgentActivity.Idle;
                Attention = AgentAttention.None;
                CurrentOperation = null;
                StatusReason = Reason(@event, "Turn completed — awaiting next input");
                break;
            case "message.started":
                Activity = AgentActivity.Responding;
                CurrentOperation = OptionalText(@event, "operation") ?? "Composing response";
                StatusReason = Reason(@event, "Responding");
                break;
            case "message.delta":
                Activity = AgentActivity.Responding;
                break;
            case "message.completed":
                CurrentOperation = null;
                StatusReason = Reason(@event, "Response delivered");
                break;
            case "tool.started":
                Activity = AgentActivity.RunningTool;
                CurrentOperation = OptionalText(@event, "tool") ?? OptionalText(@event, "operation") ?? "Running tool";
                StatusReason = Reason(@event, $"Running {CurrentOperation}");
                break;
            case "tool.progress":
                Activity = AgentActivity.RunningTool;
                CurrentOperation = OptionalText(@event, "tool") ?? CurrentOperation ?? "Running tool";
                break;
            case "tool.completed":
                Activity = AgentActivity.Reasoning;
                CurrentOperation = null;
                StatusReason = Reason(@event, "Tool completed");
                break;
            case "tool.failed":
                Attention = AgentAttention.Warning;
                CurrentOperation = null;
                StatusReason = Reason(@event, $"Tool failed: {OptionalText(@event, "error") ?? "unspecified error"}");
                break;
            case "session.completed":
            case "child.completed":
                Liveness = AgentLiveness.Exited;
                // Failure and cancellation outrank a late completion signal.
                if (WorkState is not (AgentWorkState.Failed or AgentWorkState.Cancelled))
                {
                    Attention = AgentAttention.None;
                }

                // Failure and cancellation outrank a late completion signal.
                if (WorkState is not (AgentWorkState.Failed or AgentWorkState.Cancelled))
                {
                    WorkState = AgentWorkState.Completed;
                }

                EndedAt = @event.OccurredAt;
                CurrentOperation = null;
                StatusReason = Reason(@event, "Session completed");
                break;
            case "session.closed":
                Liveness = AgentLiveness.Exited;

                // A transport/process close is not proof of successful work. Preserve every
                // stronger non-success state; only an otherwise active session closes cleanly.
                if (WorkState is not (
                    AgentWorkState.Failed
                    or AgentWorkState.Cancelled
                    or AgentWorkState.Blocked))
                {
                    WorkState = AgentWorkState.Completed;
                }

                EndedAt = @event.OccurredAt;
                CurrentOperation = null;
                StatusReason = Reason(@event, "Session closed");
                break;
            case "session.failed":
                Liveness = AgentLiveness.Exited;
                Attention = AgentAttention.Error;
                WorkState = AgentWorkState.Failed;
                EndedAt = @event.OccurredAt;
                CurrentOperation = null;
                StatusReason = Reason(@event, $"Session failed: {OptionalText(@event, "error") ?? "unspecified error"}");
                break;
            case "session.disconnected":
                Liveness = AgentLiveness.Disconnected;
                StatusReason = Reason(@event, "Runtime reported the session disconnected");
                break;
            case "session.cancelled":
                Liveness = AgentLiveness.Exited;
                WorkState = AgentWorkState.Cancelled;
                EndedAt = @event.OccurredAt;
                CurrentOperation = null;
                StatusReason = Reason(@event, "Session cancelled");
                break;
            default:
                // Unknown event type: stored safely, no status change (SPEC §22.1).
                break;
        }

        Version++;
    }

    /// <summary>Records the runtime's provider session id and OS process id when reported.</summary>
    public void AttachProvider(string providerSessionId, int? processId)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException($"Session '{Id}' is terminal and cannot attach provider state.");
        }

        if (!string.IsNullOrWhiteSpace(providerSessionId))
        {
            ProviderSessionId = providerSessionId.Trim();
        }

        if (processId is > 0)
        {
            ProcessId = processId;
        }

        Version++;
    }

    private void OnRegistered(NormalizedAgentEvent @event)
    {
        Liveness = AgentLiveness.Online;
        if (WorkState == AgentWorkState.Queued)
        {
            WorkState = AgentWorkState.Starting;
        }

        LastHeartbeatAt = @event.OccurredAt;
        StatusReason = Reason(@event, "Agent registered with the runtime");
        var provider = OptionalText(@event, "providerSessionId");
        var pid = OptionalInt(@event, "processId");
        if (provider is not null || pid is not null)
        {
            AttachProvider(provider ?? ProviderSessionId ?? string.Empty, pid);
        }
    }

    private void OnHeartbeat(NormalizedAgentEvent @event)
    {
        if (LastHeartbeatAt is null || @event.OccurredAt >= LastHeartbeatAt.Value)
        {
            LastHeartbeatAt = @event.OccurredAt;
        }

        if (Liveness == AgentLiveness.Disconnected)
        {
            Liveness = AgentLiveness.Online;
            Attention = AgentAttention.None;
        }

        StatusReason = Reason(@event, "Heartbeat received");
    }

    private void OnSnapshot(NormalizedAgentEvent @event)
    {
        // A runtime snapshot is an authoritative, explicit status signal: it may set Idle.
        Liveness = EnumText(@event, "liveness", Liveness);
        Activity = EnumText(@event, "activity", Activity);
        Attention = EnumText(@event, "attention", Attention);
        WorkState = EnumText(@event, "workState", WorkState);

        var operation = OptionalText(@event, "currentOperation");
        if (operation is not null)
        {
            CurrentOperation = operation.Length == 0 ? null : operation;
        }

        if (@event.Payload.TryGetValue("statusReason", out var reason) && reason is string text && text.Length > 0)
        {
            StatusReason = text;
        }
        else
        {
            StatusReason = Reason(@event, "Runtime snapshot applied");
        }

        var provider = OptionalText(@event, "providerSessionId");
        var pid = OptionalInt(@event, "processId");
        if (provider is not null || pid is not null)
        {
            AttachProvider(provider ?? ProviderSessionId ?? string.Empty, pid);
        }
    }

    private static bool IsMutatingType(string type) => !string.Equals(type, "session.completed", StringComparison.Ordinal)
        && !string.Equals(type, "child.completed", StringComparison.Ordinal)
        && !string.Equals(type, "session.closed", StringComparison.Ordinal)
        && !string.Equals(type, "session.failed", StringComparison.Ordinal)
        && !string.Equals(type, "session.cancelled", StringComparison.Ordinal)
        && !type.StartsWith("request.", StringComparison.Ordinal);

    private static string Reason(NormalizedAgentEvent @event, string fallback)
    {
        if (@event.Payload.TryGetValue("reason", out var value) && value is string text && text.Trim().Length > 0)
        {
            return text.Trim();
        }

        return fallback;
    }

    private static string? OptionalText(NormalizedAgentEvent @event, string key)
    {
        if (!@event.Payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = value.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static int? OptionalInt(NormalizedAgentEvent @event, string key)
    {
        if (!@event.Payload.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int number => number,
            long number when number is > 0 and <= int.MaxValue => (int)number,
            _ => null,
        };
    }

    private static T EnumText<T>(NormalizedAgentEvent @event, string key, T fallback)
        where T : struct, Enum
    {
        if (@event.Payload.TryGetValue(key, out var value)
            && value is string text
            && Enum.TryParse<T>(text, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string CleanRequired(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length == 0)
        {
            throw new ArgumentException($"{paramName} must not be empty.", paramName);
        }

        return value.Trim();
    }

    private static string? CleanOptional(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        var clean = value.Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException($"{paramName} must be null or non-empty.", paramName);
        }

        return clean;
    }
}
