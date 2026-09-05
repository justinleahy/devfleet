using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Mail;

/// <summary>
/// Allocates project-scoped agent identities for active sessions (SPEC §16.1). Names are
/// unique among active identities within a project; a colliding requested name resolves
/// deterministically to the lowest free {@code name-2}, {@code name-3}, … suffix.
/// </summary>
public interface IAgentIdentityRegistry
{
    /// <summary>
    /// Allocates an identity for the session. Idempotent per session: re-allocating for a
    /// session that already holds an identity returns it unchanged.
    /// </summary>
    /// <exception cref="MailValidationException">A command field is empty or malformed.</exception>
    Task<AgentIdentityDto> AllocateAsync(AllocateAgentIdentityCommand command, CancellationToken cancellationToken = default);

    /// <summary>Releases the session's identity, freeing its name. No-op when none is held.</summary>
    Task ReleaseAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Looks up the active identity carrying the given name in the project.</summary>
    Task<AgentIdentityDto?> FindByNameAsync(ProjectId projectId, string agentName, CancellationToken cancellationToken = default);
}
