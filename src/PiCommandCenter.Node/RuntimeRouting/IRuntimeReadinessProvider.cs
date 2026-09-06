using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.RuntimeRouting;

/// <summary>Captures node capacity and fail-closed readiness for the live runtime routes.</summary>
public interface IRuntimeReadinessProvider
{
    NodeExecutionStatusMessage Capture(IReadOnlyList<Guid> activeAssignmentIds);
}
