using System.Net;
using System.Net.Http.Json;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Api;
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
    private const string ClaimToken = "mail-coordination-fixture-token";
    private readonly Guid _nodeId;
    private Guid _projectId;
    private Guid _requestId;
    private string _rootSession = default!;
    private string _childSession = default!;

    public MailCoordinationTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        _nodeId = fixture.AuthenticatedNodeId;
        _client = fixture.CreateClient();
        _connection = fixture.CreateNodeHubConnection();
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
            _projectId, _requestId, ClaimToken, _requestId.ToString(), sender,
            [_childSession], "Reservation handoff requested",
            "I need src/DependencyInjection.cs.", MailImportance.High, AckRequired: true));

        Assert.NotEmpty(delivery.MessageId);
        Assert.Equal([_childSession], delivery.DeliveredTo);

        var inbox = await _connection.InvokeAsync<MailInboxMessage>("FetchInbox",
            new FetchMailInboxMessage(_projectId, _requestId, ClaimToken, _childSession, 50));
        var message = Assert.Single(inbox.Messages);
        Assert.Equal(delivery.MessageId, message.MessageId);
        Assert.Equal(sender, message.SenderSessionId);
        Assert.True(message.AcknowledgementRequired);
        Assert.Equal(MailImportance.High, message.Importance);
        Assert.Null(message.ReadAtUtc);
        Assert.Null(message.AcknowledgedAtUtc);

        var thread = await _connection.InvokeAsync<MailInboxMessage>("FetchThread",
            new FetchMailThreadMessage(_projectId, _requestId, ClaimToken, _childSession, _requestId.ToString()));
        Assert.Contains(thread.Messages, m => m.MessageId == delivery.MessageId);

        var read = await _connection.InvokeAsync<MailReceiptMessage>("MarkMailRead",
            new MarkMailReadMessage(_projectId, _requestId, ClaimToken, _childSession, delivery.MessageId));
        Assert.NotNull(read.ReadAtUtc);
        Assert.Null(read.AcknowledgedAtUtc);

        var ack = await _connection.InvokeAsync<MailReceiptMessage>("AcknowledgeMail",
            new AcknowledgeMailMessage(_projectId, _requestId, ClaimToken, _childSession, delivery.MessageId));
        Assert.NotNull(ack.AcknowledgedAtUtc);

        // Re-reading and re-acknowledging is idempotent.
        await _connection.InvokeAsync<MailReceiptMessage>("MarkMailRead",
            new MarkMailReadMessage(_projectId, _requestId, ClaimToken, _childSession, delivery.MessageId));
        var ackAgain = await _connection.InvokeAsync<MailReceiptMessage>("AcknowledgeMail",
            new AcknowledgeMailMessage(_projectId, _requestId, ClaimToken, _childSession, delivery.MessageId));
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
            _projectId, _requestId, ClaimToken, _requestId.ToString(), sender,
            [_childSession, third], "All hands", "Status update.", MailImportance.Normal, AckRequired: false));

        Assert.Equal(2, delivery.DeliveredTo.Count);
        foreach (var recipient in new[] { _childSession, third })
        {
            var inbox = await _connection.InvokeAsync<MailInboxMessage>("FetchInbox",
                new FetchMailInboxMessage(_projectId, _requestId, ClaimToken, recipient, 50));
            Assert.Contains(inbox.Messages, m => m.MessageId == delivery.MessageId && m.RecipientSessionId == recipient);
        }
    }

    [Fact]
    public async Task Reply_delivers_to_the_other_thread_participants()
    {
        await SeedAsync();
        var original = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, ClaimToken, _requestId.ToString(), _rootSession,
            [_childSession], "Plan check", "Please review the plan.", MailImportance.Normal, AckRequired: false));

        var reply = await _connection.InvokeAsync<MailDeliveryMessage>("ReplyMail", new ReplyMailMessage(
            _projectId, _requestId, ClaimToken, _requestId.ToString(), _childSession,
            "Plan reviewed, approved.", MailImportance.Normal, AckRequired: false));

        Assert.Equal(reply.ThreadId, original.ThreadId);
        Assert.Equal([_rootSession], reply.DeliveredTo);

        var thread = await _connection.InvokeAsync<MailInboxMessage>("FetchThread",
            new FetchMailThreadMessage(_projectId, _requestId, ClaimToken, _rootSession, original.ThreadId));
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
            .WithUrl(new Uri(_fixture.Factory.Server.BaseAddress, "nodeHub"), _fixture.ConfigureNodeHub)
            .Build();
        await recipientConnection.StartAsync();

        var pushed = new TaskCompletionSource<AgentMailMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        recipientConnection.On<AgentMailMessage>("ReceiveMail", message => pushed.TrySetResult(message));

        // Registering and heartbeating the same node id joins this connection to the
        // recipient session's live group.
        var liveNodeId = _nodeId;
        await recipientConnection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(liveNodeId, "mail-live-node", "1.0.0", "{}"));
        await recipientConnection.InvokeAsync<NodeDto>("Heartbeat",
            new NodeHeartbeatMessage(liveNodeId, [_childSession]));


        // Recipient Register on the same node id takes the live assignment; the sending
        // hub must Register again before SendMail so fail-closed auth still holds.
        await _connection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(_nodeId, "mail-coordination-node", "1.0.0", "{}"));
        var delivery = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, ClaimToken, _requestId.ToString(), _rootSession,
            [_childSession], "Live", "Delivered in real time.", MailImportance.High, AckRequired: false));

        var completed = await Task.WhenAny(pushed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(completed == pushed.Task, "ReceiveMail was not pushed to the live session within the timeout.");
        var live = await pushed.Task;
        Assert.Equal(delivery.MessageId, live.MessageId);

        await recipientConnection.DisposeAsync();
        Assert.Equal(_childSession, live.RecipientSessionId);
    }

    [Fact]
    public async Task Browser_direct_send_routes_ReceiveMail_live_exactly_once_and_only_after_persistence()
    {
        await SeedAsync();
        var recipientConnection = new HubConnectionBuilder()
            .WithUrl(new Uri(_fixture.Factory.Server.BaseAddress, "nodeHub"), _fixture.ConfigureNodeHub)
            .Build();
        await recipientConnection.StartAsync();

        var pushed = new List<AgentMailMessage>();
        var firstPush = new TaskCompletionSource<AgentMailMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        recipientConnection.On<AgentMailMessage>("ReceiveMail", message =>
        {
            lock (pushed)
            {
                pushed.Add(message);
            }
            firstPush.TrySetResult(message);
        });

        var liveNodeId = _nodeId;
        await recipientConnection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(liveNodeId, "mail-http-live-node", "1.0.0", "{}"));
        await recipientConnection.InvokeAsync<NodeDto>("Heartbeat",
            new NodeHeartbeatMessage(liveNodeId, [_childSession]));

        // A recipient list containing an unknown session fails before persistence, so the
        // connected recipient must not receive anything for it.
        var rejected = await _client.PostAsJsonAsync($"/api/requests/{_requestId}/messages",
            new
            {
                projectId = _projectId,
                senderSessionId = _rootSession,
                recipients = new[] { _childSession, NewSession() },
                subject = "Never persisted",
                bodyMarkdown = "Should not route.",
                importance = "normal",
                ackRequired = false
            });
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);

        var sent = await PostAsync<AgentMessageDto>($"/api/requests/{_requestId}/messages",
            new
            {
                projectId = _projectId,
                senderSessionId = _rootSession,
                recipients = new[] { _childSession },
                subject = "Live from browser",
                bodyMarkdown = "Delivered in real time.",
                importance = "high",
                ackRequired = false
            });

        var live = await firstPush.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(sent.Id, live.MessageId);
        Assert.Equal(_childSession, live.RecipientSessionId);

        // Allow any stray duplicate push to arrive before asserting exactly-once delivery.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await recipientConnection.DisposeAsync();
        lock (pushed)
        {
            var only = Assert.Single(pushed);
            Assert.Equal(sent.Id, only.MessageId);
        }
    }

    [Fact]
    public async Task Browser_endpoints_send_session_messages_and_acknowledge()
    {
        await SeedAsync();
        var sender = NewSession();
        await SeedSessionAsync(sender, parentSessionId: _rootSession);

        var sent = await PostAsync<AgentMessageDto>($"/api/requests/{_requestId}/messages",
            new
            {
                projectId = _projectId,
                senderSessionId = sender,
                recipients = new[] { _childSession },
                subject = "From browser",
                bodyMarkdown = "Direct note.",
                importance = "normal",
                ackRequired = true
            });

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
    public async Task Session_cancel_dispatches_to_the_owning_node_and_projection_follows_the_node_event()
    {
        await SeedAsync();
        using var response = await _client.PostAsync("/api/sessions/does-not-exist/cancel", null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // A node heartbeating the session active joins its live group and receives the
        // cancellation command; it reports the outcome by publishing the real event.
        var cancelReceived = new TaskCompletionSource<CancelSessionCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var nodeId = _nodeId;
        _connection.On<CancelSessionCommand>("CancelSession", command =>
        {
            cancelReceived.TrySetResult(command);
            return Task.CompletedTask;
        });
        await _connection.InvokeAsync<NodeDto>("Register",
            new NodeRegistrationMessage(nodeId, "mail-cancel-node", "1.0.0", "{}"));
        await _connection.InvokeAsync<NodeDto>("Heartbeat",
            new NodeHeartbeatMessage(nodeId, [_childSession]));

        var dispatch = await _client.PostAsJsonAsync($"/api/sessions/{_childSession}/cancel",
            new { reason = "operator-stop" });
        Assert.Equal(HttpStatusCode.Accepted, dispatch.StatusCode);

        var command = await cancelReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(_childSession, command.SessionId);
        Assert.Equal("operator-stop", command.Reason);

        // The node reports the real outcome through the normal event path; the projection
        // must reflect the node event, not the dispatch.
        await _connection.InvokeAsync<NodeEventAcknowledgementMessage>("PublishEvents", new NodeEventBatchMessage(
        [
            new NodeEventMessage(
                EventId: $"cancel-{Guid.NewGuid()}",
                NodeId: nodeId,
                ProjectId: _projectId,
                RequestId: _requestId,
                ClaimToken: ClaimToken,
                SessionId: _childSession,
                Sequence: 1,
                Type: "session.cancelled",
                OccurredAt: DateTimeOffset.UtcNow,
                PayloadJson: "{}"),
        ]));

        using var scope = _fixture.Factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<IAgentSessionStore>();
        var projection = await sessions.GetAsync(_childSession);
        Assert.NotNull(projection);
        Assert.Equal(AgentLiveness.Exited, projection!.Liveness);
    }

    private async Task SeedAsync()
    {
        if (_projectId != Guid.Empty)
        {
            return;
        }
        _ = await _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(_nodeId, "mail-coordination-node", "1.0.0", "{}"));

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = Project.Register(
            "Mail project " + Guid.NewGuid().ToString("N")[..6],
            "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 2, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var nodeId = new NodeId(_nodeId);
        var repositoryPath = _fixture.CreateGitRepository();
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, repositoryPath, now);
        Assert.True(binding.ApplyValidationResult(
            nodeId,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Seeded for mail coordination tests.",
            repositoryPath,
            now));
        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development, RequestPriority.Normal,
            RiskLevel.Standard, "Mail request", "Do mail work", now);
        request.Start(now);
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            ClaimToken,
            now,
            TimeSpan.FromMinutes(5));
        db.Projects.Add(project);
        db.WorkspaceBindings.Add(binding);
        db.WorkRequests.Add(request);
        db.ExecutionAssignments.Add(assignment);
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
