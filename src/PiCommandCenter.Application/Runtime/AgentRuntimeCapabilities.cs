namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// What controls a runtime supports. Capability flags, never hardcoded version checks (SPEC §28).
/// </summary>
public sealed record AgentRuntimeCapabilities(
    bool SupportsStreamingEvents,
    bool SupportsSendInput,
    bool SupportsCancel,
    bool SupportsSnapshot,
    bool SupportsChildSpawn,
    bool SupportsPlanTools)
{
    public static AgentRuntimeCapabilities None { get; } = new(
        SupportsStreamingEvents: false,
        SupportsSendInput: false,
        SupportsCancel: false,
        SupportsSnapshot: false,
        SupportsChildSpawn: false,
        SupportsPlanTools: false);
}
