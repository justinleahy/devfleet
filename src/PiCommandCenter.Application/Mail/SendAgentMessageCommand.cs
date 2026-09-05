using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Mail;

/// <summary>
/// Command to send a direct or multi-recipient message. <c>SenderSessionId</c> is null for
/// human guidance sent from the browser; otherwise it must identify an active session of the
/// same project and request as every listed recipient.
/// </summary>
public sealed record SendAgentMessageCommand(
    ProjectId ProjectId,
    WorkRequestId RequestId,
    string ThreadId,
    string? SenderSessionId,
    IReadOnlyList<string> Recipients,
    string Subject,
    string BodyMarkdown,
    MessageImportance Importance,
    bool AckRequired);
