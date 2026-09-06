using PiCommandCenter.Application.Mail;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Requests;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Mail;

namespace PiCommandCenter.Infrastructure.Tests.Mail;

/// <summary>
/// Integration tests for the EF Core mail coordination store (SPEC §16): every operation must
/// persist durable rows and respect the project/request session boundary, per-recipient read and
/// acknowledgement state, and the human-guidance convention (null sender session).
/// </summary>
public class AgentMailServiceTests : IDisposable
{
    private static readonly DateTimeOffset Base = TestNodes.Start;

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    private Guid _projectId;
    private Guid _requestId;

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

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

    private async Task<MailWorld> CreateWorldAsync(int childCount = 1)
    {
        var db = CreateContext();
        var node = TestNodes.SeedNode(db, TestNodes.NewNodeId(), _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var request = TestNodes.SeedRequest(db, project, _clock);
        _projectId = project.Id.Value;
        _requestId = request.Id.Value;

        SeedSessionRow(db, "session-root", project.Id.Value, request.Id.Value, parent: null);
        for (var i = 0; i < childCount; i++)
        {
            SeedSessionRow(db, $"session-child-{i}", project.Id.Value, request.Id.Value, "session-root");
        }

        await TestNodes.SaveAsync(db);
        return new MailWorld(db, new MailService(_clock, db, new PiCommandCenter.Application.Live.ProjectionNotifier()), new AgentIdentityRegistry(_clock, db), project.Id, request.Id);
    }
    private static void SeedSessionRow(
        ControlPlaneDbContext db,
        string sessionId,
        Guid projectId,
        Guid requestId,
        string? parent)
    {
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = sessionId,
            ProjectId = projectId,
            RequestId = requestId,
            ParentSessionId = parent,
            AgentName = sessionId + "-name",
            Role = "implementer",
            Runtime = "pi",
            Model = "codex/default",
            Liveness = "Alive",
            Activity = "Working",
            Attention = "None",
            WorkState = "Active",
            StartedAtUtcTicks = TestNodes.Start.UtcTicks,
            LastSequence = 0,
            Version = 0,
        });
    }

    private SendAgentMessageCommand Send(
        string? sender,
        IReadOnlyList<string> recipients,
        MessageImportance importance = MessageImportance.Normal,
        bool ackRequired = false,
        string? threadId = null,
        string subject = "Handoff") => new(
        new ProjectId(_projectId),
        new WorkRequestId(_requestId),
        threadId ?? "thread-1",
        sender,
        recipients,
        subject,
        "I need DependencyInjection.cs.",
        importance,
        ackRequired);

    [Fact]
    public async Task Send_persists_a_message_with_per_recipient_delivery_state()
    {
        var world = await CreateWorldAsync(2);

        var message = await world.Mail.SendAsync(Send("session-root", ["session-child-0", "session-child-1"], ackRequired: true));

        Assert.False(string.IsNullOrEmpty(message.Id));
        Assert.Equal("session-root", message.SenderSessionId);
        Assert.False(message.IsFromHuman);
        Assert.Equal(2, message.Recipients.Count);
        Assert.All(message.Recipients, r => Assert.Null(r.ReadAtUtc));
        Assert.All(message.Recipients, r => Assert.Null(r.AcknowledgedAtUtc));

        // Durable: a fresh context sees the identical stored row.
        await using var fresh = CreateContext();
        var stored = await new MailService(_clock, fresh, new PiCommandCenter.Application.Live.ProjectionNotifier()).GetThreadAsync(new ProjectId(_projectId), "thread-1");
        var persisted = Assert.Single(stored);
        Assert.Equal(message.Id, persisted.Id);
        Assert.Equal(2, persisted.Recipients.Count);
    }

    [Fact]
    public async Task Human_guidance_is_recorded_without_a_sender_and_marked_human_originated()
    {
        var world = await CreateWorldAsync();

        var message = await world.Mail.SendAsync(Send(null, ["session-root"], importance: MessageImportance.High));

        Assert.Null(message.SenderSessionId);
        Assert.True(message.IsFromHuman);
        Assert.Equal(MessageImportance.High, message.Importance);
    }

    [Fact]
    public async Task Send_rejects_recipients_from_another_request()
    {
        var world = await CreateWorldAsync();

        // A session that belongs to a different request of the same project.
        var otherRequest = TestNodes.SeedRequest(world.Db, await world.Db.Projects.AsTracking().SingleAsync(p => p.Id == world.ProjectId), _clock);
        SeedSessionRow(world.Db, "session-stranger", world.ProjectId.Value, otherRequest.Id.Value, null);
        await TestNodes.SaveAsync(world.Db);

        await Assert.ThrowsAsync<MailSessionNotFoundException>(() =>
            world.Mail.SendAsync(Send("session-root", ["session-stranger"])));
        await Assert.ThrowsAsync<MailSessionNotFoundException>(() =>
            world.Mail.SendAsync(Send("session-stranger", ["session-root"])));
    }

    [Fact]
    public async Task Send_rejects_empty_and_duplicate_recipients()
    {
        var world = await CreateWorldAsync();

        await Assert.ThrowsAsync<MailValidationException>(() =>
            world.Mail.SendAsync(Send("session-root", [])));
        await Assert.ThrowsAsync<MailValidationException>(() =>
            world.Mail.SendAsync(Send("session-root", ["session-child-0", "session-child-0"])));
    }

    [Fact]
    public async Task Unread_inbox_is_per_recipient_and_oldest_first_and_clears_on_mark_read()
    {
        var world = await CreateWorldAsync(2);

        var first = await world.Mail.SendAsync(Send("session-root", ["session-child-0"], threadId: "t-a"));
        _clock.Advance(TimeSpan.FromSeconds(5));
        var second = await world.Mail.SendAsync(Send("session-root", ["session-child-0", "session-child-1"], threadId: "t-b"));

        var unread = await world.Mail.GetUnreadAsync(world.ProjectId, "session-child-0");
        Assert.Equal([first.Id, second.Id], unread.Select(m => m.Id).ToList());
        // The other recipient still has only its own message.
        var otherInbox = await world.Mail.GetUnreadAsync(world.ProjectId, "session-child-1");
        var only = Assert.Single(otherInbox);
        Assert.Equal(second.Id, only.Id);

        await world.Mail.MarkReadAsync(second.Id, "session-child-0");
        var afterRead = await world.Mail.GetUnreadAsync(world.ProjectId, "session-child-0");
        var remaining = Assert.Single(afterRead);
        Assert.Equal(first.Id, remaining.Id);
    }

    [Fact]
    public async Task Mark_read_is_idempotent_and_ack_requires_a_prior_read()
    {
        var world = await CreateWorldAsync();
        var message = await world.Mail.SendAsync(Send("session-root", ["session-child-0"], ackRequired: true));

        await Assert.ThrowsAsync<MailAcknowledgementRequiresReadException>(() =>
            world.Mail.AcknowledgeAsync(message.Id, "session-child-0"));

        var afterRead = await world.Mail.MarkReadAsync(message.Id, "session-child-0");
        Assert.NotNull(afterRead.Recipients.Single().ReadAtUtc);
        var readAt = afterRead.Recipients.Single().ReadAtUtc;

        // Idempotent: reading again does not move the timestamp.
        _clock.Advance(TimeSpan.FromSeconds(10));
        var again = await world.Mail.MarkReadAsync(message.Id, "session-child-0");
        Assert.Equal(readAt, again.Recipients.Single().ReadAtUtc);

        var acked = await world.Mail.AcknowledgeAsync(message.Id, "session-child-0");
        Assert.NotNull(acked.Recipients.Single().AcknowledgedAtUtc);
    }

    [Fact]
    public async Task Non_addressee_and_unknown_message_are_rejected()
    {
        var world = await CreateWorldAsync();
        var message = await world.Mail.SendAsync(Send("session-root", ["session-child-0"]));

        await Assert.ThrowsAsync<MailNotAddresseeException>(() =>
            world.Mail.MarkReadAsync(message.Id, "session-child-1"));
        await Assert.ThrowsAsync<MailMessageNotFoundException>(() =>
            world.Mail.MarkReadAsync("msg-does-not-exist", "session-child-0"));
    }

    [Fact]
    public async Task Reply_fans_out_to_every_other_thread_participant_and_thread_history_is_ordered()
    {
        var world = await CreateWorldAsync(1);
        var original = await world.Mail.SendAsync(
            Send("session-root", ["session-child-0"], threadId: "t-h", subject: "Reservation handoff requested"));
        _clock.Advance(TimeSpan.FromSeconds(5));

        var reply = await world.Mail.ReplyAsync(new ReplyAgentMessageCommand(
            world.ProjectId, "t-h", "session-child-0", "Denied — still editing.", MessageImportance.High, AckRequired: false));

        Assert.Equal("t-h", reply.ThreadId);
        Assert.Equal("session-child-0", reply.SenderSessionId);
        // Thread participants besides the replier: the root sender.
        var recipient = Assert.Single(reply.Recipients);
        Assert.Equal("session-root", recipient.SessionId);

        var thread = await world.Mail.GetThreadAsync(world.ProjectId, "t-h");
        Assert.Equal(2, thread.Count);
        Assert.Equal([original.Id, reply.Id], thread.Select(m => m.Id).ToList());
        Assert.All(thread, m => Assert.Equal("Reservation handoff requested", m.Subject));
    }

    [Fact]
    public async Task Reply_to_an_unknown_thread_is_rejected()
    {
        var world = await CreateWorldAsync();

        await Assert.ThrowsAsync<MailThreadNotFoundException>(() =>
            world.Mail.ReplyAsync(new ReplyAgentMessageCommand(
                world.ProjectId, "no-such-thread", "session-root", "hello", MessageImportance.Normal, false)));
    }

    [Fact]
    public async Task Mail_and_threads_are_scoped_per_project()
    {
        var world = await CreateWorldAsync();

        await world.Mail.SendAsync(Send("session-root", ["session-child-0"], threadId: "t-x"));

        // A different project id has no thread 't-x'.
        var otherProject = new ProjectId(Guid.NewGuid());
        Assert.Empty(await world.Mail.GetThreadAsync(otherProject, "t-x"));
        Assert.Empty(await world.Mail.GetUnreadAsync(otherProject, "session-child-0"));
    }

    [Fact]
    public async Task Agent_identities_are_unique_per_project_with_deterministic_suffixing()
    {
        var world = await CreateWorldAsync();

        var first = await world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
            world.ProjectId, "session-root", "root", "root", "pi"));
        var second = await world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
            world.ProjectId, "session-child-0", "root", "implementer", "pi"));

        Assert.Equal("root", first.AgentName);
        Assert.Equal("root-2", second.AgentName);
        // Resolution by name finds the current holder only.
        var found = await world.Identity.FindByNameAsync(world.ProjectId, "root");
        Assert.Equal("session-root", found!.SessionId);
        var suffixed = await world.Identity.FindByNameAsync(world.ProjectId, "root-2");
        Assert.Equal("session-child-0", suffixed!.SessionId);
    }

    [Fact]
    public async Task Identity_allocation_is_idempotent_per_session_and_releases_allow_reuse()
    {
        var world = await CreateWorldAsync(2);

        var allocated = await world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
            world.ProjectId, "session-child-0", "tester", "implementer", "pi"));
        var again = await world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
            world.ProjectId, "session-child-0", "different-requested", "implementer", "pi"));
        Assert.Equal(allocated.AgentName, again.AgentName);

        // The same name is fine in a different project (uniqueness is project-scoped).
        var otherProject = TestNodes.SeedProject(world.Db, _clock);
        await TestNodes.SaveAsync(world.Db);
        var elsewhere = await world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
            otherProject.Id, "session-other", "tester", "implementer", "pi"));
        Assert.Equal("tester", elsewhere.AgentName);

        // A session cannot hold identities in two projects.
        await Assert.ThrowsAsync<MailValidationException>(() =>
            world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
                otherProject.Id, "session-child-0", "tester", "implementer", "pi")));

        await world.Identity.ReleaseAsync("session-child-0");
        Assert.Null(await world.Identity.FindByNameAsync(world.ProjectId, "tester"));

        // Released names can be re-allocated by another session.
        var reused = await world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(
            world.ProjectId, "session-child-1", "tester", "implementer", "pi"));
        Assert.Equal("tester", reused.AgentName);
    }

    [Fact]
    public async Task Identity_allocation_rejects_blank_fields()
    {
        var world = await CreateWorldAsync();

        await Assert.ThrowsAsync<MailValidationException>(() =>
            world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(world.ProjectId, " ", "name", "role", "pi")));
        await Assert.ThrowsAsync<MailValidationException>(() =>
            world.Identity.AllocateAsync(new AllocateAgentIdentityCommand(world.ProjectId, "session-child-0", "", "role", "pi")));
        Assert.Null(await world.Identity.FindByNameAsync(world.ProjectId, "missing"));
    }

    private sealed record MailWorld(
        ControlPlaneDbContext Db,
        MailService Mail,
        AgentIdentityRegistry Identity,
        ProjectId ProjectId,
        WorkRequestId RequestId);
}
