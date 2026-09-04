namespace PiCommandCenter.Domain.Sessions;

/// <summary>Where the session sits in the request pipeline, per SPEC §21.4.</summary>
public enum AgentWorkState
{
    Queued,
    Starting,
    Planning,
    Executing,
    Reviewing,
    Verifying,
    Blocked,
    Completed,
    Failed,
    Cancelled,
}
