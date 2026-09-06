using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>
/// Loads the current unresolved recovery attempt and each still-nonterminal
/// retained assignment, then delivers RecoverAssignment. Failed delivery
/// persists NeedsIntervention without clearing hold or ownership.
/// </summary>
public sealed class RecoveryAttemptDispatcher(
    TimeProvider clock,
    ControlPlaneDbContext db,
    INodeRecoveryCommandGateway gateway,
    IProjectionNotifier notifier) : IRecoveryAttemptDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task DispatchAsync(
        ProjectId projectId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty)
        {
            return;
        }

        var operation = await db.Set<RecoveryOperationRow>()
            .Include(row => row.AssignmentTargets)
            .SingleOrDefaultAsync(
                row => row.Id == operationId && row.ProjectId == projectId.Value,
                cancellationToken)
            .ConfigureAwait(false);
        if (operation is null || IsTerminalOperation(operation))
        {
            return;
        }

        await DispatchOperationAsync(operation, nodeFilter: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task DispatchForNodeAsync(
        NodeId nodeId,
        CancellationToken cancellationToken = default)
    {
        var recovered = nameof(RecoveryOperationStatus.Recovered);
        var operations = await db.Set<RecoveryOperationRow>()
            .Include(row => row.AssignmentTargets)
            .Where(row => row.Status != recovered)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var operation in operations)
        {
            await DispatchOperationAsync(operation, nodeId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DispatchOperationAsync(
        RecoveryOperationRow operation,
        NodeId? nodeFilter,
        CancellationToken cancellationToken)
    {
        var openTargets = operation.AssignmentTargets
            .Where(target => string.IsNullOrWhiteSpace(target.Outcome))
            .ToList();
        if (openTargets.Count == 0)
        {
            return;
        }

        var requestIds = openTargets.Select(target => new WorkRequestId(target.RequestId)).ToList();
        var assignments = await db.ExecutionAssignments
            .Where(assignment => requestIds.Contains(assignment.RequestId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var assignmentByRequest = assignments.ToDictionary(assignment => assignment.RequestId.Value);

        var unreachable = false;
        Guid? firstUnreachableRequest = null;
        foreach (var target in openTargets)
        {
            if (!assignmentByRequest.TryGetValue(target.RequestId, out var assignment)
                || IsTerminalAssignment(assignment))
            {
                continue;
            }

            if (nodeFilter is { } filter && assignment.NodeIdSnapshot != filter)
            {
                continue;
            }

            var deadline = operation.DeadlineUtcTicks is long ticks
                ? new DateTimeOffset(ticks, TimeSpan.Zero)
                : clock.GetUtcNow().ToUniversalTime();
            var command = new RecoverAssignmentCommandMessage(
                operation.Id,
                operation.Attempt,
                operation.ProjectId,
                target.RequestId,
                assignment.ClaimToken,
                target.BindingRevision,
                deadline);

            var sent = await gateway.TrySendAsync(assignment.NodeIdSnapshot, command, cancellationToken)
                .ConfigureAwait(false);
            if (!sent)
            {
                unreachable = true;
                firstUnreachableRequest ??= target.RequestId;
            }
        }

        if (!unreachable)
        {
            return;
        }

        PersistUnreachable(operation);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        notifier.Publish(ProjectionChange.Project(operation.ProjectId));
        if (firstUnreachableRequest is { } requestId)
        {
            notifier.Publish(ProjectionChange.Request(operation.ProjectId, requestId));
        }
    }

    private void PersistUnreachable(RecoveryOperationRow operation)
    {
        var now = clock.GetUtcNow().ToUniversalTime();
        var ticks = now.UtcTicks;
        operation.Status = nameof(RecoveryOperationStatus.NeedsIntervention);
        operation.BlockerCodesJson = MergeBlockers(operation.BlockerCodesJson, RecoveryReasonCodes.NodeUnreachable);
        operation.UpdatedAtUtcTicks = ticks;
        operation.LastProgressUtcTicks = ticks;
        db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            ProjectId = operation.ProjectId,
            Kind = "node_unreachable",
            Reason = RecoveryReasonCodes.NodeUnreachable,
            Actor = "control-plane",
            PayloadJson = operation.BlockerCodesJson,
            AtUtcTicks = ticks,
        });
    }

    private static string MergeBlockers(string? existingJson, string code)
    {
        var codes = new List<string>();
        if (!string.IsNullOrWhiteSpace(existingJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<string[]>(existingJson, JsonOptions);
                if (parsed is not null)
                {
                    codes.AddRange(parsed.Where(item => !string.IsNullOrWhiteSpace(item)));
                }
            }
            catch (JsonException)
            {
            }
        }

        if (!codes.Contains(code, StringComparer.Ordinal))
        {
            codes.Add(code);
        }

        return JsonSerializer.Serialize(codes.Distinct(StringComparer.Ordinal).ToArray(), JsonOptions);
    }

    private static bool IsTerminalOperation(RecoveryOperationRow operation) =>
        string.Equals(operation.Status, nameof(RecoveryOperationStatus.Recovered), StringComparison.Ordinal);

    private static bool IsTerminalAssignment(ExecutionAssignment assignment) =>
        assignment.State is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;
}
