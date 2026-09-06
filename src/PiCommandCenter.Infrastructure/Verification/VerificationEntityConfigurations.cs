using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PiCommandCenter.Infrastructure.Verification;

public sealed class VerificationRunRowConfiguration : IEntityTypeConfiguration<VerificationRunRow>
{
    public void Configure(EntityTypeBuilder<VerificationRunRow> builder)
    {
        builder.ToTable("VerificationRuns");

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnType("TEXT");
        builder.Property(r => r.RequestId).HasColumnType("TEXT");
        builder.Property(r => r.ProfileId).IsRequired().HasMaxLength(128);
        builder.Property(r => r.CommandId).IsRequired().HasMaxLength(128);
        builder.Property(r => r.Status).IsRequired().HasMaxLength(32);
        builder.Property(r => r.ExitCode).HasColumnType("INTEGER");
        builder.Property(r => r.StartedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(r => r.CompletedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(r => r.OutputSummary).HasMaxLength(16384);
        builder.Property(r => r.OutputArtifactPath).HasMaxLength(1024);
        builder.Property(r => r.Mandatory).HasColumnType("INTEGER");
        builder.Property(r => r.Fingerprint).IsRequired().HasMaxLength(256);
        builder.Property(r => r.PolicyRevision).IsRequired().HasMaxLength(128);
        builder.Property(r => r.RunKind).IsRequired().HasMaxLength(32);
        builder.Property(r => r.AttemptId).HasColumnType("TEXT");

        builder.HasIndex(r => r.RequestId).HasDatabaseName("IX_VerificationRuns_RequestId");
        builder.HasIndex(r => new
            {
                r.RequestId,
                r.Fingerprint,
                r.PolicyRevision,
                r.ProfileId,
                r.CommandId,
                r.RunKind,
            })
            .IsUnique()
            .HasFilter("\"RunKind\" <> 'Intermediate'")
            .HasDatabaseName("IX_VerificationRuns_FinalIdentity");
    }
}
