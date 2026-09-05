using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Exercises the mail and reservation halves of the /nodeHub SignalR contract through a real
/// hub connection against the full control plane (SPEC §16, §17, §18): sends persist through
/// the authoritative mail store, reservation acquisition is atomic with structured conflict
/// results, and handoff invalidates the old fencing token end to end.
/// </summary>
public sealed class NodeHubMailReservationTests : IClassFixture<ControlPlaneFixture>, IDisposable
{
    private readonly ControlPlaneFixture _fixture;
    private readonly HubConnection _connection;
    private readonly Guid _nodeId = Guid.NewGuid();
    private Guid _projectId;
    private Guid _requestId;
    private readonly string _scope = Guid.NewGuid().ToString("N")[..8];
    private string RootSession => $"session-root-{_scope}";
    private string ChildSession => $"session-child-{_scope}";
    private string SessionA => $"session-a-{_scope}";
    private string SessionB => $"session-b-{_scope}";

    public NodeHubMailReservationTests(ControlPlaneFixture fixture)
    {
        _fixture = fixture;
        var factory = fixture.Factory;
        _ = factory.CreateClient(); // force server initialization before opening the connection
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
    public async Task SendMail_persists_through_the_authority_and_the_recipient_reads_it()
    {
        await SeedAsync();

        var delivery = await _connection.InvokeAsync<MailDeliveryMessage>("SendMail", new SendMailMessage(
            _projectId, _requestId, $"thread-{_requestId:N}", RootSession,
            [ChildSession], "Reservation handoff requested", "I need DependencyInjection.cs.",
            MailImportance.High, AckRequired: true));

        Assert.Equal($"thread-{_requestId:N}", delivery.ThreadId);
        Assert.Contains(ChildSession, delivery.DeliveredTo);

        var inbox = await _connection.InvokeAsync<MailInboxMessage>(
            "FetchInbox", new FetchMailInboxMessage(_projectId, ChildSession, 50));
        var message = Assert.Single(inbox.Messages);
        Assert.Equal(RootSession, message.SenderSessionId);
        Assert.Equal(MailImportance.High, message.Importance);
        Assert.True(message.AcknowledgementRequired);

        await _connection.InvokeAsync<MailReceiptMessage>(
            "MarkMailRead", new MarkMailReadMessage(ChildSession, message.MessageId));
        var receipt = await _connection.InvokeAsync<MailReceiptMessage>(
            "AcknowledgeMail", new AcknowledgeMailMessage(ChildSession, message.MessageId));

        Assert.Equal(message.MessageId, receipt.MessageId);
        Assert.NotNull(receipt.AcknowledgedAtUtc);
        Assert.Empty((await _connection.InvokeAsync<MailInboxMessage>(
            "FetchInbox", new FetchMailInboxMessage(_projectId, ChildSession, 50))).Messages);

        // Durable: the delivered row lives in the authoritative mail store.
        Assert.Equal(1, CountRows("MailRecipients", "SessionId", ChildSession));
    }

    [Fact]
    public async Task Reservation_acquire_conflict_then_transfer_round_trip_through_the_hub()
    {
        await SeedAsync();

        var granted = await _connection.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionA,
                [new ReservationScopeMessage(0, "File", "src/App/DependencyInjection.cs")], "implement DI"));
        Assert.True(granted.Lease is not null, granted.Error?.Message);
        var grantedLease = granted.Lease!;
        Assert.Equal(1, grantedLease.FencingToken);

