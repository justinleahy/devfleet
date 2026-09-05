namespace PiCommandCenter.Application.Mail;

/// <summary>Base type for mail and identity allocation errors surfaced through the application layer.</summary>
public abstract class MailException(string message) : Exception(message);

/// <summary>A command violated a mail invariant (empty fields, duplicate recipients, wrong project).</summary>
public sealed class MailValidationException(string message) : MailException(message);

/// <summary>No message thread with the given id exists in the project.</summary>
public sealed class MailThreadNotFoundException(string threadId) : MailException($"No message thread '{threadId}' exists.");

/// <summary>No message with the given id exists.</summary>
public sealed class MailMessageNotFoundException(string messageId) : MailException($"No message '{messageId}' exists.");

/// <summary>The referenced session does not exist, or belongs to a different project or request.</summary>
public sealed class MailSessionNotFoundException(string sessionId) : MailException($"Session '{sessionId}' is not an active session of the target project and request.");

/// <summary>The acting session is not an addressee of the message.</summary>
public sealed class MailNotAddresseeException(string sessionId, string messageId) : MailException($"Session '{sessionId}' is not an addressee of message '{messageId}'.");

/// <summary>Acknowledgement was attempted before the message was marked read.</summary>
public sealed class MailAcknowledgementRequiresReadException(string messageId, string sessionId) : MailException($"Message '{messageId}' must be marked read by session '{sessionId}' before it can be acknowledged.");
