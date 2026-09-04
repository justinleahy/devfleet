using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Domain;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Sessions;

/// <summary>
/// The transactional reducer between appended <see cref="SessionEvent"/> rows and the current
/// <see cref="AgentSessionRow"/> projection. Payloads are parsed defensively (bounded, unknown
/// properties ignored); recognized event types drive the domain aggregate's transitions, and a
/// missing projection is created on <c>session.registered</c>. Unknown event types are stored but
/// change no status.
/// </summary>
public static class AgentSessionProjector
{
    /// <summary>Upper bound on payload JSON accepted for parsing; larger payloads are truncated to a safe empty payload.</summary>
    private const int MaxPayloadChars = 64 * 1024;

    /// <summary>
    /// Parses a stored event payload into a <see cref="NormalizedAgentEvent"/>. Malformed or
    /// oversized JSON yields an empty payload rather than an exception: the event row is always
    /// stored, the projection merely sees fewer hints.
    /// </summary>
    public static NormalizedAgentEvent ToNormalizedEvent(NodeEventDto nodeEvent)
    {
        ArgumentNullException.ThrowIfNull(nodeEvent);

        var payload = ParsePayload(nodeEvent.PayloadJson);
        var runtime = payload.TryGetValue("runtime", out var value)
            && value is string text
            && text.Trim().Length > 0
            ? text.Trim()
            : "pi";

        return new NormalizedAgentEvent(
            ProtocolVersion: 1,
            EventId: nodeEvent.EventId,
            NodeId: nodeEvent.NodeId.ToString("D"),
            ProjectId: nodeEvent.ProjectId.ToString("D"),
            RequestId: nodeEvent.RequestId?.ToString("D") ?? Guid.Empty.ToString("D"),
            SessionId: nodeEvent.SessionId ?? string.Empty,
            ParentSessionId: OptionalText(payload, "parentSessionId"),
            Sequence: nodeEvent.Sequence,
            Runtime: runtime,
            Type: nodeEvent.Type,
            OccurredAt: nodeEvent.OccurredAt,
            Payload: payload);
    }

    /// <summary>
    /// Applies one parsed event to the projection rows tracked by <paramref name="db"/>.
    /// Returns the touched row, or null when the event carries no session or the projection
    /// already applied a strictly greater sequence. Callers persist inside their own transaction.
    /// </summary>
    public static async Task<AgentSessionRow?> ApplyAsync(
        ControlPlaneDbContext db,
        NormalizedAgentEvent @event,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(@event.SessionId))
        {
            return null;
        }

        var row = await db.AgentSessions
            .SingleOrDefaultAsync(s => s.Id == @event.SessionId, cancellationToken);

        if (row is null)
        {
            if (!string.Equals(@event.Type, "session.registered", StringComparison.Ordinal))
            {
                // Events for a session we have never seen registered cannot be projected;
                // the event row itself is still appended by the caller.
                return null;
            }

            row = CreateRow(@event);
            db.AgentSessions.Add(row);
            return row;
        }

        var aggregate = Rehydrate(row);
        aggregate.Apply(@event);
        CopyTo(aggregate, row);
        return row;
    }

    private static AgentSessionRow CreateRow(NormalizedAgentEvent @event)
    {
        // Registration payload carries the supervisor's assignment; a missing field falls back
        // to a neutral label so a malformed payload can never block the append.
        var aggregate = AgentSession.Start(
            id: @event.SessionId,
            projectId: new ProjectId(ParseGuid(@event.ProjectId)),
            requestId: new WorkRequestId(ParseGuid(@event.RequestId)),
            parentSessionId: @event.ParentSessionId,
            agentName: OptionalText(@event.Payload, "agentName") ?? "agent",
            role: OptionalText(@event.Payload, "role") ?? "worker",
            runtime: @event.Runtime,
            runtimeProfile: OptionalText(@event.Payload, "runtimeProfile") ?? "default",
            startedAt: @event.OccurredAt);
        aggregate.Apply(@event);

        var row = new AgentSessionRow
        {
            Id = aggregate.Id,
            ProjectId = aggregate.ProjectId.Value,
            RequestId = aggregate.RequestId.Value,
            ParentSessionId = aggregate.ParentSessionId,
            AgentName = aggregate.AgentName,
            Role = aggregate.Role,
            Runtime = aggregate.Runtime,
            RuntimeProfile = aggregate.RuntimeProfile,
            StartedAtUtcTicks = aggregate.StartedAt.UtcTicks,
        };
        CopyTo(aggregate, row);
        return row;
    }

    private static AgentSession Rehydrate(AgentSessionRow row) => AgentSession.Rehydrate(
        id: row.Id,
        projectId: new ProjectId(row.ProjectId),
        requestId: new WorkRequestId(row.RequestId),
        parentSessionId: row.ParentSessionId,
        agentName: row.AgentName,
        role: row.Role,
        runtime: row.Runtime,
        runtimeProfile: row.RuntimeProfile,
        providerSessionId: row.ProviderSessionId,
        liveness: ParseEnum<AgentLiveness>(row.Liveness, AgentLiveness.Starting),
        activity: ParseEnum<AgentActivity>(row.Activity, AgentActivity.Idle),
        attention: ParseEnum<AgentAttention>(row.Attention, AgentAttention.None),
        workState: ParseEnum<AgentWorkState>(row.WorkState, AgentWorkState.Queued),
        statusReason: row.StatusReason,
        currentOperation: row.CurrentOperation,
        processId: row.ProcessId,
        startedAt: new DateTimeOffset(row.StartedAtUtcTicks, TimeSpan.Zero),
        lastHeartbeatAt: row.LastHeartbeatAtUtcTicks is { } heartbeat
            ? new DateTimeOffset(heartbeat, TimeSpan.Zero)
            : null,
        endedAt: row.EndedAtUtcTicks is { } ended
            ? new DateTimeOffset(ended, TimeSpan.Zero)
            : null,
        lastSequence: row.LastSequence,
        version: row.Version);

    private static void CopyTo(AgentSession aggregate, AgentSessionRow row)
    {
        row.ProviderSessionId = aggregate.ProviderSessionId;
        row.Liveness = aggregate.Liveness.ToString();
        row.Activity = aggregate.Activity.ToString();
        row.Attention = aggregate.Attention.ToString();
        row.WorkState = aggregate.WorkState.ToString();
        row.StatusReason = aggregate.StatusReason;
        row.CurrentOperation = aggregate.CurrentOperation;
        row.ProcessId = aggregate.ProcessId;
        row.LastHeartbeatAtUtcTicks = aggregate.LastHeartbeatAt?.UtcTicks;
        row.EndedAtUtcTicks = aggregate.EndedAt?.UtcTicks;
        row.LastSequence = aggregate.LastSequence;
        row.Version = aggregate.Version;
    }

    private static Dictionary<string, object?> ParsePayload(string payloadJson)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(payloadJson) || payloadJson.Length > MaxPayloadChars)
        {
            return payload;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return payload;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                payload[property.Name] = Scalar(property.Value);
            }

            return payload;
        }
        catch (JsonException)
        {
            // Defensive: malformed payload never blocks the append or the projection.
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }

    private static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? integer : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };

    private static string? OptionalText(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        var text = value.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var parsed)
        ? parsed
        : Guid.Empty;

    private static T ParseEnum<T>(string? text, T fallback)
        where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: false, out var parsed) ? parsed : fallback;
}
