using System.Text.Json;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Default root-orchestration request handler for the PoC root supervisor. Exactly three tools
/// produce durable, observable state — plan submission, plan revision, and blocking the request
/// — by appending normalized events through the session's emit path. Every other root tool
/// (child spawn, agent status, reservations, verification, completion, …) requires the child
/// supervisor and answers with the explicit structured code
/// <c>not_available_until_child_supervisor</c>; success is never faked.
/// </summary>
public sealed class PiOrchestrationRequestHandler : IPiOrchestrationRequestHandler
{
    /// <summary>Structured error code for tools the child supervisor must implement first.</summary>
    public const string NotAvailableUntilChildSupervisor = "not_available_until_child_supervisor";

    private static readonly IReadOnlySet<string> ChildSupervisorGatedTypes =
        new HashSet<string>
        {
            "agent.spawn",
            "agent.status",
            "agent.await",
            "agent.message.send",
            "agent.inbox.read",
            "agent.message.acknowledge",
            "agent.cancel",
            "reservation.acquire",
            "reservation.expand",
            "reservation.release",
            "reservation.handoff.request",
            "project.diff.inspect",
            "verification.request",
            "request.complete",
        };

    public async Task<PiToolResponse> HandleAsync(
        PiOrchestrationContext context,
        string requestType,
        JsonElement? payload,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestType);

        var payloadData = payload is JsonElement element && element.ValueKind == JsonValueKind.Object
            ? ToDictionary(element)
            : new Dictionary<string, object?>();

        switch (requestType)
        {
            case "plan.submit":
                return await PersistRequestEventAsync(
                    context, "request.phase_changed", "plan", payloadData, cancellationToken)
                    .ConfigureAwait(false);
            case "plan.revise":
                return await PersistRequestEventAsync(
                    context, "request.phase_changed", "plan_revision", payloadData, cancellationToken)
                    .ConfigureAwait(false);
            case "request.block":
                return await PersistRequestEventAsync(
                    context, "request.blocked", "blocked", payloadData, cancellationToken)
                    .ConfigureAwait(false);
            case var gated when ChildSupervisorGatedTypes.Contains(gated):
                return PiToolResponse.Failure(
                    NotAvailableUntilChildSupervisor,
                    $"Tool request '{requestType}' is not available until the child supervisor is implemented.");
            default:
                return PiToolResponse.Failure(
                    "unknown_request_type",
                    $"Unknown root tool request type '{requestType}'.");
        }
    }

    private async Task<PiToolResponse> PersistRequestEventAsync(
        PiOrchestrationContext context,
        string eventType,
        string phase,
        IReadOnlyDictionary<string, object?> payloadData,
        CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object?>(payloadData) { ["phase"] = phase };
        await context.EmitAsync(eventType, data, cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new { phase, eventType });
    }

    private static Dictionary<string, object?> ToDictionary(JsonElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            result[property.Name] = property.Value.Clone();
        }

        return result;
    }
}
