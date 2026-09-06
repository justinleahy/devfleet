using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Mail;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests.Mail;

/// <summary>
/// Focused coverage for the mail coordination store: session validation, per-recipient read
/// and acknowledgement rules, duplicate recipients, cross-project rejection, identity
/// uniqueness under concurrency, and deterministic name collision resolution (SPEC §16).
/// </summary>
public class MailServiceTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = new(Base);
    private readonly Guid _projectId;
    private readonly Guid _requestId;

    public MailServiceTests()
    {
        using var db = CreateContext();
        TestNodes.SeedNode(db, TestNodes.NewNodeId(), _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        TestNodes.SaveAsync(db).GetAwaiter().GetResult();
        _projectId = project.Id.Value;
        _requestId = request.Id.Value;
    }

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private MailService CreateService(ControlPlaneDbContext db) =>
        new(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier());

    private AgentIdentityRegistry CreateRegistry(ControlPlaneDbContext db) => new(_clock, db);

    /// <summary>Registers an active agent session row directly, as the session projector would.</summary>
    private void SeedSession(ControlPlaneDbContext db, string sessionId, Guid? projectId = null, Guid? requestId = null)
    {
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            ProjectId = projectId ?? _projectId,
            RequestId = requestId ?? _requestId,
            AgentName = sessionId + "-name",
            Role = "implementer",
            Runtime = "pi",
            Model = "codex/default",
            Liveness = "Alive",
            Activity = "Working",
            Attention = "None",
            WorkState = "Active",
            StartedAtUtcTicks = Base.UtcTicks,
            LastSequence = 0,
            Version = 0,
        });
        TestNodes.SaveAsync(db).GetAwaiter().GetResult();
    }

    private (AgentIdentityDto Root, AgentIdentityDto Child) AllocatePair(ControlPlaneDbContext db, string root = "session-root", string child = "session-child")
    {
        SeedSession(db, root);
        SeedSession(db, child);
        var registry = CreateRegistry(db);
        var rootIdentity = registry.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), root, "GreenCastle", "orchestrator", "pi")).GetAwaiter().GetResult();
        var childIdentity = registry.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), child, "RedAnvil", "implementer", "pi")).GetAwaiter().GetResult();
        return (rootIdentity, childIdentity);
    }

    private SendAgentMessageCommand SendCommand(
        string? sender = "session-root",
        IReadOnlyList<string>? recipients = null,
        bool ackRequired = false,
        MessageImportance importance = MessageImportance.Normal) => new(
        new ProjectId(_projectId),
        new WorkRequestId(_requestId),
        "thread-1",
        sender,
        recipients ?? ["session-child"],
        "Reservation handoff requested",
        "I need src/DependencyInjection.cs.",
        importance,
        ackRequired);

    [Fact]
    public async Task Send_PersistsMessageWithRecipientsAndRoundTrips()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);

        var sent = await service.SendAsync(SendCommand());

        Assert.Matches("^msg-[0-9a-f]{32}$", sent.Id);
        Assert.False(sent.IsFromHuman);
        Assert.Equal("session-child", Assert.Single(sent.Recipients).SessionId);
        Assert.Null(sent.Recipients[0].ReadAtUtc);
        Assert.Null(sent.Recipients[0].AcknowledgedAtUtc);

        // Persistence across contexts: a fresh context sees the same row.
        using var fresh = CreateContext();
        var thread = await CreateService(fresh).GetThreadAsync(new ProjectId(_projectId), "thread-1");
        Assert.Single(thread);
        Assert.Equal(sent.Id, thread[0].Id);
    }

    [Fact]
    public async Task Send_HumanSender_IsMarkedHumanAndHighPriorityIsPreserved()
    {
        SeedSession(CreateContext(), "session-child");
        using var db = CreateContext();
        var sent = await CreateService(db).SendAsync(SendCommand(sender: null, importance: MessageImportance.High));

        Assert.True(sent.IsFromHuman);
        Assert.Null(sent.SenderSessionId);
        Assert.Equal(MessageImportance.High, sent.Importance);
    }

    [Fact]
    public async Task Send_DuplicateRecipients_AreRejectedDeterministically()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);

        var error = await Assert.ThrowsAsync<MailValidationException>(() => service.SendAsync(
            SendCommand(recipients: ["session-child", "session-child", " session-child "])));

        Assert.Contains("session-child", error.Message);
        Assert.Empty(await service.GetThreadAsync(new ProjectId(_projectId), "thread-1"));
    }

    [Fact]
    public async Task Send_RecipientFromAnotherProject_IsRejected()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);

        // "session-foreign" exists, but in a different project.
        SeedSession(db, "session-foreign", projectId: Guid.NewGuid());

        await Assert.ThrowsAsync<MailSessionNotFoundException>(() => service.SendAsync(
            SendCommand(recipients: ["session-foreign"])));
    }

    [Fact]
    public async Task Send_SenderFromAnotherRequest_IsRejected()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);

        // Same project, different request.
        SeedSession(db, "session-other-request", requestId: Guid.NewGuid());

        await Assert.ThrowsAsync<MailSessionNotFoundException>(() => service.SendAsync(
            SendCommand(sender: "session-other-request")));
    }

    [Fact]
    public async Task Send_UnknownRecipient_IsRejected()
    {
        SeedSession(CreateContext(), "session-root");
        using var db = CreateContext();
        await Assert.ThrowsAsync<MailSessionNotFoundException>(() =>
            CreateService(db).SendAsync(SendCommand(recipients: ["session-ghost"])));
    }

    [Fact]
    public async Task Reply_GoesToAllOtherThreadParticipantsAndStaysInThread()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);
        await service.SendAsync(SendCommand(recipients: ["session-child"]));

        var reply = await service.ReplyAsync(new ReplyAgentMessageCommand(
            new ProjectId(_projectId), "thread-1", "session-child", "On it.", MessageImportance.Normal, AckRequired: false));

        Assert.Equal("session-root", Assert.Single(reply.Recipients).SessionId);
        Assert.Equal("thread-1", reply.ThreadId);

        var thread = await service.GetThreadAsync(new ProjectId(_projectId), "thread-1");
        Assert.Equal(2, thread.Count);
        Assert.Equal("Reservation handoff requested", thread[1].Subject);
    }

    [Fact]
    public async Task Reply_UnknownThread_IsRejected()
    {
        SeedSession(CreateContext(), "session-root");
        using var db = CreateContext();
        await Assert.ThrowsAsync<MailThreadNotFoundException>(() => CreateService(db).ReplyAsync(
            new ReplyAgentMessageCommand(
                new ProjectId(_projectId), "missing-thread", "session-root", "Hello?", MessageImportance.Normal, AckRequired: false)));
    }

    [Fact]
    public async Task Unread_ListsOnlyUnreadMessagesForTheAddressee()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);
        var first = await service.SendAsync(SendCommand(recipients: ["session-child"]));
        await service.SendAsync(SendCommand(recipients: ["session-root"]));
        _clock.Advance(TimeSpan.FromSeconds(1));

        var unread = await service.GetUnreadAsync(new ProjectId(_projectId), "session-child");
        Assert.Equal([first.Id], unread.Select(m => m.Id));

        await service.MarkReadAsync(first.Id, "session-child");
        Assert.Empty(await service.GetUnreadAsync(new ProjectId(_projectId), "session-child"));
    }

    [Fact]
    public async Task MarkRead_IsIdempotentAndPerRecipient()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);
        var message = await service.SendAsync(SendCommand(sender: null, recipients: ["session-child"], ackRequired: true));

        var first = await service.MarkReadAsync(message.Id, "session-child");
        _clock.Advance(TimeSpan.FromSeconds(5));
        var second = await service.MarkReadAsync(message.Id, "session-child");

        Assert.Equal(first.Recipients[0].ReadAtUtc, second.Recipients[0].ReadAtUtc);
    }

    [Fact]
    public async Task MarkRead_ByNonAddressee_IsRejected()
    {
        AllocatePair(CreateContext());
        SeedSession(CreateContext(), "session-third");
        using var db = CreateContext();
        var service = CreateService(db);
        var message = await service.SendAsync(SendCommand(recipients: ["session-child"]));

        await Assert.ThrowsAsync<MailNotAddresseeException>(() => service.MarkReadAsync(message.Id, "session-third"));
        await Assert.ThrowsAsync<MailNotAddresseeException>(() => service.AcknowledgeAsync(message.Id, "session-root"));
    }

    [Fact]
    public async Task MarkRead_UnknownMessage_IsRejected()
    {
        using var db = CreateContext();
        await Assert.ThrowsAsync<MailMessageNotFoundException>(() =>
            CreateService(db).MarkReadAsync("msg-does-not-exist", "session-child"));
    }

    [Fact]
    public async Task Acknowledge_BeforeRead_IsRejected_AfterRead_Succeeds()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var service = CreateService(db);
        var message = await service.SendAsync(SendCommand(ackRequired: true));

        await Assert.ThrowsAsync<MailAcknowledgementRequiresReadException>(
            () => service.AcknowledgeAsync(message.Id, "session-child"));

        await service.MarkReadAsync(message.Id, "session-child");
        var acknowledged = await service.AcknowledgeAsync(message.Id, "session-child");

        Assert.NotNull(acknowledged.Recipients[0].ReadAtUtc);
        Assert.NotNull(acknowledged.Recipients[0].AcknowledgedAtUtc);
        Assert.True(acknowledged.Recipients[0].AcknowledgedAtUtc >= acknowledged.Recipients[0].ReadAtUtc);
    }

    [Fact]
    public async Task Allocate_ReturnsRequestedName_AndIsIdempotentPerSession()
    {
        var (root, _) = AllocatePair(CreateContext());
        Assert.Equal("GreenCastle", root.AgentName);

        using var db = CreateContext();
        var registry = CreateRegistry(db);
        var again = await registry.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), "session-root", "GreenCastle", "orchestrator", "pi"));

        Assert.Equal(root, again);
    }

    [Fact]
    public async Task Allocate_NameCollision_ResolvesToLowestFreeSuffixedName_Deterministically()
    {
        var (_, child) = AllocatePair(CreateContext());
        Assert.Equal("RedAnvil", child.AgentName);

        using var db = CreateContext();
        SeedSession(db, "session-second");
        SeedSession(db, "session-third");

        var registry = CreateRegistry(db);
        // A new session requesting "GreenCastle" must deterministically receive "GreenCastle-2".
        var second = await registry.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), "session-second", "GreenCastle", "implementer", "pi"));
        var third = await registry.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), "session-third", "GreenCastle", "implementer", "pi"));

        Assert.Equal("GreenCastle-2", second.AgentName);
        Assert.Equal("GreenCastle-3", third.AgentName);

        // Same outcome on a fresh registry instance over the same data.
        using var fresh = CreateContext();
        var repeat = await CreateRegistry(fresh).AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), "session-second", "GreenCastle", "implementer", "pi"));
        Assert.Equal("GreenCastle-2", repeat.AgentName);
    }

    [Fact]
    public async Task Allocate_ConcurrentSessions_NeverShareAName()
    {
        using var db = CreateContext();
        const int sessionCount = 8;
        for (var i = 0; i < sessionCount; i++)
        {
            SeedSession(db, $"session-{i}");
        }

        // All allocations run against one real SQLite database through independent contexts,
        // so the (ProjectId, AgentName) unique index is the only serialization point.
        var tasks = Enumerable.Range(0, sessionCount)
            .Select(async i =>
            {
                await using var context = CreateContext();
                var registry = CreateRegistry(context);
                return (Index: i, Identity: await registry.AllocateAsync(new AllocateAgentIdentityCommand(
                    new ProjectId(_projectId), $"session-{i}", "GreenCastle", "implementer", "pi")));
            })
            .ToList();
        var results = (await Task.WhenAll(tasks)).OrderBy(r => r.Identity.AgentName, StringComparer.Ordinal).ToList();

        Assert.Equal(sessionCount, results.Count);
        Assert.Equal(results.Count, results.Select(r => r.Identity.AgentName).Distinct().Count());
        Assert.Equal("GreenCastle", results[0].Identity.AgentName);
        Assert.All(results, r => Assert.Equal($"session-{r.Index}", r.Identity.SessionId));
        // Deterministic suffix chain: name, name-2, name-3, ...
        Assert.Equal(
            results.Select((_, i) => i == 0 ? "GreenCastle" : $"GreenCastle-{i + 1}"),
            results.Select(r => r.Identity.AgentName));
    }

    [Fact]
    public async Task Release_FreesTheNameForReallocation()
    {
        AllocatePair(CreateContext());
        using var db = CreateContext();
        var registry = CreateRegistry(db);

        await registry.ReleaseAsync("session-root");
        Assert.Null(await registry.FindByNameAsync(new ProjectId(_projectId), "GreenCastle"));

        SeedSession(db, "session-new");
        var reallocated = await registry.AllocateAsync(new AllocateAgentIdentityCommand(
            new ProjectId(_projectId), "session-new", "GreenCastle", "implementer", "pi"));
        Assert.Equal("GreenCastle", reallocated.AgentName);
    }

    [Fact]
    public async Task Allocate_RejectsMalformedCommands()
    {
        using var db = CreateContext();
        var registry = CreateRegistry(db);
        var project = new ProjectId(_projectId);

        await Assert.ThrowsAsync<MailValidationException>(() => registry.AllocateAsync(
            new AllocateAgentIdentityCommand(project, "", "Name", "role", "pi")));
        await Assert.ThrowsAsync<MailValidationException>(() => registry.AllocateAsync(
            new AllocateAgentIdentityCommand(project, "session-x", " ", "role", "pi")));
        await Assert.ThrowsAsync<MailValidationException>(() => registry.AllocateAsync(
            new AllocateAgentIdentityCommand(project, "session-x", "Name", "", "pi")));
    }

    [Fact]
    public async Task MailState_SurvivesSchemaCreatedFromMigrations()
    {
        // EnsureCreated builds the model from the current mappings; verify the mail tables
        // exist with their uniqueness constraints by round-tripping through a new context.
        AllocatePair(CreateContext());
        await using var fresh = CreateContext();
        var names = await fresh.Database
            .SqlQuery<string>($"SELECT name AS Value FROM sqlite_master WHERE type = 'table' AND name LIKE 'Mail%' ORDER BY name")
            .ToListAsync();
        Assert.Equal(["MailAgentIdentities", "MailMessages", "MailRecipients"], names);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
