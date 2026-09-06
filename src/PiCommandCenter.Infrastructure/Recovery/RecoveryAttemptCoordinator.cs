using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>
/// Bounded recovery-attempt orchestration. Persistence rows stay inside this module.
/// Rejected proof never terminalizes or resolves reservations.
/// </summary>
public sealed class RecoveryAttemptCoordinator(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IRecoveryTargetTerminalizer terminalizer,
    IProjectionNotifier notifier) : IRecoveryAttemptCoordinator
{
    public const int MaxClaimTokenLength = 128;
    public const int MaxStageLength = 128;
    public const int MaxReasonCodes = 16;
    public const int MaxReasonCodeLength = 64;
    public const int MaxProcessIdentities = 32;
    public const int MaxReservationDispositions = 32;
    public const int MaxInterruptedIndicators = 16;
    public const int MaxSummaryLength = 256;
    public const int MaxEvidenceJsonLength = 8192;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task AcceptProgressAsync(
        NodeId authenticatedNodeId,
        AssignmentRecoveryProgressMessage progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var loaded = await LoadContextAsync(
                authenticatedNodeId,
                progress.RecoveryId,
                progress.Attempt,
                progress.ProjectId,
                progress.RequestId,
                progress.ClaimToken,
                progress.BindingRevision,
                progress.ObservedAt,
                requireCurrentAttempt: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded is null)
        {
            return;
        }

        var (operation, target, assignment, _) = loaded.Value;
        if (IsTerminalOperation(operation) || IsTerminalAssignment(assignment))
        {
            return;
        }

        if (!IsProgressBounded(progress)
            || progress.Children is null
            || progress.Operations is null
            || progress.Processes is null
            || progress.PendingEvents is null
            || progress.Reservations is null
            || !progress.Children.IsValid
            || !progress.Operations.IsValid
            || !progress.Processes.IsValid
            || !progress.PendingEvents.IsValid
            || !progress.Reservations.IsValid)
        {
            return;
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        if (IsStale(operation, progress.ObservedAt, now))
        {
            return;
        }

        var observedTicks = UtcTicks(progress.ObservedAt);
        if (observedTicks < operation.LastProgressUtcTicks)
        {
            return;
        }

        operation.Status = nameof(RecoveryOperationStatus.Running);
        operation.Stage = string.IsNullOrWhiteSpace(progress.Stage) ? operation.Stage : progress.Stage.Trim();
        operation.BlockerCodesJson = SerializeBlockers(progress.ReasonCodes);
        operation.EvidenceJson = BoundJson(SerializeEvidence(new
        {
            progress.ObservedAt,
            progress.Stage,
            Children = DescribeCount(progress.Children),
            Operations = DescribeCount(progress.Operations),
            Processes = DescribeCount(progress.Processes),
            PendingEvents = DescribeCount(progress.PendingEvents),
            Reservations = DescribeCount(progress.Reservations),
            progress.ReasonCodes,
        }));
        operation.LastProgressUtcTicks = observedTicks;
        operation.UpdatedAtUtcTicks = UtcTicks(now);
        operation.Version++;

        db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            ProjectId = operation.ProjectId,
            Kind = "progress",
            Reason = operation.Stage ?? "progress",
            Actor = "node",
            PayloadJson = operation.EvidenceJson,
            AtUtcTicks = UtcTicks(now),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(operation.ProjectId, target.RequestId);
    }

    public async Task<RecoveryProofDecisionMessage> AcceptProofAsync(
        NodeId authenticatedNodeId,
        AssignmentRecoveryProofMessage proof,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var decision = new RecoveryProofDecisionMessage(
            proof.RecoveryId,
            proof.Attempt,
            proof.ProjectId,
            proof.RequestId,
            proof.BindingRevision,
            Accepted: false,
            MissingRequirements: []);

        var loaded = await LoadContextAsync(
                authenticatedNodeId,
                proof.RecoveryId,
                proof.Attempt,
                proof.ProjectId,
                proof.RequestId,
                proof.ClaimToken,
                proof.BindingRevision,
                proof.ObservedAt,
                requireCurrentAttempt: false,
                cancellationToken)
            .ConfigureAwait(false);
        if (loaded is null)
        {
            return decision with { MissingRequirements = [RecoveryReasonCodes.RecoveryTargetChanged] };
        }

        var (operation, target, assignment, request) = loaded.Value;
        if (IsTerminalOperation(operation)
            && !string.IsNullOrWhiteSpace(target.Outcome))
        {
            return decision with { Accepted = true, MissingRequirements = [] };
        }

        if (operation.Attempt != proof.Attempt
            || IsStale(operation, proof.ObservedAt, clock.GetUtcNow().ToUniversalTime()))
        {
            return await RejectAsync(
                    operation,
                    target,
                    decision,
                    [RecoveryReasonCodes.RecoveryEvidenceStale],
                    proof,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (IsTerminalAssignment(assignment) && !string.IsNullOrWhiteSpace(target.Outcome))
        {
            return decision with { Accepted = true, MissingRequirements = [] };
        }

        var missing = CollectProofGaps(proof);
        if (missing.Count > 0)
        {
            return await RejectAsync(operation, target, decision, missing, proof, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(target.Outcome))
        {
            return decision with { Accepted = true, MissingRequirements = [] };
        }

        var intent = ResolveIntent(target, assignment, request);
        var gate = await terminalizer
            .TerminalizeAsync(proof, intent, cancellationToken)
            .ConfigureAwait(false);
        if (!gate.Accepted)
        {
            var blockers = gate.MissingRequirements.Count == 0
                ? [RecoveryReasonCodes.ProcessStopUnproven]
                : gate.MissingRequirements.ToArray();
            return await RejectAsync(operation, target, decision, blockers, proof, cancellationToken)
                .ConfigureAwait(false);
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        var evidence = BoundJson(SerializeEvidence(proof));
        target.Outcome = OutcomeName(intent);
        target.EvidenceJson = evidence;
        operation.EvidenceJson = evidence;
        operation.BlockerCodesJson = "[]";
        operation.Stage = "Resolving execution ownership";
        operation.LastProgressUtcTicks = UtcTicks(now);
        operation.UpdatedAtUtcTicks = UtcTicks(now);
        operation.Version++;

        ResolveReservationTargets(operation, proof);

        if (operation.AssignmentTargets.All(t => !string.IsNullOrWhiteSpace(t.Outcome))
            && operation.ReservationTargets.All(t => !string.IsNullOrWhiteSpace(t.Outcome)))
        {
            operation.Status = nameof(RecoveryOperationStatus.Recovered);
            operation.CompletedAtUtcTicks = UtcTicks(now);
        }
        else
        {
            operation.Status = nameof(RecoveryOperationStatus.Running);
        }

        db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            ProjectId = operation.ProjectId,
            Kind = "proof-accepted",
            Reason = target.Outcome ?? "accepted",
            Actor = "node",
            PayloadJson = evidence,
            AtUtcTicks = UtcTicks(now),
        });

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(operation.ProjectId, target.RequestId);
        return decision with { Accepted = true, MissingRequirements = [] };
    }

    private async Task<(
        RecoveryOperationRow Operation,
        RecoveryTargetRow Target,
        ExecutionAssignment Assignment,
        WorkRequest Request)?> LoadContextAsync(
        NodeId authenticatedNodeId,
        Guid recoveryId,
        int attempt,
        Guid projectId,
        Guid requestId,
        string claimToken,
        long bindingRevision,
        DateTimeOffset observedAt,
        bool requireCurrentAttempt,
        CancellationToken cancellationToken)
    {
        if (authenticatedNodeId.Value == Guid.Empty
            || recoveryId == Guid.Empty
            || projectId == Guid.Empty
            || requestId == Guid.Empty
            || attempt < 1
            || string.IsNullOrWhiteSpace(claimToken)
            || claimToken.Length > MaxClaimTokenLength
            || observedAt == default)
        {
            return null;
        }

        var operation = await db.Set<RecoveryOperationRow>()
            .Include(row => row.AssignmentTargets)
            .Include(row => row.ReservationTargets)
            .SingleOrDefaultAsync(row => row.Id == recoveryId, cancellationToken)
            .ConfigureAwait(false);
        if (operation is null
            || operation.ProjectId != projectId
            || (requireCurrentAttempt && operation.Attempt != attempt))
        {
            return null;
        }

        var target = operation.AssignmentTargets.SingleOrDefault(t => t.RequestId == requestId);
        if (target is null || target.BindingRevision != bindingRevision)
        {
            return null;
        }

        var assignment = await db.ExecutionAssignments
            .SingleOrDefaultAsync(a => a.RequestId == new WorkRequestId(requestId), cancellationToken)
            .ConfigureAwait(false);
        var request = await db.WorkRequests
            .SingleOrDefaultAsync(r => r.Id == new WorkRequestId(requestId), cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null
            || request is null
            || assignment.ProjectId.Value != projectId
            || request.ProjectId.Value != projectId
            || assignment.NodeIdSnapshot != authenticatedNodeId
            || !string.Equals(assignment.ClaimToken, claimToken, StringComparison.Ordinal)
            || assignment.BindingValidationRevisionSnapshot != bindingRevision)
        {
            return null;
        }

        return (operation, target, assignment, request);
    }

    private async Task<RecoveryProofDecisionMessage> RejectAsync(
        RecoveryOperationRow operation,
        RecoveryTargetRow target,
        RecoveryProofDecisionMessage decision,
        IReadOnlyList<string> missing,
        AssignmentRecoveryProofMessage proof,
        CancellationToken cancellationToken)
    {
        if (IsTerminalOperation(operation))
        {
            return decision with { MissingRequirements = missing };
        }

        var now = clock.GetUtcNow().ToUniversalTime();
        operation.Status = nameof(RecoveryOperationStatus.NeedsIntervention);
        operation.BlockerCodesJson = SerializeBlockers(missing);
        operation.EvidenceJson = BoundJson(SerializeEvidence(proof));
        operation.UpdatedAtUtcTicks = UtcTicks(now);
        operation.LastProgressUtcTicks = UtcTicks(now);
        operation.Version++;
        db.Set<RecoveryAuditFactRow>().Add(new RecoveryAuditFactRow
        {
            Id = Guid.NewGuid(),
            OperationId = operation.Id,
            ProjectId = operation.ProjectId,
            Kind = "proof-rejected",
            Reason = missing[0],
            Actor = "node",
            PayloadJson = operation.BlockerCodesJson,
            AtUtcTicks = UtcTicks(now),
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Publish(operation.ProjectId, target.RequestId);
        return decision with { MissingRequirements = missing };
    }

    private static List<string> CollectProofGaps(AssignmentRecoveryProofMessage proof)
    {
        var missing = new List<string>();
        if (!IsProofBounded(proof))
        {
            missing.Add(RecoveryReasonCodes.RecoveryEvidenceStale);
            return missing;
        }

        if (!proof.AdmissionClosed)
        {
            missing.Add(RecoveryReasonCodes.ProcessStopUnproven);
        }

        AddInventoryGap(missing, proof.Children, RecoveryReasonCodes.ProcessStopUnproven);
        AddInventoryGap(missing, proof.Operations, RecoveryReasonCodes.OperationDrainTimeout);
        AddInventoryGap(missing, proof.Processes, RecoveryReasonCodes.ProcessStopUnproven);
        AddInventoryGap(missing, proof.PendingEvents, RecoveryReasonCodes.EventsUnacknowledged);
        AddInventoryGap(missing, proof.Reservations, RecoveryReasonCodes.ReservationUnresolved);

        var ackKnown = proof.EventAcknowledgementPosition is >= 0
            && string.IsNullOrWhiteSpace(proof.EventAcknowledgementUnknownReasonCode);
        var ackUnknown = proof.EventAcknowledgementPosition is null
            && !string.IsNullOrWhiteSpace(proof.EventAcknowledgementUnknownReasonCode);
        if (!ackKnown || ackUnknown)
        {
            missing.Add(ackUnknown
                ? proof.EventAcknowledgementUnknownReasonCode!.Trim()
                : RecoveryReasonCodes.EventsUnacknowledged);
        }

        if (proof.ProcessIdentities.Count > 0
            || proof.ProcessIdentities.Any(p => p.EscapedDescendant))
        {
            missing.Add(RecoveryReasonCodes.ProcessStopUnproven);
        }

        if (proof.ReservationDispositions.Any(d =>
                !string.Equals(d.Disposition, "resolved", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(d.Disposition, "released", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(d.Disposition, "none", StringComparison.OrdinalIgnoreCase)))
        {
            missing.Add(RecoveryReasonCodes.ReservationUnresolved);
        }

        if (proof.Repository is null || !proof.Repository.Available)
        {
            missing.Add(RecoveryReasonCodes.RepositoryStatusUnknown);
        }
        else if (!proof.Repository.UntrackedCount.IsValid)
        {
            missing.Add(RecoveryReasonCodes.RepositoryStatusUnknown);
        }

        return missing.Distinct(StringComparer.Ordinal).ToList();
    }

    private static void AddInventoryGap(List<string> missing, RecoveryKnownCountMessage count, string nonzeroCode)
    {
        if (count is null || !count.IsValid)
        {
            missing.Add(nonzeroCode);
            return;
        }

        if (count.IsUnknown)
        {
            missing.Add(count.UnknownReasonCode!.Trim());
            return;
        }

        if (count.Value != 0)
        {
            missing.Add(nonzeroCode);
        }
    }

    private static bool IsProgressBounded(AssignmentRecoveryProgressMessage progress) =>
        (progress.Stage is null || progress.Stage.Length <= MaxStageLength)
        && progress.ReasonCodes.Count <= MaxReasonCodes
        && progress.ReasonCodes.All(code =>
            !string.IsNullOrWhiteSpace(code) && code.Length <= MaxReasonCodeLength);

    private static bool IsProofBounded(AssignmentRecoveryProofMessage proof) =>
        proof.ProcessIdentities.Count <= MaxProcessIdentities
        && proof.ReservationDispositions.Count <= MaxReservationDispositions
        && proof.ClaimToken.Length <= MaxClaimTokenLength
        && (proof.Repository is null
            || (proof.Repository.InterruptedOperationIndicators.Count <= MaxInterruptedIndicators
                && LengthOk(proof.Repository.Head)
                && LengthOk(proof.Repository.Branch)
                && LengthOk(proof.Repository.IndexSummary)
                && LengthOk(proof.Repository.WorktreeSummary)));

    private static bool LengthOk(string? value) =>
        value is null || value.Length <= MaxSummaryLength;

    private static bool IsStale(RecoveryOperationRow operation, DateTimeOffset observedAt, DateTimeOffset now)
    {
        var observed = observedAt.ToUniversalTime();
        if (observed > now)
        {
            return true;
        }

        if (operation.DeadlineUtcTicks is long deadline && UtcTicks(observed) > deadline)
        {
            return true;
        }

        return operation.DeadlineUtcTicks is long openDeadline && UtcTicks(now) > openDeadline;
    }

    private static bool IsTerminalOperation(RecoveryOperationRow operation) =>
        string.Equals(operation.Status, nameof(RecoveryOperationStatus.Recovered), StringComparison.Ordinal);

    private static bool IsTerminalAssignment(ExecutionAssignment assignment) =>
        assignment.State is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled;

    private static TerminalizationIntent ResolveIntent(
        RecoveryTargetRow target,
        ExecutionAssignment assignment,
        WorkRequest request)
    {
        var capturedFinalizing = string.Equals(
            target.CapturedState,
            nameof(ExecutionAssignmentState.Finalizing),
            StringComparison.Ordinal);
        if (!capturedFinalizing && assignment.State != ExecutionAssignmentState.Finalizing)
        {
            return TerminalizationIntent.Cancel;
        }

        return request.Status == WorkRequestStatus.Verifying
            ? TerminalizationIntent.Complete
            : TerminalizationIntent.Fail;
    }

    private static string OutcomeName(TerminalizationIntent intent) =>
        intent switch
        {
            TerminalizationIntent.Complete => nameof(ExecutionAssignmentState.Completed),
            TerminalizationIntent.Fail => nameof(ExecutionAssignmentState.Failed),
            _ => nameof(ExecutionAssignmentState.Cancelled),
        };

    private static void ResolveReservationTargets(
        RecoveryOperationRow operation,
        AssignmentRecoveryProofMessage proof)
    {
        foreach (var reservation in operation.ReservationTargets)
        {
            if (!string.IsNullOrWhiteSpace(reservation.Outcome))
            {
                continue;
            }

            var disposition = proof.ReservationDispositions
                .FirstOrDefault(d => d.LeaseId == reservation.LeaseId);
            if (disposition is null)
            {
                continue;
            }

            if (string.Equals(disposition.Disposition, "resolved", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition.Disposition, "released", StringComparison.OrdinalIgnoreCase)
                || string.Equals(disposition.Disposition, "none", StringComparison.OrdinalIgnoreCase))
            {
                reservation.Outcome = "Resolved";
                reservation.EvidenceJson = BoundJson(SerializeEvidence(disposition));
            }
        }
    }

    private void Publish(Guid projectId, Guid requestId)
    {
        notifier.Publish(ProjectionChange.Project(projectId));
        notifier.Publish(ProjectionChange.Request(projectId, requestId));
    }

    private static string SerializeBlockers(IReadOnlyList<string> codes) =>
        JsonSerializer.Serialize(
            codes.Where(code => !string.IsNullOrWhiteSpace(code)).Distinct(StringComparer.Ordinal).ToArray(),
            JsonOptions);

    private static string SerializeEvidence(object value) =>
        JsonSerializer.Serialize(value, JsonOptions);

    private static string BoundJson(string json) =>
        json.Length <= MaxEvidenceJsonLength ? json : json[..MaxEvidenceJsonLength];

    private static object DescribeCount(RecoveryKnownCountMessage count) =>
        new { count.Value, count.UnknownReasonCode };

    private static long UtcTicks(DateTimeOffset at) => at.ToUniversalTime().UtcTicks;
}
