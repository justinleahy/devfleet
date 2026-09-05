namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>
/// Transport mirror of the application message importance (SPEC §16.4). Kept as
/// string values so the node protocol stays decoupled from application enums.
/// </summary>
public static class MailImportance
{
    public const string Normal = "normal";
    public const string High = "high";
}

/// <summary>
/// Transport message: a node (on behalf of one of its agent sessions) sends a mail message.
/// Mirrors the application <c>SendAgentMessageCommand</c>; the hub clamps and validates before
/// the message reaches the coordination service. <c>ThreadId</c> is the request thread id.
/// </summary>
public sealed record SendMailMessage(
    Guid ProjectId,
    Guid RequestId,
    string ThreadId,
    string? SenderSessionId,
    IReadOnlyList<string> Recipients,
    string Subject,
    string BodyMarkdown,
    string Importance,
    bool AckRequired);

/// <summary>
/// Transport message: one delivered mail message as stored by the control plane, addressed to a
/// single recipient (SPEC §16.4).

/// <summary>
/// Transport message: a node replies in an existing thread on behalf of one of its agent
/// sessions. Mirrors the application <c>ReplyAgentMessageCommand</c>; recipients are derived
/// from the thread's participants, excluding the replying session.
/// </summary>
public sealed record ReplyMailMessage(
    Guid ProjectId,
    string ThreadId,
    string SenderSessionId,
    string BodyMarkdown,
    string Importance,
    bool AckRequired);
public sealed record AgentMailMessage(
    string MessageId,
    Guid ProjectId,
    Guid RequestId,
    string ThreadId,
    string? SenderSessionId,
    bool IsFromHuman,
    string RecipientSessionId,
    string Subject,
    string BodyMarkdown,
    string Importance,
    bool AcknowledgementRequired,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc,
    DateTimeOffset? AcknowledgedAtUtc);

/// <summary>Transport response: messages for one recipient session (inbox or thread).</summary>
public sealed record MailInboxMessage(IReadOnlyList<AgentMailMessage> Messages);

/// <summary>Transport request: fetch the unread inbox for one recipient session.</summary>
public sealed record FetchMailInboxMessage(Guid ProjectId, string RecipientSessionId, int MaxCount);

/// <summary>Transport request: fetch one thread for one recipient session.</summary>
public sealed record FetchMailThreadMessage(Guid ProjectId, string RecipientSessionId, string ThreadId);

/// <summary>Transport request: mark one delivered message read for one recipient session.</summary>
public sealed record MarkMailReadMessage(string RecipientSessionId, string MessageId);

/// <summary>Transport request: acknowledge one delivered message for one recipient session.</summary>
public sealed record AcknowledgeMailMessage(string RecipientSessionId, string MessageId);

/// <summary>Transport response: post-condition state after a read/ack mutation.</summary>
public sealed record MailReceiptMessage(string MessageId, string RecipientSessionId, DateTimeOffset? ReadAtUtc, DateTimeOffset? AcknowledgedAtUtc);

/// <summary>Transport response: send outcome with the created message id and thread.</summary>
public sealed record MailDeliveryMessage(string MessageId, string ThreadId, IReadOnlyList<string> DeliveredTo);
