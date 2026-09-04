namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport message: an idempotent batch of node events.
/// </summary>
public sealed record NodeEventBatchMessage(IReadOnlyList<NodeEventMessage> Events);
