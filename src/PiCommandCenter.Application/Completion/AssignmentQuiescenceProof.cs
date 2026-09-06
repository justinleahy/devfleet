namespace PiCommandCenter.Application.Completion;

/// <summary>
/// Node-attested proof that an assignment is quiescent. The terminalization authority accepts
/// a confirmation only when every count is exactly zero and both flags are true.
/// </summary>
public sealed record AssignmentQuiescenceProof(
    bool AdmissionClosed,
    int ActiveChildren,
    int ActiveOperations,
    int ActiveProcesses,
    int PendingEvents,
    int ActiveReservations,
    bool RepositoryInspected,
    DateTimeOffset ObservedAt);
