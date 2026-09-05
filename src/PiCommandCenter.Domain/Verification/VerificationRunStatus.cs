namespace PiCommandCenter.Domain.Verification;

/// <summary>Outcome of one configured verification command (SPEC §20, §29).</summary>
public enum VerificationRunStatus
{
    Running = 0,
    Passed = 1,
    Failed = 2,
    TimedOut = 3,
    Cancelled = 4,
}
