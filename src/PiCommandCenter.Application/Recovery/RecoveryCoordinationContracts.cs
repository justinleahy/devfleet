using PiCommandCenter.Application.Completion;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Recovery;

/// <summary>
/// Bounded attempt orchestration for project recovery. Accepts attempt-specific
/// progress and proof from the assigned node and returns a typed decision.
/// Does not expose persistence rows. Ordinary request cancellation semantics are
/// unchanged; recovery never resumes an interrupted assignment.
/// </summary>
public interface IRecoveryAttemptCoordinator
{
    /// <summary>
    /// Records correlated progress for the current recovery attempt. Stale
    /// attempt, assignment, or binding evidence is rejected without mutating
    /// durable targets.
    /// </summary>
    Task AcceptProgressAsync(
        NodeId authenticatedNodeId,
        AssignmentRecoveryProgressMessage progress,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Evaluates an assignment-specific recovery proof against the current
    /// attempt. The claim token remains a fence. Acceptance requires known-zero
    /// inventories, closed admission, and a present repository snapshot.
    /// The authenticated node identity is supplied separately from the untrusted
    /// proof payload.
    /// </summary>
    Task<RecoveryProofDecisionMessage> AcceptProofAsync(
        NodeId authenticatedNodeId,
        AssignmentRecoveryProofMessage proof,
        CancellationToken cancellationToken = default);

}

/// <summary>
/// Terminalization seam for a validated recovery target. Preserves an already
/// accepted Finalizing <see cref="TerminalizationIntent.Complete"/> or
/// <see cref="TerminalizationIntent.Fail"/>; otherwise terminalizes as
/// <see cref="TerminalizationIntent.Cancel"/>. Does not silently cancel
/// accepted completion intent.
/// </summary>
public interface IRecoveryTargetTerminalizer
{
    /// <summary>
    /// Commits the persisted outcome for a validated target and proof.
    /// <paramref name="acceptedIntent"/> is the intent already accepted at
    /// BeginTerminalization when the target was Finalizing; otherwise Cancel.
    /// </summary>
    Task<CompletionGateDecision> TerminalizeAsync(
        AssignmentRecoveryProofMessage proof,
        TerminalizationIntent acceptedIntent,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Delivers the current unresolved recovery attempt to retained assignment owners.
/// Offline delivery fails closed without clearing hold or ownership.
/// </summary>
public interface IRecoveryAttemptDispatcher
{
    Task DispatchAsync(
        ProjectId projectId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    Task DispatchForNodeAsync(
        NodeId nodeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends <see cref="RecoverAssignmentCommandMessage"/> to a node's current connection.
/// Returns false when the node is disconnected or the send cannot complete.
/// </summary>
public interface INodeRecoveryCommandGateway
{
    Task<bool> TrySendAsync(
        NodeId nodeId,
        RecoverAssignmentCommandMessage command,
        CancellationToken cancellationToken = default);
}

