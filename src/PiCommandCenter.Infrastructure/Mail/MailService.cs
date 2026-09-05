using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Mail;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Mail;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Mail;

/// <summary>
/// EF Core implementation of <see cref="IMessageService"/>. Every send validates that sender
/// and all recipients are active sessions of the command's project AND request, so mail can
/// never cross a project or request boundary. Read and acknowledgement state is per recipient.
/// </summary>
public sealed class MailService(TimeProvider clock, ControlPlaneDbContext db) : IMessageService
{
    private const int MaxSubjectLength = 256;
    private const int MaxSessionIdLength = 128;
    private const int MaxThreadIdLength = 128;

    public async Task<AgentMessageDto> SendAsync(SendAgentMessageCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateText(command.ThreadId, MaxThreadIdLength, "ThreadId");
        ValidateText(command.Subject, MaxSubjectLength, "Subject");
        ValidateText(command.BodyMarkdown, int.MaxValue, "BodyMarkdown");
        var recipients = ValidateRecipients(command.Recipients);

        var sender = await ResolveParticipantAsync(command.ProjectId, command.RequestId, command.SenderSessionId, cancellationToken);
        await ResolveRecipientsAsync(command.ProjectId, command.RequestId, recipients, cancellationToken);

        var row = new MailMessageRow
        {
            Id = NewMessageId(),
            ProjectId = command.ProjectId.Value,
            RequestId = command.RequestId.Value,
            ThreadId = command.ThreadId,
            SenderSessionId = sender,
            Subject = command.Subject,
            BodyMarkdown = command.BodyMarkdown,
            Importance = command.Importance.ToString(),
            AcknowledgementRequired = command.AckRequired,
            CreatedAtUtcTicks = clock.GetUtcNow().UtcTicks,
            Recipients = recipients
                .Select(sessionId => new MailRecipientRow { SessionId = sessionId })
                .ToList(),
        };

        db.Set<MailMessageRow>().Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task<AgentMessageDto> ReplyAsync(ReplyAgentMessageCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateText(command.ThreadId, MaxThreadIdLength, "ThreadId");
        ValidateText(command.BodyMarkdown, int.MaxValue, "BodyMarkdown");

        var threadRows = await db.Set<MailMessageRow>()
            .Include(m => m.Recipients)
            .Where(m => m.ProjectId == command.ProjectId.Value && m.ThreadId == command.ThreadId)
            .OrderBy(m => m.CreatedAtUtcTicks)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        if (threadRows.Count == 0)
        {
            throw new MailThreadNotFoundException(command.ThreadId);
        }

        var requestId = threadRows[0].RequestId;
        var sender = await ResolveParticipantAsync(command.ProjectId, new WorkRequestId(requestId), command.SenderSessionId, cancellationToken)
            ?? throw new MailSessionNotFoundException(command.SenderSessionId);

        // Recipients are the other thread participants: prior senders and addressees.
        var recipients = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in threadRows)
        {
            if (row.SenderSessionId is { } priorSender)
            {
                recipients.Add(priorSender);
            }

            foreach (var recipient in row.Recipients)
            {
                recipients.Add(recipient.SessionId);
            }
        }

        recipients.Remove(sender);
        if (recipients.Count == 0)
        {
            throw new MailValidationException($"Thread '{command.ThreadId}' has no participants besides the replying session.");
        }

        await ResolveRecipientsAsync(command.ProjectId, new WorkRequestId(requestId), recipients, cancellationToken);

        var reply = new MailMessageRow
        {
            Id = NewMessageId(),
            ProjectId = command.ProjectId.Value,
            RequestId = requestId,
            ThreadId = command.ThreadId,
            SenderSessionId = sender,
            Subject = threadRows[0].Subject,
            BodyMarkdown = command.BodyMarkdown,
            Importance = command.Importance.ToString(),
            AcknowledgementRequired = command.AckRequired,
            CreatedAtUtcTicks = clock.GetUtcNow().UtcTicks,
            Recipients = recipients
                .Select(sessionId => new MailRecipientRow { SessionId = sessionId })
                .ToList(),
        };

        db.Set<MailMessageRow>().Add(reply);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(reply);
    }

