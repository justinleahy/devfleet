using System.Net;
using System.Net.Http.Json;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.ControlPlane.Api;
using PiCommandCenter.Domain.Mail;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// End-to-end mail coordination over the real control plane: SignalR hub operations (send,
/// multi-recipient, reply, inbox, thread, mark-read, acknowledge, live routing) and the browser
/// mail/guidance endpoints (SPEC §16, §30.3, §30.4).
/// </summary>
public sealed class MailCoordinationTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HttpClient _client;
    private readonly HubConnection _connection;
    private readonly Guid _nodeId = Guid.NewGuid();
    private Guid _projectId;
    private Guid _requestId;
    private string _rootSession = default!;
    private string _childSession = default!;

    public MailCoordinationTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        var factory = fixture.Factory;
        _client = factory.CreateClient();
        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        _connection.StartAsync().GetAwaiter().GetResult();
    }

    public void Dispose() => _connection.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task Direct_send_inbox_read_and_acknowledge_round_trip_through_the_hub()
    {
        await SeedAsync();
        var sender = NewSession();
        await SeedSessionAsync(sender, parentSessionId: _rootSession);
        await SeedSessionAsync(_childSession);

        var delivery = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, _requestId.ToString(), sender,
            [_childSession], "Reservation handoff requested",
            "I need src/DependencyInjection.cs.", MailImportance.High, AckRequired: true));

        Assert.NotEmpty(delivery.MessageId);
        Assert.Equal([_childSession], delivery.DeliveredTo);

        var inbox = await _connection.InvokeAsync<MailInboxMessage>("FetchInbox",
            new FetchMailInboxMessage(_projectId, _childSession, 50));
        var message = Assert.Single(inbox.Messages);
        Assert.Equal(delivery.MessageId, message.MessageId);
        Assert.Equal(sender, message.SenderSessionId);
        Assert.True(message.AcknowledgementRequired);
        Assert.Equal(MailImportance.High, message.Importance);
        Assert.Null(message.ReadAtUtc);
        Assert.Null(message.AcknowledgedAtUtc);

        var thread = await _connection.InvokeAsync<MailInboxMessage>("FetchThread",
            new FetchMailThreadMessage(_projectId, _childSession, _requestId.ToString()));
        Assert.Contains(thread.Messages, m => m.MessageId == delivery.MessageId);

        var read = await _connection.InvokeAsync<MailReceiptMessage>("MarkMailRead",
            new MarkMailReadMessage(_childSession, delivery.MessageId));
        Assert.NotNull(read.ReadAtUtc);
        Assert.Null(read.AcknowledgedAtUtc);

        var ack = await _connection.InvokeAsync<MailReceiptMessage>("AcknowledgeMail",
            new AcknowledgeMailMessage(_childSession, delivery.MessageId));
        Assert.NotNull(ack.AcknowledgedAtUtc);

        // Re-reading and re-acknowledging is idempotent.
        await _connection.InvokeAsync<MailReceiptMessage>("MarkMailRead",
            new MarkMailReadMessage(_childSession, delivery.MessageId));
        var ackAgain = await _connection.InvokeAsync<MailReceiptMessage>("AcknowledgeMail",
            new AcknowledgeMailMessage(_childSession, delivery.MessageId));
        Assert.Equal(ack.AcknowledgedAtUtc, ackAgain.AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Multi_recipient_send_delivers_to_every_recipient()
    {
        await SeedAsync();
        var sender = _rootSession;
        var third = NewSession();
        await SeedSessionAsync(third, parentSessionId: _rootSession);

        var delivery = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, _requestId.ToString(), sender,
            [_childSession, third], "All hands", "Status update.", MailImportance.Normal, AckRequired: false));

        Assert.Equal(2, delivery.DeliveredTo.Count);
        foreach (var recipient in new[] { _childSession, third })
        {
            var inbox = await _connection.InvokeAsync<MailInboxMessage>("FetchInbox",
                new FetchMailInboxMessage(_projectId, recipient, 50));
            Assert.Contains(inbox.Messages, m => m.MessageId == delivery.MessageId && m.RecipientSessionId == recipient);
        }
    }

    [Fact]
    public async Task Reply_delivers_to_the_other_thread_participants()
    {
        await SeedAsync();
        var original = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, _requestId.ToString(), _rootSession,
            [_childSession], "Plan check", "Please review the plan.", MailImportance.Normal, AckRequired: false));

        var reply = await _connection.InvokeAsync<MailDeliveryMessage>("ReplyMail", new ReplyMailMessage(
            _projectId, _requestId.ToString(), _childSession, "Plan reviewed, approved.", MailImportance.Normal, AckRequired: false));

        Assert.Equal(reply.ThreadId, original.ThreadId);
        Assert.Equal([_rootSession], reply.DeliveredTo);

        var thread = await _connection.InvokeAsync<MailInboxMessage>("FetchThread",
            new FetchMailThreadMessage(_projectId, _rootSession, original.ThreadId));
        Assert.Equal(2, thread.Messages.Count);
    }

    [Fact]
    public async Task Guidance_reaches_root_specific_and_all_targets_as_high_priority_human_mail()
    {
        await SeedAsync();
        var child2 = NewSession();
        await SeedSessionAsync(child2, parentSessionId: _rootSession);

        // Root target.
        var root = await PostGuidanceAsync("root");
        Assert.Equal([_rootSession], root.Recipients);

        // Specific target.
        var specific = await PostGuidanceAsync(_childSession);
        Assert.Equal([_childSession], specific.Recipients);

        // All target.
        var all = await PostGuidanceAsync("all");
        Assert.Equal(new SortedSet<string> { _rootSession, _childSession, child2 }, new SortedSet<string>(all.Recipients));

        // Unknown target.
        var response = await _client.PostAsJsonAsync($"/api/requests/{_requestId}/guidance",
            new { projectId = _projectId, target = "does-not-exist", bodyMarkdown = "hi" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // Recorded in the thread as human, high importance.
        var thread = await GetAsync<MessageListResponse>($"/api/requests/{_requestId}/messages?projectId={_projectId}");
        var guidance = thread.Messages.Where(m => m.Id == root.MessageId).Single();
        Assert.Null(guidance.SenderSessionId);
        Assert.True(guidance.IsFromHuman);
        Assert.Equal(MessageImportance.High, guidance.Importance);
        Assert.True(guidance.AcknowledgementRequired);
    }

    [Fact]
    public async Task Live_routing_pushes_ReceiveMail_to_nodes_with_the_recipient_active()
    {
        await SeedAsync();
        var recipientConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_fixture.Factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => _fixture.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        await recipientConnection.StartAsync();

        var pushed = new TaskCompletionSource<AgentMailMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        recipientConnection.On<AgentMailMessage>("ReceiveMail", message => pushed.TrySetResult(message));

        // Registering and heartbeating the same node id joins this connection to the
        // recipient session's live group.
        var liveNodeId = Guid.NewGuid();
        await recipientConnection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(liveNodeId, "mail-live-node", "1.0.0", "{}"));
        await recipientConnection.InvokeAsync<NodeDto>("Heartbeat",
            new NodeHeartbeatMessage(liveNodeId, [_childSession]));

        var delivery = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, _requestId.ToString(), _rootSession,
            [_childSession], "Live", "Delivered in real time.", MailImportance.High, AckRequired: false));

        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == pushed.Task, "ReceiveMail was not pushed to the live session within the timeout.");
        var live = await pushed.Task;
        Assert.Equal(delivery.MessageId, live.MessageId);

        await recipientConnection.DisposeAsync();
        Assert.Equal(_childSession, live.RecipientSessionId);
    }

    [Fact]
    public async Task Browser_endpoints_send_session_messages_and_acknowledge()
    {
        await SeedAsync();
        var sender = NewSession();
        await SeedSessionAsync(sender, parentSessionId: _rootSession);

        var sent = await PostAsync<AgentMessageDto>($"/api/requests/{_requestId}/messages",
            new { projectId = _projectId, senderSessionId = sender, recipients = new[] { _childSession },
                subject = "From browser", bodyMarkdown = "Direct note.", importance = "normal", ackRequired = true });

        var sessionMessage = await PostAsync<AgentMessageDto>($"/api/sessions/{_childSession}/message",
            new { projectId = _projectId, requestId = _requestId, subject = "Human", bodyMarkdown = "From the human." });
        Assert.Null(sessionMessage.SenderSessionId);

        // Acknowledging before a read conflicts.
        var conflict = await _client.PostAsJsonAsync($"/api/messages/{sent.Id}/acknowledge",
            new { sessionId = _childSession });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);

        using var scope = _fixture.Factory.Services.CreateScope();
        var messages = scope.ServiceProvider.GetRequiredService<IMessageService>();
        await messages.MarkReadAsync(sent.Id, _childSession);

        var ack = await PostAsync<AgentMessageDto>($"/api/messages/{sent.Id}/acknowledge",
            new { sessionId = _childSession });
        Assert.True(ack.Recipients.Single(r => r.SessionId == _childSession).AcknowledgedAtUtc is not null);
    }

    [Fact]
    public async Task Session_cancel_records_a_cancelled_projection()
    {
        await SeedAsync();
        var cancelled = await PostAsync<AgentSessionDto>($"/api/sessions/{_childSession}/cancel", new { });
        Assert.Equal(AgentLiveness.Exited, cancelled.Liveness);

        var missing = await _client.PostAsync("/api/sessions/does-not-exist/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private async Task SeedAsync()
    {
        if (_projectId != Guid.Empty)
        {
            return;
        }

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var nodeId = new NodeId(_nodeId);
        var project = Project.Register(
            nodeId, "Mail project " + Guid.NewGuid().ToString("N")[..6],
            Path.Combine(Path.GetTempPath(), "pi-cc-integration", Guid.NewGuid().ToString("N")),
            "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 2, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        db.Projects.Add(project);
        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development, RequestPriority.Normal,
            RiskLevel.Standard, "Mail request", "Do mail work", now);
        db.WorkRequests.Add(request);
        await db.SaveChangesAsync();
        _projectId = project.Id.Value;
        _requestId = request.Id.Value;

        _rootSession = NewSession();
        _childSession = NewSession();
        await SeedSessionAsync(_rootSession);
        await SeedSessionAsync(_childSession, parentSessionId: _rootSession);
    }

    private async Task SeedSessionAsync(string sessionId, string? parentSessionId = null)
    {
        using var scope = _fixture.Factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        await sessions.ApplyAsync(new NormalizedAgentEvent(
            ProtocolVersion: 1,
            EventId: $"seed-{Guid.NewGuid()}",
            NodeId: _nodeId.ToString(),
            ProjectId: _projectId.ToString(),
            RequestId: _requestId.ToString(),
            SessionId: sessionId,
            ParentSessionId: parentSessionId,
            Sequence: 0,
            Runtime: "pi",
            Type: "session.registered",
            OccurredAt: DateTimeOffset.UtcNow,
            Payload: new Dictionary<string, object?>
            {
                ["agentName"] = "agent-" + sessionId[..8],
                ["role"] = parentSessionId is null ? "root" : "worker",
            }));
    }

    private static string NewSession() => "session-" + Guid.NewGuid().ToString("N");

    private async Task<GuidanceDeliveryResponse> PostGuidanceAsync(string target)
    {
        var response = await _client.PostAsJsonAsync($"/api/requests/{_requestId}/guidance",
            new { projectId = _projectId, target, bodyMarkdown = $"Guidance to {target}." });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<GuidanceDeliveryResponse>())!;
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await _client.PostAsJsonAsync(url, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
