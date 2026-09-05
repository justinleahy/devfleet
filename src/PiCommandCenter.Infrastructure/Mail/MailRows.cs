namespace PiCommandCenter.Infrastructure.Mail;

/// <summary>Persisted mail message; one row per sent or replied message (SPEC §16.4).</summary>
public sealed class MailMessageRow
{
    public string Id { get; init; } = string.Empty;

    public Guid ProjectId { get; init; }

    public Guid RequestId { get; init; }

    public string ThreadId { get; init; } = string.Empty;

    /// <summary>Null when the message was written by the human user from the browser.</summary>
    public string? SenderSessionId { get; init; }

    public string Subject { get; init; } = string.Empty;

    public string BodyMarkdown { get; init; } = string.Empty;

    public string Importance { get; init; } = string.Empty;

    public bool AcknowledgementRequired { get; init; }

    public long CreatedAtUtcTicks { get; init; }

    public List<MailRecipientRow> Recipients { get; init; } = [];
}

/// <summary>Per-recipient delivery state of one message.</summary>
public sealed class MailRecipientRow
{
    public string MessageId { get; init; } = string.Empty;

    public MailMessageRow Message { get; init; } = null!;

    public string SessionId { get; init; } = string.Empty;

    public long? ReadAtUtcTicks { get; set; }

    public long? AcknowledgedAtUtcTicks { get; set; }
}

/// <summary>Active, project-scoped agent identity; released identities are deleted.</summary>
public sealed class MailAgentIdentityRow
{
    public string SessionId { get; init; } = string.Empty;

    public Guid ProjectId { get; init; }

    public string AgentName { get; set; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public string Runtime { get; init; } = string.Empty;

    public long AllocatedAtUtcTicks { get; init; }
}
