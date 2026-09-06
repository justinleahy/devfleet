namespace PiCommandCenter.Domain.Completion;

/// <summary>Stable missing-criterion codes returned by the completion gate.</summary>
public static class CompletionRequirements
{
    public const string PlanEvent = "plan-event";
    public const string ImplementationChild = "implementation-child";
    public const string IndependentReviewer = "independent-reviewer";
    public const string UnresolvedBlockingFinding = "unresolved-blocking-finding";
    public const string MandatoryVerification = "mandatory-verification";
    public const string VerificationEvidence = "verification-evidence";
    public const string VerificationStale = "verification_stale";
    public const string ActiveMutation = "active-mutation";
    public const string ActiveReservation = "active-reservation";
    public const string DiffCaptured = "diff-captured";
    public const string OwnershipKnown = "ownership-known";
    public const string ResultSummary = "result-summary";
    public const string CompletionEvidence = "completion-evidence";
    public const string TerminalizationReason = "terminalization-reason";
    public const string QuiescenceAdmission = "quiescence-admission";
    public const string QuiescenceChildren = "quiescence-children";
    public const string QuiescenceOperations = "quiescence-operations";
    public const string QuiescenceProcesses = "quiescence-processes";
    public const string QuiescenceEvents = "quiescence-events";
    public const string QuiescenceReservations = "quiescence-reservations";
    public const string QuiescenceRepository = "quiescence-repository";

    public static string VerificationNotRun(string commandId) =>
        $"{commandId} verification has not run";

    public static string VerificationFailed(string commandId) =>
        $"{commandId} verification failed";
}
