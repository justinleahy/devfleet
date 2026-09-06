namespace PiCommandCenter.Node;

/// <summary>
/// Durable node-side assignment journal. An assignment is written before local execution starts
/// and deleted only after the Control Plane authoritatively accepts its terminal state.
/// </summary>
public interface INodeAssignmentJournal : IAsyncDisposable
{
    /// <summary>
    /// Loads every durable assignment. Persisted supervisor observations are returned as
    /// <c>Unknown</c> because they cannot establish process state after restart.
    /// </summary>
    Task<IReadOnlyList<NodeAssignmentJournalEntry>> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Creates or completely replaces the entry for the assignment request.</summary>
    Task UpsertAsync(NodeAssignmentJournalEntry entry, CancellationToken cancellationToken);

    /// <summary>Deletes exactly the entry identified by <paramref name="requestId"/>.</summary>
    Task DeleteAsync(Guid requestId, CancellationToken cancellationToken);
}