    public async Task<IReadOnlyList<AgentMessageDto>> GetUnreadAsync(
        ProjectId projectId,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ValidateSessionId(sessionId);
        var rows = await db.Set<MailMessageRow>()
            .Include(m => m.Recipients)
            .Where(m => m.ProjectId == projectId.Value
                && m.Recipients.Any(r => r.SessionId == sessionId && r.ReadAtUtcTicks == null))
            .OrderBy(m => m.CreatedAtUtcTicks)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<AgentMessageDto>> GetThreadAsync(
        ProjectId projectId,
        string threadId,
        CancellationToken cancellationToken = default)
    {
        ValidateText(threadId, MaxThreadIdLength, "ThreadId");
        var rows = await db.Set<MailMessageRow>()
            .Include(m => m.Recipients)
            .Where(m => m.ProjectId == projectId.Value && m.ThreadId == threadId)
            .OrderBy(m => m.CreatedAtUtcTicks)
            .ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        return rows.Select(ToDto).ToList();
    }

    public async Task<AgentMessageDto> MarkReadAsync(string messageId, string sessionId, CancellationToken cancellationToken = default)
    {
        var recipient = await ResolveAddresseeAsync(messageId, sessionId, cancellationToken);
        recipient.ReadAtUtcTicks ??= clock.GetUtcNow().UtcTicks;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(recipient.Message);
    }

    public async Task<AgentMessageDto> AcknowledgeAsync(string messageId, string sessionId, CancellationToken cancellationToken = default)
    {
        var recipient = await ResolveAddresseeAsync(messageId, sessionId, cancellationToken);
        if (recipient.ReadAtUtcTicks is null)
        {
            throw new MailAcknowledgementRequiresReadException(messageId, sessionId);
        }

        recipient.AcknowledgedAtUtcTicks ??= clock.GetUtcNow().UtcTicks;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(recipient.Message);
    }

    private async Task<string?> ResolveParticipantAsync(
        ProjectId projectId,
        WorkRequestId requestId,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        if (sessionId is null)
        {
            // Human guidance: no session, recorded as human-originated.
            return null;
        }

        ValidateSessionId(sessionId);
        await RequireSessionAsync(projectId, requestId, sessionId, cancellationToken);
        return sessionId;
    }

    private async Task RequireSessionAsync(ProjectId projectId, WorkRequestId requestId, string sessionId, CancellationToken cancellationToken)
    {
        var sessionGuid = requestId.Value;
        var exists = await db.AgentSessions
            .AnyAsync(
                s => s.Id == sessionId && s.ProjectId == projectId.Value && s.RequestId == sessionGuid,
                cancellationToken);
        if (!exists)
        {
            throw new MailSessionNotFoundException(sessionId);
        }
    }

    private static List<string> ValidateRecipients(IReadOnlyList<string> recipients)
    {
        if (recipients is { Count: > 0 } && recipients.All(r => !string.IsNullOrWhiteSpace(r)))
        {
            var distinct = recipients
                .Select(r => r.Trim())
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (distinct.Count != recipients.Count)
            {
                var duplicates = recipients
                    .GroupBy(r => r.Trim(), StringComparer.Ordinal)
                    .Where(g => g.Count() > 1)
                    .Select(g => g.Key)
                    .ToList();
                throw new MailValidationException($"Recipient list contains duplicates: {string.Join(", ", duplicates)}.");
            }

            foreach (var recipient in distinct)
            {
                if (recipient.Length > MaxSessionIdLength)
                {
                    throw new MailValidationException($"Recipient session id exceeds {MaxSessionIdLength} characters.");
                }
            }

            return distinct;
        }

        throw new MailValidationException("At least one non-empty recipient is required.");
    }

    private async Task ResolveRecipientsAsync(ProjectId projectId, WorkRequestId requestId, IReadOnlyCollection<string> recipients, CancellationToken cancellationToken)
    {
        foreach (var sessionId in recipients)
        {
            await RequireSessionAsync(projectId, requestId, sessionId, cancellationToken);
        }
    }

    private async Task<MailRecipientRow> ResolveAddresseeAsync(string messageId, string sessionId, CancellationToken cancellationToken)
    {
        ValidateSessionId(sessionId);
        var recipient = await db.Set<MailRecipientRow>()
            .Include(r => r.Message)
            .ThenInclude(m => m.Recipients)
            .SingleOrDefaultAsync(
                r => r.MessageId == messageId && r.SessionId == sessionId,
                cancellationToken);
        if (recipient is null)
        {
            var exists = await db.Set<MailMessageRow>().AnyAsync(m => m.Id == messageId, cancellationToken);
            throw exists
                ? new MailNotAddresseeException(sessionId, messageId)
                : new MailMessageNotFoundException(messageId);
        }

        return recipient;
    }

    private static void ValidateSessionId(string sessionId)
    {
        ValidateText(sessionId, MaxSessionIdLength, "SessionId");
    }

    private static void ValidateText(string value, int maxLength, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new MailValidationException($"{fieldName} must not be empty.");
        }

        if (value.Length > maxLength)
        {
            throw new MailValidationException($"{fieldName} exceeds {maxLength} characters.");
        }
    }

    private static string NewMessageId() => $"msg-{Guid.NewGuid():N}";

    private static AgentMessageDto ToDto(MailMessageRow row) => new(
        row.Id,
        new ProjectId(row.ProjectId),
        new WorkRequestId(row.RequestId),
        row.ThreadId,
        row.SenderSessionId,
        IsFromHuman: row.SenderSessionId is null,
        row.Recipients
            .OrderBy(r => r.SessionId, StringComparer.Ordinal)
            .Select(r => new AgentMessageRecipientDto(
                r.SessionId,
                r.ReadAtUtcTicks is { } read ? new DateTimeOffset(read, TimeSpan.Zero) : null,
                r.AcknowledgedAtUtcTicks is { } ack ? new DateTimeOffset(ack, TimeSpan.Zero) : null))
            .ToList(),
        row.Subject,
        row.BodyMarkdown,
        Enum.Parse<MessageImportance>(row.Importance),
        row.AcknowledgementRequired,
        new DateTimeOffset(row.CreatedAtUtcTicks, TimeSpan.Zero));
}
