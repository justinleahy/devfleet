using System.ComponentModel.DataAnnotations;

namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>Allocates one project-scoped agent identity for a live node session.</summary>
public sealed record AllocateAgentIdentityMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string SessionId,
    string RequestedName,
    string Role,
    string Runtime);

/// <summary>Persisted identity returned to the node after collision-safe allocation.</summary>
public sealed record AgentIdentityMessage(
    Guid ProjectId,
    string SessionId,
    string AgentName,
    string Role,
    string Runtime,
    DateTimeOffset AllocatedAtUtc);

/// <summary>Looks up an active project-scoped identity by its allocated name.</summary>
public sealed record FindAgentIdentityMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string AgentName);

/// <summary>Releases the identity owned by a terminal node session.</summary>
public sealed record ReleaseAgentIdentityMessage(
    Guid ProjectId,
    Guid RequestId,
    [property: Required, MaxLength(128)] string ClaimToken,
    string SessionId);
