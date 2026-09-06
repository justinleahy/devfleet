using System.Collections.Generic;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node;

/// <summary>
/// Durable node-side view of an execution assignment and the local facts reported during reconciliation.
/// </summary>
public sealed record NodeAssignmentJournalEntry(
    ExecutionAssignmentMessage Assignment,
    AssignmentSupervisorState SupervisorState,
    bool RepositoryKnown,
    int PendingEventCount,
    IReadOnlyList<AssignmentProcessIdentity>? ProcessIdentities = null);

/// <summary>Raised when a persisted assignment cannot be reconstructed safely.</summary>
public sealed class NodeAssignmentJournalCorruptionException : Exception
{
    public NodeAssignmentJournalCorruptionException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
