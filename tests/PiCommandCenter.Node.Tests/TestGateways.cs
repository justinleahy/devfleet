using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// In-memory reservation gateway fake: records acquire/authorize calls, grants leases on
/// demand, and can deny or stale-token any single authorization for denial tests.
/// </summary>
public sealed class FakeReservationGateway : INodeReservationGateway
{
    public List<(Guid ProjectId, Guid RequestId, string OwnerSessionId, IReadOnlyList<ReservationScopeSpec> Scopes)> Acquires { get; } = [];
    public List<(string Path, string Operation)> Authorizations { get; } = [];
    public List<Guid> Releases { get; } = [];
    public List<(Guid LeaseId, string Reason)> Recoveries { get; } = [];
    public GatewayError? AcquireError { get; set; }
    public (Guid ProjectId, Guid RequestId, string OwnerSessionId, IReadOnlyList<ReservationScopeSpec> Scopes)? LastAcquire { get; private set; }

    /// <summary>When set, overrides the authorization decision for matching (path, operation).</summary>
    public Func<string, string, MutationAuthorizationResult?>? OnAuthorize { get; set; }

    public ReservationLeaseInfo GrantLease()
    {
        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(), 42, "Active", DateTimeOffset.UtcNow.AddMinutes(2), []);
        _granted[lease.LeaseId] = lease;
        return lease;
    }

    public void Seed(ReservationLeaseInfo lease) => _granted[lease.LeaseId] = lease;

    private readonly Dictionary<Guid, ReservationLeaseInfo> _granted = new();

    public Task<ReservationOperationResult> AcquireAsync(
        Guid projectId,
        Guid requestId,
        string ownerSessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        string reason,
        CancellationToken cancellationToken)
    {
        var acquire = (projectId, requestId, ownerSessionId, scopes);
        Acquires.Add(acquire);
        LastAcquire = acquire;
        if (AcquireError is not null)
        {
            return Task.FromResult(new ReservationOperationResult(null, AcquireError));
        }

        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(), 7, "Active", DateTimeOffset.UtcNow.AddMinutes(2), scopes, ownerSessionId);
        _granted[lease.LeaseId] = lease;
        return Task.FromResult(new ReservationOperationResult(lease, null));
    }

    public Task<ReservationOperationResult> ExpandAsync(
        Guid leaseId,
        Guid projectId,
        long fencingToken,
        string sessionId,
        IReadOnlyList<ReservationScopeSpec> scopes,
        CancellationToken cancellationToken)
    {
        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
        }

        var expanded = lease with { Scopes = [.. lease.Scopes, .. scopes] };
        _granted[leaseId] = expanded;
        return Task.FromResult(new ReservationOperationResult(expanded, null));
    }

    public Task<ReservationOperationResult> ReleaseAsync(
        Guid leaseId,
        Guid projectId,
        string sessionId,
        CancellationToken cancellationToken)
    {
        Releases.Add(leaseId);
        return Task.FromResult(_granted.Remove(leaseId)
            ? new ReservationOperationResult(
                new ReservationLeaseInfo(leaseId, 0, "Released", DateTimeOffset.UtcNow, []),
                null)
            : new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
    }

    public Task<ReservationOperationResult> TransferAsync(
        Guid leaseId,
        string fromSessionId,
        string toSessionId,
        CancellationToken cancellationToken)
        => Task.FromResult(_granted.TryGetValue(leaseId, out var lease)
            ? new ReservationOperationResult(lease, null)
            : new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));

    public Task<MutationAuthorizationResult> AuthorizeAsync(
        Guid leaseId,
        long fencingToken,
        string sessionId,
        string targetPath,
        string operation,
        CancellationToken cancellationToken)
    {
        Authorizations.Add((targetPath, operation));
        var overridden = OnAuthorize?.Invoke(targetPath, operation);
        if (overridden is not null)
        {
            return Task.FromResult(overridden);
        }

        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new MutationAuthorizationResult(
                false, new GatewayError("not_found", "No such lease.")));
        }

        if (lease.FencingToken != fencingToken)
        {
            return Task.FromResult(new MutationAuthorizationResult(
                false, new GatewayError("invalid_fencing_token", "The fencing token is stale.")));
        }

        return Task.FromResult(new MutationAuthorizationResult(true, null));
    }

    public Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
        Guid projectId,
        bool includeReleased,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ReservationLeaseInfo>>([.. _granted.Values]);

    public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
        Guid leaseId,
        string reason,
        CancellationToken cancellationToken)
    {
        Recoveries.Add((leaseId, reason));
        if (!_granted.TryGetValue(leaseId, out var lease))
        {
            return Task.FromResult(new ReservationOperationResult(
                null, new GatewayError("not_found", "No such lease.")));
        }

        var marked = lease with { State = "RecoveryRequired" };
        _granted[leaseId] = marked;
        return Task.FromResult(new ReservationOperationResult(marked, null));
    }
}

/// <summary>In-memory mail gateway fake recording sends and acknowledging per session.</summary>
public sealed class FakeMailGateway : INodeMailGateway
{
    public List<(string SenderSessionId, IReadOnlyList<string> Recipients, string Subject)> Sends { get; } = [];

    public Task<MailDeliveryResult> SendAsync(
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
        Sends.Add((senderSessionId, recipients, subject));
        return Task.FromResult(new MailDeliveryResult(
            Guid.NewGuid().ToString("N"), threadId ?? "thread-1", recipients));
    }

    public Task<MailInboxResult> FetchInboxAsync(
        Guid projectId,
        string recipientSessionId,
        int maxCount,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailInboxResult([]));

    public Task<MailInboxResult> FetchThreadAsync(
        Guid projectId,
        string recipientSessionId,
        string threadId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailInboxResult([]));

    public Task<MailReceiptResult> MarkReadAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailReceiptResult(messageId, recipientSessionId));

    public Task<MailReceiptResult> AcknowledgeAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
        => Task.FromResult(new MailReceiptResult(messageId, recipientSessionId));
}
