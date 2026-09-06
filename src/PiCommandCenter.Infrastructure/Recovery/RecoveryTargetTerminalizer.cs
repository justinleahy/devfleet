using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>
/// Sole seam from an already-validated recovery proof to the existing terminalization
/// authority. Cancelling targets confirm Cancel; Finalizing targets replay the exact
/// persisted Complete/Fail intent. Request status is never used to infer Complete.
/// </summary>
public sealed class RecoveryTargetTerminalizer(
    ControlPlaneDbContext db,
    IAssignmentTerminalizationService terminalization) : IRecoveryTargetTerminalizer
{
    public const string RecoveryCancelReason = "project recovery";

    private static readonly JsonSerializerOptions EvidenceJsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task<CompletionGateDecision> TerminalizeAsync(
        AssignmentRecoveryProofMessage proof,
        TerminalizationIntent acceptedIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(proof);

        var assignment = await db.ExecutionAssignments
            .SingleOrDefaultAsync(a => a.RequestId == new WorkRequestId(proof.RequestId), cancellationToken)
            .ConfigureAwait(false);
        if (assignment is null
            || assignment.ProjectId.Value != proof.ProjectId
            || !string.Equals(assignment.ClaimToken, proof.ClaimToken, StringComparison.Ordinal))
        {
            return Rejected();
        }

        var quiescence = KnownZeroProof(proof.ObservedAt);

        if (acceptedIntent == TerminalizationIntent.Cancel)
        {
            return await terminalization
                .ConfirmAsync(
                    assignment.NodeIdSnapshot,
                    assignment.ProjectId,
                    assignment.RequestId,
                    assignment.ClaimToken,
                    rootSessionId: null,
                    TerminalizationIntent.Cancel,
                    evidence: null,
                    RecoveryCancelReason,
                    quiescence,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (acceptedIntent is not TerminalizationIntent.Complete and not TerminalizationIntent.Fail)
        {
            return Rejected();
        }

        var pending = await db.PendingTerminalizations
            .SingleOrDefaultAsync(row => row.RequestId == assignment.RequestId, cancellationToken)
            .ConfigureAwait(false);
        if (pending is null
            || pending.ProjectId != assignment.ProjectId
            || pending.NodeId != assignment.NodeIdSnapshot
            || !string.Equals(pending.ClaimToken, proof.ClaimToken, StringComparison.Ordinal)
            || !TryParseIntent(pending.Intent, out var persistedIntent)
            || persistedIntent != acceptedIntent
            || persistedIntent is not TerminalizationIntent.Complete and not TerminalizationIntent.Fail)
        {
            return Rejected();
        }

        if (!TryDeserializeEvidence(pending.CompletionEvidenceJson, persistedIntent, out var evidence))
        {
            return Rejected();
        }

        return await terminalization
            .ConfirmAsync(
                assignment.NodeIdSnapshot,
                assignment.ProjectId,
                assignment.RequestId,
                assignment.ClaimToken,
                pending.RootSessionId,
                persistedIntent,
                evidence,
                pending.Reason,
                quiescence,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static CompletionGateDecision Rejected() =>
        new(false, [RecoveryReasonCodes.RecoveryTargetChanged], null);

    private static AssignmentQuiescenceProof KnownZeroProof(DateTimeOffset observedAt) =>
        new(
            AdmissionClosed: true,
            ActiveChildren: 0,
            ActiveOperations: 0,
            ActiveProcesses: 0,
            PendingEvents: 0,
            ActiveReservations: 0,
            RepositoryInspected: true,
            ObservedAt: observedAt);

    private static bool TryParseIntent(string intent, out TerminalizationIntent parsed)
    {
        parsed = default;
        return Enum.TryParse(intent, ignoreCase: false, out parsed)
            && Enum.IsDefined(parsed);
    }

    private static bool TryDeserializeEvidence(
        string? json,
        TerminalizationIntent intent,
        out CompletionEvidence? evidence)
    {
        evidence = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            return intent != TerminalizationIntent.Complete;
        }

        if (json.Length > PendingTerminalizationRow.MaxCompletionEvidenceJsonLength)
        {
            return false;
        }

        try
        {
            evidence = JsonSerializer.Deserialize<CompletionEvidence>(json, EvidenceJsonOptions);
            return evidence is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
