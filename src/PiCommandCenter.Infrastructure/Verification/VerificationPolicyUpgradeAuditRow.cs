using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

/// <summary>Idempotent per-project record of historical default-profile auto-selection.</summary>
public sealed class VerificationPolicyUpgradeAuditRow
{
    public const int MaxReasonLength = 256;
    public const int MaxProfileIdLength = 128;
    public const int MaxProfileRevisionLength = 128;

    public ProjectId ProjectId { get; init; }

    public string ProfileId { get; init; } = string.Empty;

    public string ProfileRevision { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public long MigratedAtUtcTicks { get; init; }
}

/// <summary>Maps <see cref="VerificationPolicyUpgradeAuditRow"/>; unique on project so repeats are no-ops.</summary>
public sealed class VerificationPolicyUpgradeAuditRowConfiguration
    : IEntityTypeConfiguration<VerificationPolicyUpgradeAuditRow>
{
    public void Configure(EntityTypeBuilder<VerificationPolicyUpgradeAuditRow> builder)
    {
        builder.ToTable("VerificationPolicyUpgradeAudits");

        builder.HasKey(row => row.ProjectId);
        builder.Property(row => row.ProjectId).HasColumnType("TEXT");
        builder.Property(row => row.ProfileId)
            .IsRequired()
            .HasMaxLength(VerificationPolicyUpgradeAuditRow.MaxProfileIdLength);
        builder.Property(row => row.ProfileRevision)
            .IsRequired()
            .HasMaxLength(VerificationPolicyUpgradeAuditRow.MaxProfileRevisionLength);
        builder.Property(row => row.Reason)
            .IsRequired()
            .HasMaxLength(VerificationPolicyUpgradeAuditRow.MaxReasonLength);
        builder.Property(row => row.MigratedAtUtcTicks).HasColumnType("INTEGER");

        builder.HasIndex(row => row.ProjectId)
            .IsUnique()
            .HasDatabaseName("IX_VerificationPolicyUpgradeAudits_ProjectId");
    }
}
