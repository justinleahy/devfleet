using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Runtime;

/// <summary>
/// Everything a runtime adapter needs to start one agent session. Validated at construction;
/// throws <see cref="ArgumentException"/> when an identifier or label is empty.
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
        string runtimeProfile,
        AgentRuntimeAuthorizationContext? authorization = null,
        bool createRequestCommit = false)
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
        RuntimeProfile = Require(runtimeProfile, nameof(runtimeProfile));
        Authorization = authorization;
        CreateRequestCommit = createRequestCommit;
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

    /// <summary>Runtime profile per SPEC §15.1.</summary>
    public string RuntimeProfile { get; }

    /// <summary>
    /// Successful reservation grant for reserved-write Claude. Null for read-only starts.
    /// </summary>
    public AgentRuntimeAuthorizationContext? Authorization { get; }

    /// <summary>Whether the trusted supervisor must create a request checkpoint at completion.</summary>
    public bool CreateRequestCommit { get; }

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
}
