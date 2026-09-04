namespace PiCommandCenter.Domain.Sessions;

/// <summary>What the agent is doing right now, per SPEC §21.2. Idle requires an explicit signal.</summary>
public enum AgentActivity
{
    Idle,
    Planning,
    Reasoning,
    Responding,
    RunningTool,
    WaitingForReservation,
    WaitingForChild,
    WaitingForMessage,
    Reviewing,
    Verifying,
    Finalizing,
}
