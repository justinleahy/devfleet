using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Requests;

/// <summary>
/// Stable scheduling reason codes in evaluator precedence order.
/// </summary>
public static class SchedulingReasonCodes
{
    public const string ProjectRecoveryPaused = "project_recovery_paused";
    public const string ProjectDisabled = "project_disabled";
    public const string WorkspaceBindingMissing = "workspace_binding_missing";
    public const string WorkspaceValidationPending = "workspace_validation_pending";
    public const string WorkspaceInvalid = "workspace_invalid";
    public const string NodeOffline = "node_offline";
    public const string RuntimeUnavailable = "runtime_unavailable";
    public const string RuntimeUnknown = "runtime_unknown";
    public const string VerificationPolicyUnavailable = "verification_policy_unavailable";
    public const string NodeCapacityUnavailable = "node_capacity_unavailable";
    public const string ProjectConcurrencyUnavailable = "project_concurrency_unavailable";
    public const string Eligible = "eligible";
}

/// <summary>
/// Operator-safe scheduling status for request projections.
/// </summary>
public sealed record SchedulingStatusDto(
    string Code,
    string Detail,
    string Action,
    bool IsEligible);

/// <summary>
/// Immutable result of evaluating one request, optionally for a candidate node.
/// </summary>
/// <param name="RequestId">The evaluated request.</param>
/// <param name="CandidateNodeId">
/// The candidate node required by the caller, or null when evaluating the designated binding node.
/// </param>
/// <param name="Status">The deterministic scheduling status.</param>
/// <param name="EligibleBinding">
/// The validated binding when eligible; null for every ineligible decision.
/// </param>
/// <param name="Assignment">
/// The request's current or historical assignment projection, independent of eligibility.
/// </param>
public sealed record EligibilityDecision(
    WorkRequestId RequestId,
    NodeId? CandidateNodeId,
    SchedulingStatusDto Status,
    WorkspaceBindingDto? EligibleBinding,
    ExecutionAssignmentProjectionDto? Assignment);
