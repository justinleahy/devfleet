using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Applies a node heartbeat; also refreshes reported agent metadata.
/// </summary>
public sealed record NodeHeartbeatCommand(
    NodeId Id,
    IReadOnlyList<string> ActiveSessionIds,
    NodeResourceSnapshotDto? Resources = null,
    NodeExecutionStatusDto? ExecutionStatus = null);

public sealed record NodeResourceSnapshotDto(
    DateTimeOffset ObservedAt,
    double? CpuUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    double? LoadAverageOneMinute,
    double? UptimeSeconds);

public sealed record NodeExecutionStatusDto(
    DateTimeOffset ObservedAt,
    int AvailableRequestSlots,
    IReadOnlyList<Guid> ActiveAssignmentIds,
    string RoutingRevision,
    IReadOnlyList<RuntimeRouteReadinessDto> Routes,
    VerificationPolicyCatalogMessage? VerificationPolicy = null);

public sealed record RuntimeRouteReadinessDto(
    string Role,
    string CanonicalModel,
    string Readiness,
    string EvidenceSource,
    DateTimeOffset ObservedAt,
    string RoutingRevision);
