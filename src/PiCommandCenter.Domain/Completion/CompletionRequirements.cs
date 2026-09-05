namespace PiCommandCenter.Domain.Completion;

/// <summary>Stable missing-criterion codes returned by the completion gate.</summary>
public static class CompletionRequirements
{
    public const string PlanEvent = "plan-event";
    public const string ImplementationChild = "implementation-child";
    public const string IndependentReviewer = "independent-reviewer";
    public const string UnresolvedBlockingFinding = "unresolved-blocking-finding";
    public const string MandatoryVerification = "mandatory-verification";
    public const string ActiveMutation = "active-mutation";
    public const string ActiveReservation = "active-reservation";
    public const string DiffCaptured = "diff-captured";
    public const string OwnershipKnown = "ownership-known";
    public const string ResultSummary = "result-summary";
}
