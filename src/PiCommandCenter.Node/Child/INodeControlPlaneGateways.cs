using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>One requested or granted scope: <c>file</c>, <c>directory</c>, or <c>resource</c>.</summary>
public sealed record ReservationScopeSpec(string Kind, string Path);

/// <summary>Node-side projection of a granted lease.</summary>
public sealed record ReservationLeaseInfo(
    Guid LeaseId,
    long FencingToken,
    string State,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<ReservationScopeSpec> Scopes);

/// <summary>Structured authority error (conflict, not_found, invalid_fencing_token, …).</summary>
public sealed record GatewayError(string Code, string Message);

/// <summary>Result envelope for reservation lifecycle operations.</summary>
public sealed record ReservationOperationResult(
    ReservationLeaseInfo? Lease,
    GatewayError? Error)
{
    public bool Ok => Lease is not null;
}

/// <summary>Decision of the reservation authority for one mutation.</summary>
public sealed record MutationAuthorizationResult(bool Authorized, GatewayError? Error);

/// <summary>Delivery receipt for one sent mail message.</summary>
public sealed record MailDeliveryResult(
    string MessageId,
    string ThreadId,
    IReadOnlyList<string> Recipients);

/// <summary>One inbox message as delivered to the node.</summary>
public sealed record MailInboxEntry(
    string MessageId,
    string SenderSessionId,
    string ThreadId,
    string Subject,
    string BodyMarkdown,
    string Importance,
    bool AckRequired,
    DateTimeOffset SentAt);

/// <summary>Inbox or thread fetch result.</summary>
public sealed record MailInboxResult(IReadOnlyList<MailInboxEntry> Messages);

/// <summary>Receipt for a mark-read or acknowledge operation.</summary>
public sealed record MailReceiptResult(string MessageId, string RecipientSessionId);

/// <summary>
/// Reservation-authority seam used by the child supervisor. The production adapter delegates
/// to the Control Plane node hub through <see cref="NodeTransportClient"/>; tests substitute
/// fakes. Implementations never touch the filesystem.
/// </summary>
public interface INodeReservationGateway
{
    Task<ReservationOperationResult> AcquireAsync(
        Guid projectId,
        Guid requestId,
        string ownerSessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        string reason,
        CancellationToken cancellationToken);

    Task<ReservationOperationResult> ExpandAsync(
        Guid leaseId,
        Guid projectId,
        long fencingToken,
        string sessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        CancellationToken cancellationToken);

    Task<ReservationOperationResult> ReleaseAsync(
        Guid leaseId,
        Guid projectId,
        string sessionId,
        CancellationToken cancellationToken);

    Task<ReservationOperationResult> TransferAsync(
        Guid leaseId,
        string fromSessionId,
        string toSessionId,
        CancellationToken cancellationToken);

    Task<MutationAuthorizationResult> AuthorizeAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        string targetPath,
        string operation,
        CancellationToken cancellationToken);
}

/// <summary>
/// Agent Mail seam used by the child supervisor; the production adapter delegates to the
/// Control Plane node hub through <see cref="NodeTransportClient"/>.
/// </summary>
public interface INodeMailGateway
{
    Task<MailDeliveryResult> SendAsync(
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
        CancellationToken cancellationToken);

    Task<MailInboxResult> FetchInboxAsync(
        Guid projectId,
        string recipientSessionId,
        int maxCount,
        CancellationToken cancellationToken);

    Task<MailInboxResult> FetchThreadAsync(
        Guid projectId,
        string recipientSessionId,
        string threadId,
        CancellationToken cancellationToken);

    Task<MailReceiptResult> MarkReadAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken);

    Task<MailReceiptResult> AcknowledgeAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken);
}
