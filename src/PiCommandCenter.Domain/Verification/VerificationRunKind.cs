namespace PiCommandCenter.Domain.Verification;

/// <summary>
/// Distinguishes final policy runs from child-requested intermediate checks
/// (SPEC streamlined verification).
/// </summary>
public enum VerificationRunKind
{
    Baseline = 0,
    ProjectCheck = 1,
    Intermediate = 2,
}
