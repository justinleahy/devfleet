using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Mail;

/// <summary>Project-scoped agent identity bound to one active session (SPEC §16.1).</summary>
public sealed record AgentIdentityDto(
    ProjectId ProjectId,
    string SessionId,
    string AgentName,
    string Role,
    string Runtime,
    DateTimeOffset AllocatedAtUtc);
