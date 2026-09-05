using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Nodes;

/// <summary>
/// Applies a node heartbeat; also refreshes reported agent metadata.
/// </summary>
public sealed record NodeHeartbeatCommand(
    NodeId Id,
    IReadOnlyList<string> ActiveSessionIds,
    NodeResourceSnapshotDto? Resources = null);

public sealed record NodeResourceSnapshotDto(
    DateTimeOffset ObservedAt,
    double? CpuUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    double? LoadAverageOneMinute,
    double? UptimeSeconds);
