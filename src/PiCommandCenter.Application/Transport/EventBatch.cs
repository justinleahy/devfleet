namespace PiCommandCenter.Application.Transport;

/// <summary>
/// An idempotent batch of node events transported in one message.
/// </summary>
public sealed record EventBatch(IReadOnlyList<NodeEventDto> Events);
