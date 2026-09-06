namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Periodic node heartbeat with active sessions and separate resource and execution snapshots.
/// </summary>
public sealed record NodeHeartbeatMessage(
    Guid NodeId,
    IReadOnlyList<string> ActiveSessionIds,
    NodeResourceSnapshotMessage? Resources = null,
    NodeExecutionStatusMessage? ExecutionStatus = null);

public sealed record NodeResourceSnapshotMessage(
    DateTimeOffset ObservedAt,
    double? CpuUsagePercent,
    long? MemoryUsedBytes,
    long? MemoryTotalBytes,
    long? DiskUsedBytes,
    long? DiskTotalBytes,
    double? LoadAverageOneMinute,
    double? UptimeSeconds);
