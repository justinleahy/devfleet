namespace PiCommandCenter.Domain.Sessions;

/// <summary>Whether the agent needs human or coordinator intervention, per SPEC §21.3.</summary>
public enum AgentAttention
{
    None,
    InputRequired,
    ApprovalRequired,
    ReservationConflict,
    Warning,
    Error,
}
