using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Infrastructure.Persistence;

/// <summary>
/// EF Core SQLite context for the control plane. Strongly-typed identifiers are stored as
/// Guids, enumerations as short text codes, and aggregates carry explicit optimistic
/// concurrency tokens (<c>Version</c>).
/// </summary>
public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options)
    : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<WorkRequest> WorkRequests => Set<WorkRequest>();

    public DbSet<FleetNode> FleetNodes => Set<FleetNode>();

    public DbSet<RequestClaim> RequestClaims => Set<RequestClaim>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<ProjectId>().HaveConversion<ProjectIdConverter>();
        configurationBuilder.Properties<WorkRequestId>().HaveConversion<WorkRequestIdConverter>();
        configurationBuilder.Properties<NodeId>().HaveConversion<NodeIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<Project>(project =>
        {
            project.ToTable("Projects");

            project.HasKey(p => p.Id);
            project.Property(p => p.Id)
                .HasConversion(id => id.Value, value => new ProjectId(value))
                .HasColumnType("TEXT");

            project.Property(p => p.NodeId)
                .HasColumnType("TEXT");

            project.Property(p => p.DisplayName)
                .IsRequired()
                .HasMaxLength(256);

            project.Property(p => p.RepositoryPath)
                .IsRequired()
                .HasMaxLength(1024);

            project.Property(p => p.DefaultBranch)
                .IsRequired()
                .HasMaxLength(128);

            project.Property(p => p.CreatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            project.Property(p => p.UpdatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");

            project.Property(p => p.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            project.HasIndex(p => p.RepositoryPath)
                .IsUnique()
                .HasDatabaseName("IX_Projects_RepositoryPath");
        });

        builder.Entity<WorkRequest>(request =>
        {
            request.ToTable("WorkRequests");

            request.HasKey(r => r.Id);
            request.Property(r => r.Id)
                .HasConversion(id => id.Value, value => new WorkRequestId(value))
                .HasColumnType("TEXT");

            request.Property(r => r.ProjectId)
                .HasConversion(id => id.Value, value => new ProjectId(value))
                .HasColumnType("TEXT");

            request.Property(r => r.Kind)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasColumnType("TEXT")
                .IsRequired();

            request.Property(r => r.Priority)
                .HasConversion(priority => (int)priority, value => (RequestPriority)value)
                .HasColumnType("INTEGER")
                .IsRequired();

            request.Property(r => r.RiskLevel)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasColumnType("TEXT")
                .IsRequired();

            request.Property(r => r.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasColumnType("TEXT")
                .IsRequired();

            request.Property(r => r.BlockedPhase)
                .HasConversion<string?>(
                    phase => phase.HasValue ? phase.Value.ToString() : null,
                    value => string.IsNullOrEmpty(value) ? null : Enum.Parse<WorkRequestStatus>(value))
                .HasMaxLength(32)
                .HasColumnType("TEXT");

            request.Property(r => r.Title)
                .IsRequired()
                .HasMaxLength(256);

            request.Property(r => r.Prompt)
                .IsRequired()
                .HasMaxLength(8192);

            request.Property(r => r.CreatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            request.Property(r => r.UpdatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");

            request.Property(r => r.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            request.HasOne<Project>()
                .WithMany()
                .HasForeignKey(r => r.ProjectId)
                .HasPrincipalKey(p => p.Id)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();

            request.HasIndex(r => r.ProjectId)
                .HasDatabaseName("IX_WorkRequests_ProjectId");

            // Serves queue ordering: Priority DESC, CreatedAt ASC (SQLite scans backward).
            request.HasIndex(r => new { r.Priority, r.CreatedAt })
                .HasDatabaseName("IX_WorkRequests_Priority_CreatedAt");
        });

        builder.Entity<FleetNode>(node =>
        {
            node.ToTable("FleetNodes");

            node.HasKey(n => n.Id);
            node.Property(n => n.Id)
                .HasConversion(id => id.Value, value => new NodeId(value))
                .HasColumnType("TEXT");

            node.Property(n => n.DisplayName)
                .IsRequired()
                .HasMaxLength(256);

            node.Property(n => n.AgentVersion)
                .IsRequired()
                .HasMaxLength(64);

            node.Property(n => n.Status)
                .HasConversion<string>()
                .HasMaxLength(16)
                .HasColumnType("TEXT")
                .IsRequired();

            node.Property(n => n.LastHeartbeatAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            node.Property(n => n.CapabilitiesJson)
                .IsRequired()
                .HasMaxLength(16384);

            node.Property(n => n.CreatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            node.Property(n => n.UpdatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");

            node.Property(n => n.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            node.HasIndex(n => n.DisplayName)
                .HasDatabaseName("IX_FleetNodes_DisplayName");
        });

        builder.Entity<RequestClaim>(claim =>
        {
            claim.ToTable("RequestClaims");

            // Primary key on the request id: at most one claim can ever exist per request.
            claim.HasKey(c => c.RequestId);
            claim.Property(c => c.RequestId)
                .HasConversion(id => id.Value, value => new WorkRequestId(value))
                .HasColumnType("TEXT");

            claim.Property(c => c.ProjectId)
                .HasConversion(id => id.Value, value => new ProjectId(value))
                .HasColumnType("TEXT");

            claim.Property(c => c.NodeId)
                .HasConversion(id => id.Value, value => new NodeId(value))
                .HasColumnType("TEXT");

            claim.Property(c => c.ClaimToken)
                .IsRequired()
                .HasMaxLength(128);

            claim.Property(c => c.ClaimedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            claim.Property(c => c.LeaseExpiresAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");

            claim.Property(c => c.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            claim.HasOne<WorkRequest>()
                .WithOne()
                .HasPrincipalKey<WorkRequest>(r => r.Id)
                .HasForeignKey<RequestClaim>(c => c.RequestId)
                .OnDelete(DeleteBehavior.Cascade);

            claim.HasIndex(c => c.RequestId)
                .IsUnique()
                .HasDatabaseName("IX_RequestClaims_RequestId");

            // Capacity checks look up active (unexpired) claims per project and node.
            claim.HasIndex(c => new { c.ProjectId, c.LeaseExpiresAt })
                .HasDatabaseName("IX_RequestClaims_ProjectId_LeaseExpiresAt");
        });

        builder.Entity<SessionEvent>(sessionEvent =>
        {
            sessionEvent.ToTable("SessionEvents");

            // Primary key on the globally unique event id backs batch idempotency.
            sessionEvent.HasKey(e => e.EventId);
            sessionEvent.Property(e => e.EventId)
                .HasColumnType("TEXT")
                .HasMaxLength(64);

            sessionEvent.Property(e => e.NodeId).HasColumnType("TEXT");
            sessionEvent.Property(e => e.ProjectId).HasColumnType("TEXT");
            sessionEvent.Property(e => e.RequestId).HasColumnType("TEXT");
            sessionEvent.Property(e => e.SessionId)
                .HasMaxLength(128);

            sessionEvent.Property(e => e.Sequence).HasColumnType("INTEGER");

            sessionEvent.Property(e => e.Type)
                .IsRequired()
                .HasMaxLength(64);

            sessionEvent.Property(e => e.OccurredAtUtcTicks).HasColumnType("INTEGER");
            sessionEvent.Property(e => e.ReceivedAtUtcTicks).HasColumnType("INTEGER");

            sessionEvent.Property(e => e.PayloadJson)
                .IsRequired()
                .HasMaxLength(65536);

            sessionEvent.HasIndex(e => new { e.ProjectId, e.OccurredAtUtcTicks })
                .HasDatabaseName("IX_SessionEvents_ProjectId_OccurredAtUtcTicks");
        });
    }

    private sealed class ProjectIdConverter : ValueConverter<ProjectId, Guid>
    {
        public ProjectIdConverter()
            : base(id => id.Value, value => new ProjectId(value))
        {
        }
    }

    private sealed class WorkRequestIdConverter : ValueConverter<WorkRequestId, Guid>
    {
        public WorkRequestIdConverter()
            : base(id => id.Value, value => new WorkRequestId(value))
        {
        }
    }

    private sealed class NodeIdConverter : ValueConverter<NodeId, Guid>
    {
        public NodeIdConverter()
            : base(id => id.Value, value => new NodeId(value))
        {
        }
    }
}
