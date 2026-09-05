using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace PiCommandCenter.Infrastructure.Reservations;

/// <summary>
/// EF Core mappings for the reservation authority tables. Applied via
/// ApplyConfigurationsFromAssembly; reservation code never edits the DbContext directly.
/// </summary>
public sealed class ReservationLeaseRowConfiguration : IEntityTypeConfiguration<ReservationLeaseRow>
{
    public void Configure(EntityTypeBuilder<ReservationLeaseRow> builder)
    {
        builder.ToTable("ReservationLeases");

        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnType("TEXT");
        builder.Property(l => l.ProjectId).HasColumnType("TEXT");
        builder.Property(l => l.RequestId).HasColumnType("TEXT");

        builder.Property(l => l.OwnerSessionId)
            .IsRequired()
            .HasMaxLength(128);
        builder.Property(l => l.Reason)
            .IsRequired()
            .HasMaxLength(1024);
        builder.Property(l => l.State)
            .IsRequired()
            .HasMaxLength(32);
        builder.Property(l => l.FencingToken).HasColumnType("INTEGER");
        builder.Property(l => l.AcquiredAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(l => l.LastRenewedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(l => l.ExpiresAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(l => l.ReleasedAtUtcTicks).HasColumnType("INTEGER");
        builder.Property(l => l.Version)
            .IsConcurrencyToken()
            .HasColumnType("INTEGER");

        builder.HasMany(l => l.Scopes)
            .WithOne()
            .HasForeignKey(s => s.LeaseId)
            .OnDelete(DeleteBehavior.Cascade);

        // Conflict checks and expiration sweeps resolve leases per project.
        builder.HasIndex(l => new { l.ProjectId, l.State, l.ExpiresAtUtcTicks })
            .HasDatabaseName("IX_ReservationLeases_ProjectId_State_ExpiresAt");
        builder.HasIndex(l => l.OwnerSessionId)
            .HasDatabaseName("IX_ReservationLeases_OwnerSessionId");
    }
}

public sealed class ReservationScopeRowConfiguration : IEntityTypeConfiguration<ReservationScopeRow>
{
    public void Configure(EntityTypeBuilder<ReservationScopeRow> builder)
    {
        builder.ToTable("ReservationScopes");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnType("TEXT");
        builder.Property(s => s.LeaseId).HasColumnType("TEXT");
        builder.Property(s => s.Kind).HasColumnType("INTEGER");
        builder.Property(s => s.Path)
            .IsRequired()
            .HasMaxLength(PiCommandCenter.Domain.Reservations.ReservationScope.MaxPathLength);

        // Conflict checks only ever compare scopes of active leases in one project.
        builder.HasIndex(s => new { s.LeaseId })
            .HasDatabaseName("IX_ReservationScopes_LeaseId");
    }
}

public sealed class ProjectFencingTokenRowConfiguration : IEntityTypeConfiguration<ProjectFencingTokenRow>
{
    public void Configure(EntityTypeBuilder<ProjectFencingTokenRow> builder)
    {
        builder.ToTable("ProjectFencingTokens");

        builder.HasKey(t => t.ProjectId);
        builder.Property(t => t.ProjectId).HasColumnType("TEXT");
        builder.Property(t => t.LastFencingToken).HasColumnType("INTEGER");
    }
}

public sealed class ReservationAuditFactRowConfiguration : IEntityTypeConfiguration<ReservationAuditFactRow>
{
    public void Configure(EntityTypeBuilder<ReservationAuditFactRow> builder)
    {
        builder.ToTable("ReservationAuditFacts");

        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnType("TEXT");
        builder.Property(f => f.LeaseId).HasColumnType("TEXT");
        builder.Property(f => f.ProjectId).HasColumnType("TEXT");
        builder.Property(f => f.Kind)
            .IsRequired()
            .HasMaxLength(32);
        builder.Property(f => f.Reason)
            .IsRequired()
            .HasMaxLength(2048);
        builder.Property(f => f.RepositoryStatusSnapshot)
            .HasMaxLength(16384);
        builder.Property(f => f.Actor)
            .HasMaxLength(256);
        builder.Property(f => f.AtUtcTicks).HasColumnType("INTEGER");

        builder.HasIndex(f => new { f.LeaseId, f.AtUtcTicks })
            .HasDatabaseName("IX_ReservationAuditFacts_LeaseId_At");
    }
}
