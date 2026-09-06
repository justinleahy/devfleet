using PiCommandCenter.Application.Completion;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>The authoritative outcome of a root-session terminalization attempt.</summary>
public enum RootTerminalizationOutcome
{
    Accepted,
    Rejected,
    Uncertain,
}

/// <summary>
/// Serializes the authoritative Begin call with the node's durable assignment-state update so
/// reconciliation cannot observe stale local evidence after the Control Plane accepts Begin.
/// </summary>
public interface INodeAssignmentTerminalizationOrchestrator
{
    Task<CompletionGateDecision> BeginTerminalizationAsync(
        Guid requestId,
        TerminalizationIntent intent,
        Func<CancellationToken, Task<CompletionGateDecision>> beginAsync,
        CancellationToken cancellationToken);
}

/// <summary>
/// Terminalizes a stopped or stopping root session only after every request-owned session and
/// admitted activity has stopped and the node can provide an exact quiescence proof.
/// </summary>
public interface IRootSessionTerminalizer
{
    Task<RootTerminalizationOutcome> CancelAsync(
        ExecutionAssignmentMessage assignment,
        string rootSessionId,
        string reason,
        CancellationToken cancellationToken);

    Task<RootTerminalizationOutcome> FailAsync(
        ExecutionAssignmentMessage assignment,
        string rootSessionId,
        string reason,
        CancellationToken cancellationToken);
}
