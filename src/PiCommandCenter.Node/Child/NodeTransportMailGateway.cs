using PiCommandCenter.Application.Mail;
using PiCommandCenter.Domain;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Production <see cref="INodeMailGateway"/>: delegates to the Control Plane node hub via the
/// <see cref="NodeTransportClient"/> mail wrappers (SendMail, FetchInbox, FetchThread,
/// MarkMailRead, AcknowledgeMail).
/// </summary>
public sealed class NodeTransportMailGateway : INodeMailGateway, IAgentIdentityRegistry
{
    internal const int CredentialCorrelationCapacity = 1024;

    private readonly INodeMailTransport _transport;
    private readonly INodeAssignmentCredentialSource _credentials;
    private readonly BoundedCredentialCache _sessionCredentials = new(CredentialCorrelationCapacity);
    private readonly BoundedCredentialCache _messageCredentials = new(CredentialCorrelationCapacity);

    public NodeTransportMailGateway(
        NodeTransportClient transport,
        INodeAssignmentCredentialSource credentials)
        : this(new NodeMailTransport(transport), credentials)
    {
    }

    internal NodeTransportMailGateway(
        INodeMailTransport transport,
        INodeAssignmentCredentialSource credentials)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    }

    public async Task<AgentIdentityDto> AllocateAsync(
        AllocateAgentIdentityCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var credential = ResolveProject(command.ProjectId.Value);
        var identity = await _transport.AllocateAgentIdentityAsync(
            new AllocateAgentIdentityMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                command.SessionId,
                command.RequestedName,
                command.Role,
                command.Runtime),
            cancellationToken).ConfigureAwait(false);

        _sessionCredentials.Track(identity.SessionId, credential);
        return new AgentIdentityDto(
            new ProjectId(identity.ProjectId),
            identity.SessionId,
            identity.AgentName,
            identity.Role,
            identity.Runtime,
            identity.AllocatedAtUtc);
    }

    public async Task ReleaseAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var credential = ResolveSession(sessionId);
        await _transport.ReleaseAgentIdentityAsync(
            new ReleaseAgentIdentityMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                sessionId),
            cancellationToken).ConfigureAwait(false);
        _sessionCredentials.Remove(sessionId, credential);
    }

    public async Task<AgentIdentityDto?> FindByNameAsync(
        ProjectId projectId,
        string agentName,
        CancellationToken cancellationToken = default)
    {
        var credential = ResolveProject(projectId.Value);
        var identity = await _transport.FindAgentIdentityAsync(
            new FindAgentIdentityMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                agentName),
            cancellationToken).ConfigureAwait(false);
        return identity is null
            ? null
            : new AgentIdentityDto(
                new ProjectId(identity.ProjectId),
                identity.SessionId,
                identity.AgentName,
                identity.Role,
                identity.Runtime,
                identity.AllocatedAtUtc);
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
        var credential = ResolveRequest(requestId);
        RequireProject(projectId, credential);
        var delivery = await _transport.SendMailAsync(
            new SendMailMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                threadId ?? $"mail-{Guid.NewGuid():N}",
                senderSessionId,
                recipients,
                subject,
                bodyMarkdown,
                importance,
                ackRequired),
            cancellationToken).ConfigureAwait(false);

        _sessionCredentials.Track(senderSessionId, credential);
        _messageCredentials.Track(delivery.MessageId, credential);
        return new MailDeliveryResult(delivery.MessageId, delivery.ThreadId, delivery.DeliveredTo);
    }

    public async Task<MailInboxResult> FetchInboxAsync(
        Guid projectId,
        string recipientSessionId,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var credential = ResolveProject(projectId);
        var inbox = await _transport.FetchInboxAsync(
            new FetchMailInboxMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                recipientSessionId,
                maxCount),
            cancellationToken).ConfigureAwait(false);
        TrackFetchedMail(recipientSessionId, inbox, credential);
        return ToResult(inbox);
    }

    public async Task<MailInboxResult> FetchThreadAsync(
        Guid projectId,
        string recipientSessionId,
        string threadId,
        CancellationToken cancellationToken)
    {
        var credential = ResolveProject(projectId);
        var inbox = await _transport.FetchThreadAsync(
            new FetchMailThreadMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                recipientSessionId,
                threadId),
            cancellationToken).ConfigureAwait(false);
        TrackFetchedMail(recipientSessionId, inbox, credential);
        return ToResult(inbox);
    }

    public async Task<MailReceiptResult> MarkReadAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var credential = ResolveMailReceipt(recipientSessionId, messageId);
        var receipt = await _transport.MarkMailReadAsync(
            new MarkMailReadMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                recipientSessionId,
                messageId),
            cancellationToken).ConfigureAwait(false);
        return new MailReceiptResult(receipt.MessageId, receipt.RecipientSessionId);
    }

    public async Task<MailReceiptResult> AcknowledgeAsync(
        string recipientSessionId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var credential = ResolveMailReceipt(recipientSessionId, messageId);
        var receipt = await _transport.AcknowledgeMailAsync(
            new AcknowledgeMailMessage(
                credential.ProjectId,
                credential.RequestId,
                credential.ClaimToken,
                recipientSessionId,
                messageId),
            cancellationToken).ConfigureAwait(false);
        return new MailReceiptResult(receipt.MessageId, receipt.RecipientSessionId);
    }

    private NodeAssignmentCredential ResolveRequest(Guid requestId)
        => _credentials.TryGetByRequest(requestId, out var credential)
            ? credential
            : throw new InvalidOperationException(
                $"No active assignment credential exists for request {requestId}.");

    private NodeAssignmentCredential ResolveProject(Guid projectId)
        => _credentials.TryGetByProject(projectId, out var credential)
            ? credential
            : throw new InvalidOperationException(
                $"No active assignment credential exists for project {projectId}.");

    private NodeAssignmentCredential ResolveSession(string sessionId)
        => _sessionCredentials.TryGet(sessionId, out var credential)
            ? credential
            : throw new InvalidOperationException(
                $"No authenticated assignment context exists for session '{sessionId}'.");

    private NodeAssignmentCredential ResolveMailReceipt(string sessionId, string messageId)
    {
        var sessionCredential = ResolveSession(sessionId);
        if (!_messageCredentials.TryGet(messageId, out var messageCredential))
        {
            throw new InvalidOperationException(
                $"No authenticated assignment context exists for message '{messageId}'.");
        }

        if (sessionCredential != messageCredential)
        {
            throw new InvalidOperationException(
                "The session and message belong to different assignment contexts.");
        }

        return sessionCredential;
    }

    private static void RequireProject(Guid projectId, NodeAssignmentCredential credential)
    {
        if (projectId != credential.ProjectId)
        {
            throw new InvalidOperationException(
                "The request credential does not belong to the supplied project.");
        }
    }

    private void TrackFetchedMail(
        string recipientSessionId,
        MailInboxMessage inbox,
        NodeAssignmentCredential credential)
    {
        _sessionCredentials.Track(recipientSessionId, credential);
        foreach (var message in inbox.Messages)
        {
            _messageCredentials.Track(message.MessageId, credential);
        }
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

    private sealed class NodeMailTransport(NodeTransportClient client) : INodeMailTransport
    {
        private readonly NodeTransportClient _client =
            client ?? throw new ArgumentNullException(nameof(client));

        public Task<AgentIdentityMessage> AllocateAgentIdentityAsync(
            AllocateAgentIdentityMessage message,
            CancellationToken cancellationToken)
            => _client.AllocateAgentIdentityAsync(message, cancellationToken);

        public Task ReleaseAgentIdentityAsync(
            ReleaseAgentIdentityMessage message,
            CancellationToken cancellationToken)
            => _client.ReleaseAgentIdentityAsync(message, cancellationToken);

        public Task<AgentIdentityMessage?> FindAgentIdentityAsync(
            FindAgentIdentityMessage message,
            CancellationToken cancellationToken)
            => _client.FindAgentIdentityAsync(message, cancellationToken);

        public Task<MailDeliveryMessage> SendMailAsync(
            SendMailMessage message,
            CancellationToken cancellationToken)
            => _client.SendMailAsync(message, cancellationToken);

        public Task<MailInboxMessage> FetchInboxAsync(
            FetchMailInboxMessage message,
            CancellationToken cancellationToken)
            => _client.FetchInboxAsync(message, cancellationToken);

        public Task<MailInboxMessage> FetchThreadAsync(
            FetchMailThreadMessage message,
            CancellationToken cancellationToken)
            => _client.FetchThreadAsync(message, cancellationToken);

        public Task<MailReceiptMessage> MarkMailReadAsync(
            MarkMailReadMessage message,
            CancellationToken cancellationToken)
            => _client.MarkMailReadAsync(message, cancellationToken);

        public Task<MailReceiptMessage> AcknowledgeMailAsync(
            AcknowledgeMailMessage message,
            CancellationToken cancellationToken)
            => _client.AcknowledgeMailAsync(message, cancellationToken);
    }

    private sealed class BoundedCredentialCache(int capacity)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<string> _order = new();

        public void Track(string key, NodeAssignmentCredential credential)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var current))
                {
                    current.Credential = credential;
                    _order.Remove(current.Node);
                    _order.AddLast(current.Node);
                    return;
                }

                if (_entries.Count == capacity)
                {
                    var oldest = _order.First!;
                    _order.RemoveFirst();
                    _entries.Remove(oldest.Value);
                }

                var node = _order.AddLast(key);
                _entries.Add(key, new Entry(credential, node));
            }
        }

        public bool TryGet(
            string key,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NodeAssignmentCredential? credential)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out var entry))
                {
                    credential = entry.Credential;
                    return true;
                }

                credential = null;
                return false;
            }
        }

        public void Remove(string key, NodeAssignmentCredential credential)
        {
            lock (_gate)
            {
                if (!_entries.TryGetValue(key, out var entry)
                    || entry.Credential != credential)
                {
                    return;
                }

                _entries.Remove(key);
                _order.Remove(entry.Node);
            }
        }

        private sealed class Entry(
            NodeAssignmentCredential credential,
            LinkedListNode<string> node)
        {
            public NodeAssignmentCredential Credential { get; set; } = credential;

            public LinkedListNode<string> Node { get; } = node;
        }
    }
}

internal interface INodeMailTransport
{
    Task<AgentIdentityMessage> AllocateAgentIdentityAsync(
        AllocateAgentIdentityMessage message,
        CancellationToken cancellationToken);

    Task ReleaseAgentIdentityAsync(
        ReleaseAgentIdentityMessage message,
        CancellationToken cancellationToken);

    Task<AgentIdentityMessage?> FindAgentIdentityAsync(
        FindAgentIdentityMessage message,
        CancellationToken cancellationToken);

    Task<MailDeliveryMessage> SendMailAsync(
        SendMailMessage message,
        CancellationToken cancellationToken);

    Task<MailInboxMessage> FetchInboxAsync(
        FetchMailInboxMessage message,
        CancellationToken cancellationToken);

    Task<MailInboxMessage> FetchThreadAsync(
        FetchMailThreadMessage message,
        CancellationToken cancellationToken);

    Task<MailReceiptMessage> MarkMailReadAsync(
        MarkMailReadMessage message,
        CancellationToken cancellationToken);

    Task<MailReceiptMessage> AcknowledgeMailAsync(
        AcknowledgeMailMessage message,
        CancellationToken cancellationToken);
}
