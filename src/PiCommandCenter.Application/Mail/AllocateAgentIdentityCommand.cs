using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Mail;

/// <summary>Command to allocate a project-scoped agent identity for one active session.</summary>
public sealed record AllocateAgentIdentityCommand(
    ProjectId ProjectId,
    string SessionId,
    string RequestedName,
    string Role,
    string Runtime);
