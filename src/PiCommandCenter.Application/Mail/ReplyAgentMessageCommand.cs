using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Application.Mail;

/// <summary>
/// Command to reply inside an existing thread. Recipients are derived from the thread's
/// participants (prior senders and recipients), excluding the replying session itself.
/// </summary>
public sealed record ReplyAgentMessageCommand(
    ProjectId ProjectId,
    string ThreadId,
    string SenderSessionId,
    string BodyMarkdown,
    MessageImportance Importance,
    bool AckRequired);
