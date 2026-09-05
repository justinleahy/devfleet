namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: periodic node heartbeat with locally active session ids.
/// </summary>
public sealed record NodeHeartbeatMessage(
    Guid NodeId,
    IReadOnlyList<string> ActiveSessionIds,
    NodeResourceSnapshotMessage? Resources = null);

public sealed record NodeResourceSnapshotMessage(
    DateTimeOffset ObservedAt,
    double? CpuUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    double? LoadAverageOneMinute,
    double? UptimeSeconds);
