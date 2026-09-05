using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Mail;

/// <summary>Per-recipient delivery state of one mail message.</summary>
public sealed record AgentMessageRecipientDto(
    string SessionId,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

/// <summary>
/// A mail-like coordination message (SPEC §16.4). <c>SenderSessionId</c> is null exactly when
/// the message was written by the human user through the browser (<see cref="IsFromHuman"/>).
/// </summary>
public sealed record AgentMessageDto(
    string Id,
    ProjectId ProjectId,
    WorkRequestId RequestId,
    string ThreadId,
    string? SenderSessionId,
    bool IsFromHuman,
    IReadOnlyList<AgentMessageRecipientDto> Recipients,
    string Subject,
    string BodyMarkdown,
    MessageImportance Importance,
    bool AcknowledgementRequired,
    DateTimeOffset CreatedAtUtc);
