using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PiCommandCenter.Infrastructure.Recovery;

/// <summary>
/// EF Core mappings for recovery tables. Applied via ApplyConfigurationsFromAssembly;
/// recovery code never edits the DbContext directly.
/// </summary>
public sealed class RecoveryOperationRowConfiguration : IEntityTypeConfiguration<RecoveryOperationRow>
{
    public void Configure(EntityTypeBuilder<RecoveryOperationRow> builder)
    {
        builder.ToTable("RecoveryOperations");

        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnType("TEXT");
        builder.Property(o => o.ProjectId).HasColumnType("TEXT").IsRequired();
        builder.Property(o => o.Status).IsRequired().HasMaxLength(32);
        builder.Property(o => o.Attempt).HasColumnType("INTEGER");
        builder.Property(o => o.InventoryRevision).IsRequired().HasMaxLength(64);
        builder.Property(o => o.Reason).IsRequired().HasMaxLength(1024);
        builder.Property(o => o.Actor).IsRequired().HasMaxLength(128);
        builder.Property(o => o.Stage).HasMaxLength(64);
        builder.Property(o => o.BlockerCodesJson).HasMaxLength(4096);
        builder.Property(o => o.EvidenceJson).HasMaxLength(16384);
        builder.Property(o => o.CreatedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(o => o.UpdatedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(o => o.CompletedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(o => o.DeadlineUtcTicks).HasColumnType("INTEGER");
        builder.Property(o => o.LastProgressUtcTicks).HasColumnType("INTEGER");
        builder.Property(o => o.Version)
            .IsConcurrencyToken()
            .HasColumnType("INTEGER");

        builder.HasMany(o => o.AssignmentTargets)
            .WithOne()
            .HasForeignKey(t => t.OperationId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(o => o.ReservationTargets)
            .WithOne()
            .HasForeignKey(t => t.OperationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => o.ProjectId)
            .IsUnique()
            .HasFilter("\"Status\" <> 'Recovered'")
            .HasDatabaseName("IX_RecoveryOperations_ProjectId_Unresolved");
        builder.HasIndex(o => new { o.ProjectId, o.CreatedAtUtcTicks })
            .HasDatabaseName("IX_RecoveryOperations_ProjectId_CreatedAt");
    }
}

public sealed class RecoveryTargetRowConfiguration : IEntityTypeConfiguration<RecoveryTargetRow>
{
    public void Configure(EntityTypeBuilder<RecoveryTargetRow> builder)
    {
        builder.ToTable("RecoveryTargets");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnType("TEXT");
        builder.Property(t => t.OperationId).HasColumnType("TEXT").IsRequired();
        builder.Property(t => t.RequestId).HasColumnType("TEXT").IsRequired();
        builder.Property(t => t.CapturedVersion).HasColumnType("INTEGER");
        builder.Property(t => t.CapturedState).IsRequired().HasMaxLength(32);
        builder.Property(t => t.BindingRevision).HasColumnType("INTEGER");
        builder.Property(t => t.Outcome).HasMaxLength(32);
        builder.Property(t => t.EvidenceJson).HasMaxLength(16384);

        builder.HasIndex(t => new { t.OperationId, t.RequestId })
            .IsUnique()
            .HasDatabaseName("IX_RecoveryTargets_OperationId_RequestId");
    }
}

public sealed class RecoveryReservationTargetRowConfiguration
    : IEntityTypeConfiguration<RecoveryReservationTargetRow>
{
    public void Configure(EntityTypeBuilder<RecoveryReservationTargetRow> builder)
    {
        builder.ToTable("RecoveryReservationTargets");

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnType("TEXT");
        builder.Property(t => t.OperationId).HasColumnType("TEXT").IsRequired();
        builder.Property(t => t.LeaseId).HasColumnType("TEXT").IsRequired();
        builder.Property(t => t.CapturedVersion).HasColumnType("INTEGER");
        builder.Property(t => t.CapturedState).IsRequired().HasMaxLength(32);
        builder.Property(t => t.Outcome).HasMaxLength(32);
        builder.Property(t => t.EvidenceJson).HasMaxLength(16384);

        builder.HasIndex(t => new { t.OperationId, t.LeaseId })
            .IsUnique()
            .HasDatabaseName("IX_RecoveryReservationTargets_OperationId_LeaseId");
    }
}

public sealed class RecoveryHoldRowConfiguration : IEntityTypeConfiguration<RecoveryHoldRow>
{
    public void Configure(EntityTypeBuilder<RecoveryHoldRow> builder)
    {
        builder.ToTable("RecoveryHolds");

        builder.HasKey(h => h.ProjectId);
        builder.Property(h => h.ProjectId).HasColumnType("TEXT");
        builder.Property(h => h.OperationId).HasColumnType("TEXT").IsRequired();
        builder.Property(h => h.EstablishedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(h => h.Version)
            .IsConcurrencyToken()
            .HasColumnType("INTEGER");

        builder.HasOne<RecoveryOperationRow>()
            .WithMany()
            .HasForeignKey(h => h.OperationId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();
    }
}

public sealed class RecoveryIdempotencyRowConfiguration : IEntityTypeConfiguration<RecoveryIdempotencyRow>
{
    public void Configure(EntityTypeBuilder<RecoveryIdempotencyRow> builder)
    {
        builder.ToTable("RecoveryIdempotencyKeys");

        builder.HasKey(k => new { k.ProjectId, k.Action, k.Key });
        builder.Property(k => k.ProjectId).HasColumnType("TEXT");
        builder.Property(k => k.Action).IsRequired().HasMaxLength(64);
        builder.Property(k => k.Key).IsRequired().HasMaxLength(128);
        builder.Property(k => k.InputHash).IsRequired().HasMaxLength(64);
        builder.Property(k => k.OperationId).HasColumnType("TEXT");
        builder.Property(k => k.CreatedAtUtcTicks).HasColumnType("INTEGER");

        builder.HasOne<RecoveryOperationRow>()
            .WithMany()
            .HasForeignKey(k => k.OperationId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class RecoveryAuditFactRowConfiguration : IEntityTypeConfiguration<RecoveryAuditFactRow>
{
    public void Configure(EntityTypeBuilder<RecoveryAuditFactRow> builder)
    {
        builder.ToTable("RecoveryAuditFacts");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnType("TEXT");
        builder.Property(f => f.OperationId).HasColumnType("TEXT").IsRequired();
        builder.Property(f => f.ProjectId).HasColumnType("TEXT").IsRequired();
        builder.Property(f => f.Kind).IsRequired().HasMaxLength(64);
        builder.Property(f => f.Reason).IsRequired().HasMaxLength(1024);
        builder.Property(f => f.Actor).HasMaxLength(128);
        builder.Property(f => f.PayloadJson).HasMaxLength(16384);
        builder.Property(f => f.AtUtcTicks).HasColumnType("INTEGER");

        builder.HasOne<RecoveryOperationRow>()
            .WithMany()
            .HasForeignKey(f => f.OperationId)
            .OnDelete(DeleteBehavior.Restrict)
            .IsRequired();

        builder.HasIndex(f => new { f.OperationId, f.AtUtcTicks })
            .HasDatabaseName("IX_RecoveryAuditFacts_OperationId_At");
    }
}
