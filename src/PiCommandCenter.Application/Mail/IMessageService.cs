namespace PiCommandCenter.Application.Mail;

/// <summary>
/// Mail-like coordination surface (SPEC §16.3): direct and multi-recipient send, reply,
/// unread inbox, thread history, mark-read, and required acknowledgement. Implementations
/// must validate that sender and every recipient are active sessions of the command's
/// project and request.
/// </summary>
public interface IMessageService
{
    /// <summary>Sends a direct or multi-recipient message, creating it in the given thread.</summary>
    /// <exception cref="MailValidationException">The command violates a mail invariant.</exception>
    /// <exception cref="MailSessionNotFoundException">Sender or a recipient is not an active session of the project and request.</exception>
    Task<AgentMessageDto> SendAsync(SendAgentMessageCommand command, CancellationToken cancellationToken = default);

    /// <summary>Replies in an existing thread to all other thread participants.</summary>
    /// <exception cref="MailThreadNotFoundException">The thread does not exist in the project.</exception>
    /// <exception cref="MailValidationException">The command violates a mail invariant.</exception>
    /// <exception cref="MailSessionNotFoundException">The sender or a derived recipient is not an active session of the project and request.</exception>
    Task<AgentMessageDto> ReplyAsync(ReplyAgentMessageCommand command, CancellationToken cancellationToken = default);

    /// <summary>Lists the session's messages that are not yet read by that session, oldest first.</summary>
    Task<IReadOnlyList<AgentMessageDto>> GetUnreadAsync(
        PiCommandCenter.Domain.ProjectId projectId,
        string sessionId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists a thread's messages in creation order.</summary>
    Task<IReadOnlyList<AgentMessageDto>> GetThreadAsync(
        PiCommandCenter.Domain.ProjectId projectId,
        string threadId,
        CancellationToken cancellationToken = default);

    /// <summary>Marks the message read by one of its recipients. Idempotent.</summary>
    /// <exception cref="MailMessageNotFoundException">No such message.</exception>
    /// <exception cref="MailNotAddresseeException">The session is not an addressee.</exception>
    Task<AgentMessageDto> MarkReadAsync(string messageId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges the message as one of its recipients. Requires a prior mark-read.</summary>
    /// <exception cref="MailMessageNotFoundException">No such message.</exception>
    /// <exception cref="MailNotAddresseeException">The session is not an addressee.</exception>
    /// <exception cref="MailAcknowledgementRequiresReadException">The message has not been read yet.</exception>
    Task<AgentMessageDto> AcknowledgeAsync(string messageId, string sessionId, CancellationToken cancellationToken = default);
}
