using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PiCommandCenter.Infrastructure.Mail;

namespace PiCommandCenter.Infrastructure.Persistence;

/// <summary>
/// EF mappings for the mail coordination store (SPEC §16). The (ProjectId, AgentName) unique
/// index makes concurrent identity allocation race-safe: the loser of the index insert retries
/// with the next deterministic name.
/// </summary>
public sealed class MailMessageConfiguration : IEntityTypeConfiguration<MailMessageRow>
{
    public void Configure(EntityTypeBuilder<MailMessageRow> builder)
    {
        builder.ToTable("MailMessages");

        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnType("TEXT").HasMaxLength(64);

        builder.Property(m => m.ProjectId).HasColumnType("TEXT");
        builder.Property(m => m.RequestId).HasColumnType("TEXT");
        builder.Property(m => m.ThreadId).IsRequired().HasMaxLength(128).HasColumnType("TEXT");
        builder.Property(m => m.SenderSessionId).HasMaxLength(128).HasColumnType("TEXT");
        builder.Property(m => m.Subject).IsRequired().HasMaxLength(256);
        builder.Property(m => m.BodyMarkdown).IsRequired();
        builder.Property(m => m.Importance).IsRequired().HasMaxLength(16).HasColumnType("TEXT");
        builder.Property(m => m.AcknowledgementRequired).HasColumnType("INTEGER");
        builder.Property(m => m.CreatedAtUtcTicks).HasColumnType("INTEGER");

        builder.HasIndex(m => new { m.ProjectId, m.ThreadId, m.CreatedAtUtcTicks })
            .HasDatabaseName("IX_MailMessages_Thread");

        builder.HasMany(m => m.Recipients)
            .WithOne(r => r.Message)
            .HasForeignKey(r => r.MessageId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class MailRecipientConfiguration : IEntityTypeConfiguration<MailRecipientRow>
{
    public void Configure(EntityTypeBuilder<MailRecipientRow> builder)
    {
        builder.ToTable("MailRecipients");

        builder.HasKey(r => new { r.MessageId, r.SessionId });
        builder.Property(r => r.MessageId).HasColumnType("TEXT").HasMaxLength(64);
        builder.Property(r => r.SessionId).IsRequired().HasMaxLength(128).HasColumnType("TEXT");
        builder.Property(r => r.ReadAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(r => r.AcknowledgedAtUtcTicks).HasColumnType("INTEGER");

        builder.HasIndex(r => new { r.SessionId, r.ReadAtUtcTicks })
            .HasDatabaseName("IX_MailRecipients_Inbox");
    }
}

public sealed class MailAgentIdentityConfiguration : IEntityTypeConfiguration<MailAgentIdentityRow>
{
    public void Configure(EntityTypeBuilder<MailAgentIdentityRow> builder)
    {
        builder.ToTable("MailAgentIdentities");

        builder.HasKey(i => i.SessionId);
        builder.Property(i => i.SessionId).HasColumnType("TEXT").HasMaxLength(128);
        builder.Property(i => i.ProjectId).HasColumnType("TEXT");
        builder.Property(i => i.AgentName).IsRequired().HasMaxLength(128).HasColumnType("TEXT");
        builder.Property(i => i.Role).IsRequired().HasMaxLength(64);
        builder.Property(i => i.Runtime).IsRequired().HasMaxLength(64);
        builder.Property(i => i.AllocatedAtUtcTicks).HasColumnType("INTEGER");

        builder.HasIndex(i => new { i.ProjectId, i.AgentName })
            .IsUnique()
            .HasDatabaseName("IX_MailAgentIdentities_ProjectName");
    }
}
