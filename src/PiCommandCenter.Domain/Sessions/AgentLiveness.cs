namespace PiCommandCenter.Domain.Sessions;

/// <summary>Whether the agent process is reachable, per SPEC §21.1.</summary>
public enum AgentLiveness
{
    Starting,
    Online,
    Disconnected,
    Exited,
}
