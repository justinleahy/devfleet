using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Infrastructure.Completion;

public sealed class PendingTerminalizationEntityConfiguration
    : IEntityTypeConfiguration<PendingTerminalizationRow>
{
    public void Configure(EntityTypeBuilder<PendingTerminalizationRow> builder)
    {
        builder.ToTable("PendingTerminalizations");

        builder.HasKey(r => r.RequestId);
        builder.Property(r => r.RequestId).HasColumnType("TEXT");
        builder.Property(r => r.ProjectId).HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.NodeId).HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.ClaimToken)
            .IsRequired()
            .HasMaxLength(PendingTerminalizationRow.MaxClaimTokenLength);
        builder.Property(r => r.RootSessionId)
            .HasMaxLength(PendingTerminalizationRow.MaxRootSessionIdLength);
        builder.Property(r => r.Intent)
            .IsRequired()
            .HasMaxLength(PendingTerminalizationRow.MaxIntentLength);
        builder.Property(r => r.CompletionEvidenceJson)
            .HasMaxLength(PendingTerminalizationRow.MaxCompletionEvidenceJsonLength);
        builder.Property(r => r.Reason)
            .HasMaxLength(PendingTerminalizationRow.MaxReasonLength);
        builder.Property(r => r.AcceptedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(r => r.Version)
            .IsConcurrencyToken()
            .HasColumnType("INTEGER");

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(r => r.ProjectId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
        builder.HasOne<WorkRequest>()
            .WithMany()
            .HasForeignKey(r => r.RequestId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();

        builder.HasIndex(r => r.NodeId)
            .HasDatabaseName("IX_PendingTerminalizations_NodeId");
    }
}
