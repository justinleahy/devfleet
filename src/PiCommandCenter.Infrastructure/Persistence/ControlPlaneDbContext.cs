using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Domain;
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
