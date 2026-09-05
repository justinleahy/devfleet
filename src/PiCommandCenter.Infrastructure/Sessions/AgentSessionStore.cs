using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Sessions;

/// <summary>
/// EF Core implementation of <see cref="IAgentSessionStore"/>. Each <see cref="ApplyAsync"/>
/// persists the appended <see cref="SessionEvent"/> row and the <see cref="AgentSessionRow"/>
/// projection update in one <c>SaveChanges</c> transaction; the event id primary key makes
/// duplicates inert before any projection work happens.
/// </summary>
public sealed class AgentSessionStore(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier) : IAgentSessionStore
{
    public async Task<IReadOnlyList<AgentSessionDto>> ListAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var requestGuid = requestId.Value;
        var rows = await db.AgentSessions
            .Where(s => s.RequestId == requestGuid)
            .OrderBy(s => s.ParentSessionId == null ? 0 : 1)
            .ThenBy(s => s.StartedAtUtcTicks)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);

        return rows.Select(ToDto).ToList();
    }

    public async Task<AgentSessionDto?> GetAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var row = await db.AgentSessions
            .SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        return row is null ? null : ToDto(row);
    }

    public async Task ApplyAsync(
        NormalizedAgentEvent @event,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(@event);

        var existing = await db.SessionEvents
            .AnyAsync(e => e.EventId == @event.EventId, cancellationToken);
        if (existing)
        {
            // Duplicate delivery: the event row and its projection transition were already
            // persisted together; never reapply.
            return;
        }

        db.SessionEvents.Add(ToEventRow(@event));
        await AgentSessionProjector.ApplyAsync(db, @event, cancellationToken);

        // One SaveChanges commits the event row and the projection transition atomically.
        await db.SaveChangesAsync(cancellationToken);

        // After the commit, so a view refreshing on this signal reads the applied transition.
        if (Guid.TryParse(@event.RequestId, out var requestId))
        {
            _ = Guid.TryParse(@event.ProjectId, out var projectId);
            notifier.Publish(ProjectionChange.Request(projectId, requestId));
        }
    }

    public async Task<IReadOnlyList<SessionEventDto>> ListEventsAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var requestGuid = requestId.Value;
        var rows = await db.SessionEvents
            .Where(e => e.RequestId == requestGuid)
            .OrderBy(e => e.OccurredAtUtcTicks)
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.EventId)
            .ToListAsync(cancellationToken);

        return rows.Select(e => new SessionEventDto(
            e.EventId,
            e.SessionId,
            e.Sequence,
            e.Type,
            new DateTimeOffset(e.OccurredAtUtcTicks, TimeSpan.Zero),
            e.PayloadJson)).ToList();
    }

    private SessionEvent ToEventRow(NormalizedAgentEvent @event) => new()
    {
        EventId = @event.EventId,
        NodeId = ParseGuid(@event.NodeId),
        ProjectId = ParseGuid(@event.ProjectId),
        RequestId = Guid.TryParse(@event.RequestId, out var requestId) ? requestId : null,
        SessionId = string.IsNullOrWhiteSpace(@event.SessionId) ? null : @event.SessionId,
        Sequence = @event.Sequence,
        Type = @event.Type,
        OccurredAtUtcTicks = @event.OccurredAt.UtcTicks,
        PayloadJson = SerializePayload(@event.Payload),
        ReceivedAtUtcTicks = clock.GetUtcNow().UtcTicks,
    };

    private static string SerializePayload(IReadOnlyDictionary<string, object?> payload)
    {
        if (payload.Count == 0)
        {
            return "{}";
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in payload)
            {
                writer.WritePropertyName(key);
                WriteValue(writer, value);
            }

            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null:
                writer.WriteNullValue();
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case int number:
                writer.WriteNumberValue(number);
                break;
            case long number:
                writer.WriteNumberValue(number);
                break;
            case double number:
                writer.WriteNumberValue(number);
                break;
            case decimal number:
                writer.WriteNumberValue(number);
                break;
            case JsonElement element:
                // Nested telemetry (usage objects, cost breakdowns) arrives as JsonElement;
                // write the raw value so objects/arrays/numbers survive the round-trip.
                element.WriteTo(writer);
                break;
            default:
                writer.WriteStringValue(value.ToString());
                break;
        }
    }

    private static Guid ParseGuid(string value) => Guid.TryParse(value, out var parsed)
        ? parsed
        : Guid.Empty;

    private static AgentSessionDto ToDto(AgentSessionRow row) => new(
        row.Id,
        row.ProjectId,
        row.RequestId,
        row.ParentSessionId,
        row.AgentName,
        row.Role,
        row.Runtime,
        row.Model,
        row.ProviderSessionId,
        ParseEnum<PiCommandCenter.Domain.Sessions.AgentLiveness>(row.Liveness),
        ParseEnum<PiCommandCenter.Domain.Sessions.AgentActivity>(row.Activity),
        ParseEnum<PiCommandCenter.Domain.Sessions.AgentAttention>(row.Attention),
        ParseEnum<PiCommandCenter.Domain.Sessions.AgentWorkState>(row.WorkState),
        row.StatusReason,
        row.CurrentOperation,
        row.ProcessId,
        new DateTimeOffset(row.StartedAtUtcTicks, TimeSpan.Zero),
        row.LastHeartbeatAtUtcTicks is { } heartbeat
            ? new DateTimeOffset(heartbeat, TimeSpan.Zero)
            : null,
        row.EndedAtUtcTicks is { } ended
            ? new DateTimeOffset(ended, TimeSpan.Zero)
            : null);

    private static T ParseEnum<T>(string? text)
        where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: false, out var parsed) ? parsed : default;
}
