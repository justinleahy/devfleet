using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Production <see cref="INodeMailGateway"/>: delegates to the Control Plane node hub via the
/// <see cref="NodeTransportClient"/> mail wrappers (SendMail, FetchInbox, FetchThread,
/// MarkMailRead, AcknowledgeMail).
/// </summary>
public sealed class NodeTransportMailGateway : INodeMailGateway
{
    private readonly NodeTransportClient _transport;

    public NodeTransportMailGateway(NodeTransportClient transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    public async Task<MailDeliveryResult> SendAsync(
        Guid projectId,
        Guid requestId,
        string? threadId,
        string senderSessionId,
        IReadOnlyList<string> recipients,
        string subject,
        string bodyMarkdown,
        string importance,
        bool ackRequired,
        string? inReplyToMessageId,
        CancellationToken cancellationToken)
    {
        var delivery = await _transport.SendMailAsync(new SendMailMessage(
            projectId,
            requestId,
            threadId ?? $"mail-{Guid.NewGuid():N}",
            senderSessionId,
            recipients,
            subject,
            bodyMarkdown,
            importance,
            ackRequired), cancellationToken).ConfigureAwait(false);
        return new MailDeliveryResult(delivery.MessageId, delivery.ThreadId, delivery.DeliveredTo);
    }

    public async Task<MailInboxResult> FetchInboxAsync(
        Guid projectId,
        string recipientSessionId,
        int maxCount,
        CancellationToken cancellationToken)
        => ToResult(await _transport.FetchInboxAsync(
            new FetchMailInboxMessage(projectId, recipientSessionId, maxCount),
            cancellationToken).ConfigureAwait(false));

    public async Task<MailInboxResult> FetchThreadAsync(
        Guid projectId,
        string recipientSessionId,
        string threadId,
        CancellationToken cancellationToken)
        => ToResult(await _transport.FetchThreadAsync(
            new FetchMailThreadMessage(projectId, recipientSessionId, threadId),
            cancellationToken).ConfigureAwait(false));

    public async Task<MailReceiptResult> MarkReadAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var receipt = await _transport.MarkMailReadAsync(
            new MarkMailReadMessage(recipientSessionId, messageId), cancellationToken)
            .ConfigureAwait(false);
        return new MailReceiptResult(receipt.MessageId, receipt.RecipientSessionId);
    }

    public async Task<MailReceiptResult> AcknowledgeAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var receipt = await _transport.AcknowledgeMailAsync(
            new AcknowledgeMailMessage(recipientSessionId, messageId), cancellationToken)
            .ConfigureAwait(false);
        return new MailReceiptResult(receipt.MessageId, receipt.RecipientSessionId);
    }

    private static MailInboxResult ToResult(MailInboxMessage inbox)
        => new([.. inbox.Messages.Select(m => new MailInboxEntry(
            m.MessageId,
            m.SenderSessionId ?? string.Empty,
            m.ThreadId,
            m.Subject,
            m.BodyMarkdown,
            m.Importance,
            m.AcknowledgementRequired,
            m.CreatedAtUtc))]);
}
