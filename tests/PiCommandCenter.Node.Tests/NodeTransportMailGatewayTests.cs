using System.Diagnostics.CodeAnalysis;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Node.Child;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeTransportMailGatewayTests
{
    [Fact]
    public async Task Identity_and_project_scoped_mail_calls_propagate_the_project_credential()
    {
        var credential = CreateCredential();
        var transport = new RecordingMailTransport();
        var gateway = CreateGateway(transport, credential);
        const string allocatedSessionId = "session-allocated";

        await gateway.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(credential.ProjectId),
            allocatedSessionId,
            "reviewer",
            "worker",
            "pi"));
        await gateway.FindByNameAsync(new ProjectId(credential.ProjectId), "reviewer");
        await gateway.FetchInboxAsync(
            credential.ProjectId,
            "session-recipient",
            20,
            CancellationToken.None);
        await gateway.FetchThreadAsync(
            credential.ProjectId,
            "session-recipient",
            "thread-1",
            CancellationToken.None);
        await gateway.ReleaseAsync(allocatedSessionId);

        AssertCredential(credential, transport.AllocateMessage!);
        AssertCredential(credential, transport.FindMessage!);
        AssertCredential(credential, transport.FetchInboxMessage!);
        AssertCredential(credential, transport.FetchThreadMessage!);
        AssertCredential(credential, transport.ReleaseMessage!);
    }

    [Fact]
    public async Task Request_and_project_operations_use_their_respective_credential_lookup()
    {
        var projectId = Guid.NewGuid();
        var requestCredential = new NodeAssignmentCredential(
            Guid.NewGuid(),
            projectId,
            "request-claim");
        var projectCredential = new NodeAssignmentCredential(
            Guid.NewGuid(),
            projectId,
            "project-claim");
        var transport = new RecordingMailTransport();
        var gateway = new NodeTransportMailGateway(
            transport,
            new SplitCredentialSource(requestCredential, projectCredential));

        await SendAsync(gateway, requestCredential, "sender");
        await gateway.FindByNameAsync(new ProjectId(projectId), "reviewer");

        AssertCredential(requestCredential, transport.SendMessage!);
        AssertCredential(projectCredential, transport.FindMessage!);
    }

    [Fact]
    public async Task Send_and_fetch_correlations_propagate_credentials_to_receipts()
    {
        var credential = CreateCredential();
        var transport = new RecordingMailTransport
        {
            SendResult = new MailDeliveryMessage("sent-message", "thread-1", ["recipient"]),
            FetchInboxResult = new MailInboxMessage([
                CreateMailMessage(credential, "fetched-message", "recipient"),
            ]),
        };
        var gateway = CreateGateway(transport, credential);

        await gateway.SendAsync(
            credential.ProjectId,
            credential.RequestId,
            "thread-1",
            "sender",
            ["recipient"],
            "Subject",
            "Body",
            "normal",
            ackRequired: true,
            inReplyToMessageId: null,
            CancellationToken.None);
        await gateway.MarkReadAsync("sender", "sent-message", CancellationToken.None);
        await gateway.FetchInboxAsync(
            credential.ProjectId,
            "recipient",
            20,
            CancellationToken.None);
        await gateway.AcknowledgeAsync("recipient", "fetched-message", CancellationToken.None);

        AssertCredential(credential, transport.SendMessage!);
        AssertCredential(credential, transport.MarkReadMessage!);
        AssertCredential(credential, transport.FetchInboxMessage!);
        AssertCredential(credential, transport.AcknowledgeMessage!);
    }

    [Fact]
    public async Task Missing_assignment_credentials_fail_before_transport()
    {
        var transport = new RecordingMailTransport();
        var gateway = new NodeTransportMailGateway(
            transport,
            new NodeAssignmentCredentialSource());
        var projectId = Guid.NewGuid();
        var requestId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AllocateAsync(
            new AllocateAgentIdentityCommand(
                new ProjectId(projectId),
                "session",
                "reviewer",
                "worker",
                "pi")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.FindByNameAsync(
            new ProjectId(projectId),
            "reviewer"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.SendAsync(
            projectId,
            requestId,
            null,
            "sender",
            ["recipient"],
            "Subject",
            "Body",
            "normal",
            ackRequired: false,
            inReplyToMessageId: null,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.FetchInboxAsync(
            projectId,
            "recipient",
            20,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.FetchThreadAsync(
            projectId,
            "recipient",
            "thread-1",
            CancellationToken.None));

        Assert.Equal(0, transport.InvocationCount);
    }

    [Fact]
    public async Task Missing_and_foreign_session_message_contexts_fail_before_transport()
    {
        var first = CreateCredential();
        var second = CreateCredential();
        var source = new NodeAssignmentCredentialSource();
        source.Track(first);
        source.Track(second);
        var transport = new RecordingMailTransport
        {
            SendResultFactory = message => new MailDeliveryMessage(
                $"message-{message.RequestId:N}",
                message.ThreadId,
                message.Recipients),
        };
        var gateway = new NodeTransportMailGateway(transport, source);

        await SendAsync(gateway, first, "first-session");
        await SendAsync(gateway, second, "second-session");
        var firstMessageId = $"message-{first.RequestId:N}";
        var secondMessageId = $"message-{second.RequestId:N}";
        var invocationsBeforeReceipts = transport.InvocationCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReleaseAsync(
            "missing-session"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.MarkReadAsync(
            "missing-session",
            firstMessageId,
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.MarkReadAsync(
            "first-session",
            "missing-message",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcknowledgeAsync(
            "first-session",
            secondMessageId,
            CancellationToken.None));

        Assert.Equal(invocationsBeforeReceipts, transport.InvocationCount);
    }

    [Fact]
    public async Task Failed_authenticated_calls_do_not_create_correlations()
    {
        var credential = CreateCredential();
        var transport = new RecordingMailTransport
        {
            AllocateException = new InvalidOperationException("allocation failed"),
            SendException = new InvalidOperationException("send failed"),
            FetchInboxException = new InvalidOperationException("fetch failed"),
        };
        var gateway = CreateGateway(transport, credential);

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AllocateAsync(
            new AllocateAgentIdentityCommand(
                new ProjectId(credential.ProjectId),
                "allocated-session",
                "reviewer",
                "worker",
                "pi")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(
            gateway,
            credential,
            "sender"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.FetchInboxAsync(
            credential.ProjectId,
            "recipient",
            20,
            CancellationToken.None));
        var invocationsBeforeCorrelationChecks = transport.InvocationCount;

        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReleaseAsync(
            "allocated-session"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.MarkReadAsync(
            "sender",
            "sent-message",
            CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AcknowledgeAsync(
            "recipient",
            "fetched-message",
            CancellationToken.None));

        Assert.Equal(invocationsBeforeCorrelationChecks, transport.InvocationCount);
    }

    [Fact]
    public async Task Credential_correlations_evict_the_oldest_entries_at_capacity()
    {
        var credential = CreateCredential();
        var transport = new RecordingMailTransport
        {
            SendResultFactory = message => new MailDeliveryMessage(
                $"message-{message.SenderSessionId}",
                message.ThreadId,
                message.Recipients),
        };
        var gateway = CreateGateway(transport, credential);

        for (var index = 0; index <= NodeTransportMailGateway.CredentialCorrelationCapacity; index++)
        {
            await SendAsync(gateway, credential, $"session-{index}");
        }

        var invocationsBeforeChecks = transport.InvocationCount;
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.ReleaseAsync("session-0"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.MarkReadAsync(
            $"session-{NodeTransportMailGateway.CredentialCorrelationCapacity}",
            "message-session-0",
            CancellationToken.None));
        Assert.Equal(invocationsBeforeChecks, transport.InvocationCount);

        await gateway.ReleaseAsync(
            $"session-{NodeTransportMailGateway.CredentialCorrelationCapacity}");
        Assert.Equal(invocationsBeforeChecks + 1, transport.InvocationCount);
    }

    private static NodeTransportMailGateway CreateGateway(
        RecordingMailTransport transport,
        NodeAssignmentCredential credential)
    {
        var source = new NodeAssignmentCredentialSource();
        source.Track(credential);
        return new NodeTransportMailGateway(transport, source);
    }

    private static NodeAssignmentCredential CreateCredential()
        => new(Guid.NewGuid(), Guid.NewGuid(), $"claim-{Guid.NewGuid():N}");

    private static AgentMailMessage CreateMailMessage(
        NodeAssignmentCredential credential,
        string messageId,
        string recipientSessionId)
        => new(
            messageId,
            credential.ProjectId,
            credential.RequestId,
            "thread-1",
            "sender",
            IsFromHuman: false,
            recipientSessionId,
            "Subject",
            "Body",
            "normal",
            AcknowledgementRequired: true,
            DateTimeOffset.UtcNow,
            ReadAtUtc: null,
            AcknowledgedAtUtc: null);

    private static Task<MailDeliveryResult> SendAsync(
        NodeTransportMailGateway gateway,
        NodeAssignmentCredential credential,
        string senderSessionId)
        => gateway.SendAsync(
            credential.ProjectId,
            credential.RequestId,
            "thread-1",
            senderSessionId,
            ["recipient"],
            "Subject",
            "Body",
            "normal",
            ackRequired: false,
            inReplyToMessageId: null,
            CancellationToken.None);

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        AllocateAgentIdentityMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        FindAgentIdentityMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        ReleaseAgentIdentityMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        SendMailMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        FetchMailInboxMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        FetchMailThreadMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        MarkMailReadMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private static void AssertCredential(
        NodeAssignmentCredential credential,
        AcknowledgeMailMessage message)
    {
        Assert.Equal(credential.ProjectId, message.ProjectId);
        Assert.Equal(credential.RequestId, message.RequestId);
        Assert.Equal(credential.ClaimToken, message.ClaimToken);
    }

    private sealed class SplitCredentialSource(
        NodeAssignmentCredential requestCredential,
        NodeAssignmentCredential projectCredential) : INodeAssignmentCredentialSource
    {
        public bool TryGetByRequest(
            Guid requestId,
            [NotNullWhen(true)] out NodeAssignmentCredential? credential)
        {
            credential = requestId == requestCredential.RequestId
                ? requestCredential
                : null;
            return credential is not null;
        }

        public bool TryGetByProject(
            Guid projectId,
            [NotNullWhen(true)] out NodeAssignmentCredential? credential)
        {
            credential = projectId == projectCredential.ProjectId
                ? projectCredential
                : null;
            return credential is not null;
        }
    }

    private sealed class RecordingMailTransport : INodeMailTransport
    {
        public AllocateAgentIdentityMessage? AllocateMessage { get; private set; }
        public ReleaseAgentIdentityMessage? ReleaseMessage { get; private set; }
        public FindAgentIdentityMessage? FindMessage { get; private set; }
        public SendMailMessage? SendMessage { get; private set; }
        public FetchMailInboxMessage? FetchInboxMessage { get; private set; }
        public FetchMailThreadMessage? FetchThreadMessage { get; private set; }
        public MarkMailReadMessage? MarkReadMessage { get; private set; }
        public AcknowledgeMailMessage? AcknowledgeMessage { get; private set; }
        public int InvocationCount { get; private set; }

        public Exception? AllocateException { get; init; }
        public Exception? SendException { get; init; }
        public Exception? FetchInboxException { get; init; }
        public MailDeliveryMessage SendResult { get; init; } =
            new("sent-message", "thread-1", ["recipient"]);
        public Func<SendMailMessage, MailDeliveryMessage>? SendResultFactory { get; init; }
        public MailInboxMessage FetchInboxResult { get; init; } = new([]);

        public Task<AgentIdentityMessage> AllocateAgentIdentityAsync(
            AllocateAgentIdentityMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            AllocateMessage = message;
            return AllocateException is null
                ? Task.FromResult(new AgentIdentityMessage(
                    message.ProjectId,
                    message.SessionId,
                    message.RequestedName,
                    message.Role,
                    message.Runtime,
                    DateTimeOffset.UtcNow))
                : Task.FromException<AgentIdentityMessage>(AllocateException);
        }

        public Task ReleaseAgentIdentityAsync(
            ReleaseAgentIdentityMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            ReleaseMessage = message;
            return Task.CompletedTask;
        }

        public Task<AgentIdentityMessage?> FindAgentIdentityAsync(
            FindAgentIdentityMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            FindMessage = message;
            return Task.FromResult<AgentIdentityMessage?>(null);
        }

        public Task<MailDeliveryMessage> SendMailAsync(
            SendMailMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            SendMessage = message;
            if (SendException is not null)
            {
                return Task.FromException<MailDeliveryMessage>(SendException);
            }

            return Task.FromResult(SendResultFactory?.Invoke(message) ?? SendResult);
        }

        public Task<MailInboxMessage> FetchInboxAsync(
            FetchMailInboxMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            FetchInboxMessage = message;
            return FetchInboxException is null
                ? Task.FromResult(FetchInboxResult)
                : Task.FromException<MailInboxMessage>(FetchInboxException);
        }

        public Task<MailInboxMessage> FetchThreadAsync(
            FetchMailThreadMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            FetchThreadMessage = message;
            return Task.FromResult(new MailInboxMessage([]));
        }

        public Task<MailReceiptMessage> MarkMailReadAsync(
            MarkMailReadMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            MarkReadMessage = message;
            return Task.FromResult(new MailReceiptMessage(
                message.MessageId,
                message.RecipientSessionId,
                DateTimeOffset.UtcNow,
                null));
        }

        public Task<MailReceiptMessage> AcknowledgeMailAsync(
            AcknowledgeMailMessage message,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            AcknowledgeMessage = message;
            return Task.FromResult(new MailReceiptMessage(
                message.MessageId,
                message.RecipientSessionId,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow));
        }
    }
}
