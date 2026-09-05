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
            case "request.complete":
                return await HandleRequestCompleteAsync(
                    context, payloadData, cancellationToken).ConfigureAwait(false);
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

    private async Task<PiToolResponse> HandleRequestCompleteAsync(
        PiOrchestrationContext context,
        IReadOnlyDictionary<string, object?> payloadData,
        CancellationToken cancellationToken)
    {
        if (context.CreateCheckpointAsync is null)
        {
            return PiToolResponse.Failure(
                NotAvailableUntilChildSupervisor,
                "Tool request 'request.complete' is not available until the child supervisor is implemented.");
        }

        var paths = payloadData.TryGetValue("paths", out var rawPaths)
            && rawPaths is JsonElement { ValueKind: JsonValueKind.Array } pathArray
            ? pathArray.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList()
            : [];
        if (paths.Count == 0)
        {
            return PiToolResponse.Failure(
                "checkpoint_requires_paths",
                "request.complete requires an explicit, non-empty 'paths' list of changed files.");
        }

        var message = payloadData.TryGetValue("message", out var rawMessage)
            && rawMessage is JsonElement { ValueKind: JsonValueKind.String } messageElement
            ? messageElement.GetString()
            : null;
        var branchName = payloadData.TryGetValue("branchName", out var rawBranch)
            && rawBranch is JsonElement { ValueKind: JsonValueKind.String } branchElement
            ? branchElement.GetString()
            : null;

        var checkpoint = await context.CreateCheckpointAsync(
            new PiCheckpointRequest(
                branchName ?? string.Empty,
                message ?? "Checkpoint commit for completed work request",
                paths),
            cancellationToken).ConfigureAwait(false);
        if (!checkpoint.Ok)
        {
            return PiToolResponse.Failure(
                checkpoint.ErrorCode ?? "checkpoint_failed",
                checkpoint.ErrorMessage ?? "Checkpoint commit failed.");
        }

        await context.EmitAsync(
            "repository.checkpoint_created",
            new Dictionary<string, object?>
            {
                ["branchName"] = checkpoint.BranchName,
                ["commitId"] = checkpoint.CommitId,
                ["paths"] = paths,
            },
            cancellationToken).ConfigureAwait(false);
        return PiToolResponse.Success(new { commitId = checkpoint.CommitId, branchName = checkpoint.BranchName });
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
