using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Everything a runtime adapter needs to start one agent session. Validated at construction;
/// throws <see cref="ArgumentException"/> when an identifier or label is empty or the model
/// selector is not canonical.
/// </summary>
public sealed record AgentStartRequest
{
    public AgentStartRequest(
        string sessionId,
        ProjectId projectId,
        WorkRequestId requestId,
        string? parentSessionId,
        string agentName,
        string role,
        string workingDirectory,
        string prompt,
        AgentRuntimeMode mode,
        string model,
        AgentRuntimeAuthorizationContext? authorization = null,
        bool createRequestCommit = false,
        WorkspaceBindingId? workspaceBindingId = null,
        long? bindingValidationRevisionSnapshot = null,
        string? verificationPolicyRevision = null,
        string? baselineVersion = null,
        string? trustedVerificationProfileId = null,
        string? trustedVerificationProfileRevision = null,
        IReadOnlyList<string>? mandatoryVerificationCommandIds = null)
    {
        SessionId = Require(sessionId, nameof(sessionId));
        ProjectId = projectId;
        RequestId = requestId;
        ParentSessionId = Optional(parentSessionId, nameof(parentSessionId));
        AgentName = Require(agentName, nameof(agentName));
        Role = Require(role, nameof(role));
        WorkingDirectory = Require(workingDirectory, nameof(workingDirectory));
        Prompt = Require(prompt, nameof(prompt));
        Mode = mode;
        Model = AgentModelSelector.Parse(model);
        Authorization = authorization;
        CreateRequestCommit = createRequestCommit;
        WorkspaceBindingId = workspaceBindingId;
        BindingValidationRevisionSnapshot = ValidateRevision(
            bindingValidationRevisionSnapshot,
            nameof(bindingValidationRevisionSnapshot));
        VerificationPolicyRevision = Optional(
            verificationPolicyRevision,
            nameof(verificationPolicyRevision));
        BaselineVersion = Optional(baselineVersion, nameof(baselineVersion));
        TrustedVerificationProfileId = Optional(
            trustedVerificationProfileId,
            nameof(trustedVerificationProfileId));
        TrustedVerificationProfileRevision = Optional(
            trustedVerificationProfileRevision,
            nameof(trustedVerificationProfileRevision));
        MandatoryVerificationCommandIds = NormalizeCommandIds(
            mandatoryVerificationCommandIds,
            nameof(mandatoryVerificationCommandIds));
    }

    /// <summary>Orchestrator-assigned session id (the projection identity).</summary>
    public string SessionId { get; }

    /// <summary>Owning project.</summary>
    public ProjectId ProjectId { get; }

    /// <summary>Work request the session works.</summary>
    public WorkRequestId RequestId { get; }

    /// <summary>Null for a root session.</summary>
    public string? ParentSessionId { get; }

    /// <summary>Mail-like agent identity name.</summary>
    public string AgentName { get; }

    /// <summary>Role label, e.g. <c>root</c>, <c>implementer</c>, <c>reviewer</c>.</summary>
    public string Role { get; }

    /// <summary>Absolute working directory the agent operates in.</summary>
    public string WorkingDirectory { get; }

    /// <summary>Initial instruction prompt.</summary>
    public string Prompt { get; }

    /// <summary>Root or child position in the tree.</summary>
    public AgentRuntimeMode Mode { get; }

    /// <summary>Canonical <c>runtime/model</c> selector chosen by the trusted node route.</summary>
    public AgentModelSelector Model { get; }

    /// <summary>
    /// Successful reservation grant for reserved-write Claude. Null for read-only starts.
    /// </summary>
    public AgentRuntimeAuthorizationContext? Authorization { get; }

    /// <summary>Whether the trusted supervisor must create a request checkpoint at completion.</summary>
    public bool CreateRequestCommit { get; }

    /// <summary>Workspace binding captured by the execution assignment.</summary>
    public WorkspaceBindingId? WorkspaceBindingId { get; }

    /// <summary>Validation revision captured with the assigned workspace binding.</summary>
    public long? BindingValidationRevisionSnapshot { get; }

    /// <summary>Captured effective verification-policy revision, or null for a pre-upgrade assignment.</summary>
    public string? VerificationPolicyRevision { get; }

    /// <summary>Captured baseline version, or null for a pre-upgrade assignment.</summary>
    public string? BaselineVersion { get; }

    /// <summary>Captured trusted verification profile id, when the assignment selected one.</summary>
    public string? TrustedVerificationProfileId { get; }

    /// <summary>Captured trusted verification profile revision, when the assignment selected one.</summary>
    public string? TrustedVerificationProfileRevision { get; }

    /// <summary>Captured command ids that must pass before the assignment can complete.</summary>
    public IReadOnlyList<string>? MandatoryVerificationCommandIds { get; }

    private static string Require(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length == 0)
        {
            throw new ArgumentException($"{paramName} must not be empty.", paramName);
        }

        return value.Trim();
    }

    private static string? Optional(string? value, string paramName)
    {
        if (value is null)
        {
            return null;
        }

        var clean = value.Trim();
        if (clean.Length == 0)
        {
            throw new ArgumentException($"{paramName} must be null or non-empty.", paramName);
        }

        return clean;
    }

    private static long? ValidateRevision(long? revision, string paramName)
    {
        if (revision is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                revision,
                "Binding validation revision must be positive when present.");
        }

        return revision;
    }

    private static IReadOnlyList<string>? NormalizeCommandIds(
        IReadOnlyList<string>? commandIds,
        string paramName)
    {
        if (commandIds is null)
        {
            return null;
        }

        var normalized = new string[commandIds.Count];
        for (var index = 0; index < commandIds.Count; index++)
        {
            normalized[index] = Require(commandIds[index], paramName);
        }

        return normalized;
    }
}