        // The owner with a wrong token receives the typed fencing-token error.
        var wrongToken = await _connection.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
                grantedLease.LeaseId, grantedLease.FencingToken + 500, SessionA,
                "src/App/DependencyInjection.cs", Operation: 1, OperationName: "write"));
        Assert.False(wrongToken.Authorized);
        Assert.Equal(ReservationErrorCodes.InvalidFencingToken, wrongToken.Error!.Code);

        var denied = await _connection.InvokeAsync<ReservationOperationResultMessage>("AcquireReservation",
            new AcquireReservationMessage(_projectId, _requestId, SessionB,
                [new ReservationScopeMessage(0, "File", "src/App/DependencyInjection.cs")], "same file"));
        Assert.True(denied.Error is not null, "the conflicting acquisition must be denied");
        Assert.Equal(ReservationErrorCodes.Conflict, denied.Error.Code);
        Assert.NotEmpty(denied.Error.Conflicts);

        // The conflicting request granted nothing.
        var listed = await _connection.InvokeAsync<ReservationLeaseMessage[]>(
            "ListReservations", new ListReservationsMessage(_projectId, IncludeReleased: false));
        var lease = Assert.Single(listed);
        Assert.Equal(grantedLease.LeaseId, lease.LeaseId);

        // Atomic handoff through the hub invalidates the old token immediately.
        var handed = await _connection.InvokeAsync<ReservationOperationResultMessage>("TransferReservation",
            new TransferReservationMessage(lease.LeaseId, SessionA, SessionB));
        Assert.True(handed.Lease is not null, handed.Error?.Message);
        var handedLease = handed.Lease!;
        Assert.Equal(SessionB, handedLease.OwnerSessionId);
        Assert.True(handedLease.FencingToken > grantedLease.FencingToken);

        // A former owner's stale decision is simply unauthorized (ownership is checked first).
        var stale = await _connection.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId, grantedLease.FencingToken, SessionA,
                "src/App/DependencyInjection.cs", Operation: 1, OperationName: "write"));
        Assert.False(stale.Authorized);
        Assert.NotNull(stale.Error);

        // The new owner mutates with the fresh token.
        var fresh = await _connection.InvokeAsync<MutationAuthorizationResultMessage>("AuthorizeMutation",
            new MutationAuthorizationMessage(
                lease.LeaseId, handedLease.FencingToken, SessionB,
                "src/App/DependencyInjection.cs", Operation: 2, OperationName: "edit"));
        Assert.True(fresh.Authorized, fresh.Error?.Message);
    }

    private async Task SeedAsync()
    {
        _ = await _connection.InvokeAsync<NodeDto>(
            "Register", new NodeRegistrationMessage(_nodeId, "pi-hub-mail", "1.0.0", "{}"));

        using var scope = _fixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var node = new NodeId(_nodeId);
        var project = Project.Register(
            node, "Mail hub project " + Guid.NewGuid().ToString("N")[..6],
            Path.Combine(Path.GetTempPath(), "pi-cc-integration", Guid.NewGuid().ToString("N")),
            "main", enabled: true, maxActiveWriteRequests: 2, maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        var request = WorkRequest.Enqueue(project.Id, WorkRequestKind.Development,
            RequestPriority.Normal, RiskLevel.Standard, "Mail hub request", "Do hub work", now);
        db.Projects.Add(project);
        db.WorkRequests.Add(request);
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = RootSession, ProjectId = project.Id.Value, RequestId = request.Id.Value,
            AgentName = "root", Role = "root", Runtime = "pi", RuntimeProfile = "root-readonly",
            Liveness = "Active", Activity = "Idle", Attention = "None", WorkState = "Working",
            StatusReason = "Seeded for hub tests", StartedAtUtcTicks = now.UtcTicks, Version = 1,
        });
        foreach (var (id, name) in new[]
                 {
                     (ChildSession, "child-0"), (SessionA, "writer-a"), (SessionB, "writer-b"),
                 })
        {
            db.AgentSessions.Add(new AgentSessionRow
            {
                Id = id, ProjectId = project.Id.Value, RequestId = request.Id.Value, ParentSessionId = RootSession,
                AgentName = name, Role = "implementer", Runtime = "pi", RuntimeProfile = "coder",
                Liveness = "Active", Activity = "Idle", Attention = "None", WorkState = "Working",
                StatusReason = "Seeded for hub tests", StartedAtUtcTicks = now.UtcTicks, Version = 1,
            });
        }

        await db.SaveChangesAsync();
        _projectId = project.Id.Value;
        _requestId = request.Id.Value;
    }

    private int CountRows(string table, string column, string value)
    {
        using var connection = new SqliteConnection($"Data Source={_fixture.SqlitePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = $value";
        command.Parameters.AddWithValue("$value", value);
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
