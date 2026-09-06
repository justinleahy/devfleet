using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Transport;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// Reduces appended node events onto <see cref="WorkRequest"/> status. Catch-up is
/// idempotent and forward-only so out-of-order delivery cannot regress or throw. Terminal
/// outcomes are never inferred here: request.completed/failed/cancelled remain history only;
/// only the terminalization authority terminalizes a request.
/// </summary>
public static class WorkRequestProjector
{
    public static async Task ApplyAsync(
        ControlPlaneDbContext db,
        NodeEventDto nodeEvent,
        CancellationToken cancellationToken)
    {
        if (nodeEvent.RequestId is not { } requestGuid)
        {
            return;
        }

        var target = InferTarget(nodeEvent);
        if (target is null)
        {
            return;
        }

        var request = await db.WorkRequests
            .SingleOrDefaultAsync(r => r.Id == new WorkRequestId(requestGuid), cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        request.TryCatchUpTo(target.Value, nodeEvent.OccurredAt);
    }

    internal static WorkRequestStatus? InferTarget(NodeEventDto nodeEvent)
    {
        var type = nodeEvent.Type;
        if (string.Equals(type, "request.blocked", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Blocked;
        }

        if (string.Equals(type, "verification.started", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Verifying;
        }

        if (string.Equals(type, "request.claimed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "session.registered", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Starting;
        }

        if (string.Equals(type, "request.phase_changed", StringComparison.OrdinalIgnoreCase))
        {
            return PhaseFromPayload(nodeEvent.PayloadJson);
        }

        if (string.Equals(type, "child.started", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "turn.started", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Executing;
        }

        if (string.Equals(type, "child.completed", StringComparison.OrdinalIgnoreCase)
            && IsReviewer(nodeEvent.PayloadJson))
        {
            return WorkRequestStatus.Reviewing;
        }

        return null;
    }

    private static WorkRequestStatus? PhaseFromPayload(string payloadJson)
    {
        var phase = ReadString(payloadJson, "phase");
        if (string.IsNullOrEmpty(phase))
        {
            return WorkRequestStatus.Planning;
        }

        if (phase.Contains("plan", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Planning;
        }

        if (phase.Contains("review", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Reviewing;
        }

        if (phase.Contains("verif", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Verifying;
        }

        if (phase.Contains("execut", StringComparison.OrdinalIgnoreCase)
            || phase.Contains("implement", StringComparison.OrdinalIgnoreCase))
        {
            return WorkRequestStatus.Executing;
        }

        return WorkRequestStatus.Planning;
    }

    private static bool IsReviewer(string payloadJson)
    {
        var role = ReadString(payloadJson, "role");
        return role is not null && role.Contains("review", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(string payloadJson, string property)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
