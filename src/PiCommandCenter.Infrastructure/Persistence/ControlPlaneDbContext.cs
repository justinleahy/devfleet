using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Requests;
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

    public DbSet<WorkspaceBinding> WorkspaceBindings => Set<WorkspaceBinding>();

    public DbSet<ExecutionAssignment> ExecutionAssignments => Set<ExecutionAssignment>();

    public DbSet<SessionEvent> SessionEvents => Set<SessionEvent>();
    public DbSet<AgentSessionRow> AgentSessions => Set<AgentSessionRow>();

    public DbSet<PiCommandCenter.Infrastructure.Reservations.ReservationLeaseRow> ReservationLeases => Set<PiCommandCenter.Infrastructure.Reservations.ReservationLeaseRow>();
    public DbSet<PiCommandCenter.Infrastructure.Reservations.ReservationScopeRow> ReservationScopes => Set<PiCommandCenter.Infrastructure.Reservations.ReservationScopeRow>();

    public DbSet<PiCommandCenter.Infrastructure.Reservations.ProjectFencingTokenRow> ProjectFencingTokens => Set<PiCommandCenter.Infrastructure.Reservations.ProjectFencingTokenRow>();

    public DbSet<PiCommandCenter.Infrastructure.Reservations.ReservationAuditFactRow> ReservationAuditFacts => Set<PiCommandCenter.Infrastructure.Reservations.ReservationAuditFactRow>();
    public DbSet<PiCommandCenter.Infrastructure.Verification.VerificationRunRow> VerificationRuns => Set<PiCommandCenter.Infrastructure.Verification.VerificationRunRow>();

    public DbSet<PiCommandCenter.Infrastructure.Completion.RequestResultRow> RequestResults => Set<PiCommandCenter.Infrastructure.Completion.RequestResultRow>();
    public DbSet<PiCommandCenter.Infrastructure.Completion.PendingTerminalizationRow> PendingTerminalizations => Set<PiCommandCenter.Infrastructure.Completion.PendingTerminalizationRow>();
    public DbSet<PiCommandCenter.Infrastructure.Recovery.RecoveryOperationRow> RecoveryOperations => Set<PiCommandCenter.Infrastructure.Recovery.RecoveryOperationRow>();
    public DbSet<PiCommandCenter.Infrastructure.Recovery.RecoveryHoldRow> RecoveryHolds => Set<PiCommandCenter.Infrastructure.Recovery.RecoveryHoldRow>();
    public DbSet<PiCommandCenter.Infrastructure.Recovery.RecoveryTargetRow> RecoveryTargets => Set<PiCommandCenter.Infrastructure.Recovery.RecoveryTargetRow>();
    public DbSet<PiCommandCenter.Infrastructure.Recovery.RecoveryReservationTargetRow> RecoveryReservationTargets => Set<PiCommandCenter.Infrastructure.Recovery.RecoveryReservationTargetRow>();
    public DbSet<PiCommandCenter.Infrastructure.Recovery.RecoveryIdempotencyRow> RecoveryIdempotencyKeys => Set<PiCommandCenter.Infrastructure.Recovery.RecoveryIdempotencyRow>();
    public DbSet<PiCommandCenter.Infrastructure.Recovery.RecoveryAuditFactRow> RecoveryAuditFacts => Set<PiCommandCenter.Infrastructure.Recovery.RecoveryAuditFactRow>();


    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<ProjectId>().HaveConversion<ProjectIdConverter>();
        configurationBuilder.Properties<WorkRequestId>().HaveConversion<WorkRequestIdConverter>();
        configurationBuilder.Properties<NodeId>().HaveConversion<NodeIdConverter>();
        configurationBuilder.Properties<WorkspaceBindingId>().HaveConversion<WorkspaceBindingIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // Mail, reservations, and other feature mappings live in their own
        // IEntityTypeConfiguration classes (Infrastructure/Mail, Infrastructure/Reservations).
        builder.ApplyConfigurationsFromAssembly(typeof(ControlPlaneDbContext).Assembly);
        builder.Entity<Project>(project =>
        {
            project.ToTable("Projects");

            project.HasKey(p => p.Id);
            project.Property(p => p.Id)
                .HasConversion(id => id.Value, value => new ProjectId(value))
                .HasColumnType("TEXT");

            project.Property(p => p.DisplayName)
                .IsRequired()
                .HasMaxLength(256);

            project.Property(p => p.DefaultBranch)
                .IsRequired()
                .HasMaxLength(128);
            project.Property(p => p.TrustedVerificationProfileId)
                .HasMaxLength(Project.MaxTrustedVerificationProfileIdLength);
            project.Property(p => p.TrustedVerificationProfileRevision)
                .HasMaxLength(Project.MaxTrustedVerificationProfileRevisionLength);

            project.Property(p => p.CreatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            project.Property(p => p.UpdatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");

            project.Property(p => p.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");
        });

        builder.Entity<WorkspaceBinding>(binding =>
        {
            binding.ToTable("WorkspaceBindings");

            binding.HasKey(b => b.Id);
            binding.Property(b => b.Id).HasColumnType("TEXT");
            binding.Property(b => b.ProjectId).HasColumnType("TEXT");
            binding.Property(b => b.NodeId).HasColumnType("TEXT");

            binding.Property(b => b.RepositoryPath)
                .IsRequired()
                .HasMaxLength(1024);
            binding.Property(b => b.CanonicalRepositoryPath)
                .HasMaxLength(1024);
            binding.Property(b => b.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasColumnType("TEXT")
                .IsRequired();
            binding.Property(b => b.ValidationRevision).HasColumnType("INTEGER");
            binding.Property(b => b.ValidationCode)
                .HasMaxLength(WorkspaceBinding.MaxValidationCodeLength);
            binding.Property(b => b.ValidationDetail)
                .HasMaxLength(WorkspaceBinding.MaxValidationDetailLength);
            binding.Property(b => b.ValidatedAt).HasConversion(
                timestamp => timestamp.HasValue ? timestamp.Value.UtcTicks : (long?)null,
                ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null)
                .HasColumnType("INTEGER");
            binding.Property(b => b.CreatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            binding.Property(b => b.UpdatedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            binding.Property(b => b.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            binding.HasOne<Project>()
                .WithOne()
                .HasForeignKey<WorkspaceBinding>(b => b.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            binding.HasOne<FleetNode>()
                .WithMany()
                .HasForeignKey(b => b.NodeId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();

            binding.HasIndex(b => b.ProjectId)
                .IsUnique()
                .HasDatabaseName("IX_WorkspaceBindings_ProjectId");
            binding.HasIndex(b => new { b.NodeId, b.RepositoryPath })
                .IsUnique()
                .HasDatabaseName("IX_WorkspaceBindings_NodeId_RepositoryPath");
            binding.HasIndex(b => new { b.NodeId, b.CanonicalRepositoryPath })
                .IsUnique()
                .HasDatabaseName("IX_WorkspaceBindings_NodeId_CanonicalRepositoryPath");
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

            request.Property(r => r.OriginalRequestId)
                .HasConversion(
                    id => id.HasValue ? id.Value.Value : (Guid?)null,
                    value => value.HasValue ? new WorkRequestId(value.Value) : null)
                .HasColumnType("TEXT");

            request.HasOne<WorkRequest>()
                .WithMany()
                .HasForeignKey(r => r.OriginalRequestId)
                .OnDelete(DeleteBehavior.Restrict);

            request.HasIndex(r => r.OriginalRequestId)
                .HasDatabaseName("IX_WorkRequests_OriginalRequestId");

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
            node.Property(n => n.ExecutionStatusJson)
                .HasColumnType("TEXT")
                .HasMaxLength(131072);
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

        builder.Entity<ExecutionAssignment>(assignment =>
        {
            assignment.ToTable("ExecutionAssignments");

            assignment.HasKey(a => a.RequestId);
            assignment.Property(a => a.RequestId).HasColumnType("TEXT");
            assignment.Property(a => a.ProjectId).HasColumnType("TEXT");
            assignment.Property(a => a.WorkspaceBindingId).HasColumnType("TEXT");
            assignment.Property(a => a.NodeIdSnapshot).HasColumnType("TEXT");

            assignment.Property(a => a.CanonicalRepositoryPathSnapshot)
                .IsRequired()
                .HasMaxLength(1024);
            assignment.Property(a => a.DefaultBranchSnapshot)
                .IsRequired()
                .HasMaxLength(128);
            assignment.Property(a => a.BindingValidationRevisionSnapshot)
                .HasColumnType("INTEGER");
            assignment.Property(a => a.VerificationPolicyRevision)
                .HasMaxLength(ExecutionAssignment.MaxVerificationPolicyRevisionLength);
            assignment.Property(a => a.BaselineVersion)
                .HasMaxLength(ExecutionAssignment.MaxBaselineVersionLength);
            assignment.Property(a => a.TrustedVerificationProfileId)
                .HasMaxLength(ExecutionAssignment.MaxTrustedVerificationProfileIdLength);
            assignment.Property(a => a.TrustedVerificationProfileRevision)
                .HasMaxLength(ExecutionAssignment.MaxTrustedVerificationProfileRevisionLength);
            assignment.Property(a => a.MandatoryCommandIdsJson)
                .HasMaxLength(ExecutionAssignment.MaxMandatoryCommandIdsJsonLength);
            assignment.Ignore(a => a.HasCapturedVerificationPolicy);
            assignment.Property(a => a.State)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasColumnType("TEXT")
                .IsRequired();
            assignment.Property(a => a.ClaimToken)
                .IsRequired()
                .HasMaxLength(128);
            assignment.Property(a => a.AssignedAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            assignment.Property(a => a.LeaseExpiresAt).HasConversion(
                timestamp => timestamp.UtcTicks,
                ticks => new DateTimeOffset(ticks, TimeSpan.Zero)).HasColumnType("INTEGER");
            assignment.Property(a => a.LastRenewedAt).HasConversion(
                timestamp => timestamp.HasValue ? timestamp.Value.UtcTicks : (long?)null,
                ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null)
                .HasColumnType("INTEGER");
            assignment.Property(a => a.LastReconciledAt).HasConversion(
                timestamp => timestamp.HasValue ? timestamp.Value.UtcTicks : (long?)null,
                ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null)
                .HasColumnType("INTEGER");
            assignment.Property(a => a.TerminalAt).HasConversion(
                timestamp => timestamp.HasValue ? timestamp.Value.UtcTicks : (long?)null,
                ticks => ticks.HasValue ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null)
                .HasColumnType("INTEGER");
            assignment.Property(a => a.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            assignment.HasOne<WorkRequest>()
                .WithOne()
                .HasForeignKey<ExecutionAssignment>(a => a.RequestId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            assignment.HasOne<Project>()
                .WithMany()
                .HasForeignKey(a => a.ProjectId)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();
            assignment.HasOne<FleetNode>()
                .WithMany()
                .HasForeignKey(a => a.NodeIdSnapshot)
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();

            assignment.HasIndex(a => new { a.NodeIdSnapshot, a.State })
                .HasDatabaseName("IX_ExecutionAssignments_NodeIdSnapshot_State");
            assignment.HasIndex(a => new { a.ProjectId, a.State })
                .HasDatabaseName("IX_ExecutionAssignments_ProjectId_State");
            assignment.HasIndex(a => a.WorkspaceBindingId)
                .HasDatabaseName("IX_ExecutionAssignments_WorkspaceBindingId");
        });

        builder.Entity<SessionEvent>(sessionEvent =>
        {
            sessionEvent.ToTable("SessionEvents");

            // Primary key on the globally unique event id backs batch idempotency.
            sessionEvent.HasKey(e => e.EventId);
            sessionEvent.Property(e => e.EventId)
                .HasColumnType("TEXT")
                .HasMaxLength(AssignmentOperationLimits.MaxEventIdLength);

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

        builder.Entity<AgentSessionRow>(session =>
        {
            session.ToTable("AgentSessions");

            // The session id is globally unique: the primary key doubles as the uniqueness
            // constraint required for upsert-on-registration.
            session.HasKey(s => s.Id);
            session.Property(s => s.Id)
                .HasMaxLength(128);

            session.Property(s => s.ProjectId).HasColumnType("TEXT");
            session.Property(s => s.RequestId).HasColumnType("TEXT");
            session.Property(s => s.ParentSessionId)
                .HasMaxLength(128);

            session.Property(s => s.AgentName)
                .IsRequired()
                .HasMaxLength(256);
            session.Property(s => s.Role)
                .IsRequired()
                .HasMaxLength(64);
            session.Property(s => s.Runtime)
                .IsRequired()
                .HasMaxLength(64);
            session.Property(s => s.Model)
                .IsRequired()
                .HasMaxLength(256);
            session.Property(s => s.ProviderSessionId)
                .HasMaxLength(256);

            session.Property(s => s.Liveness)
                .IsRequired()
                .HasMaxLength(32);
            session.Property(s => s.Activity)
                .IsRequired()
                .HasMaxLength(32);
            session.Property(s => s.Attention)
                .IsRequired()
                .HasMaxLength(32);
            session.Property(s => s.WorkState)
                .IsRequired()
                .HasMaxLength(32);

            session.Property(s => s.StatusReason)
                .IsRequired()
                .HasMaxLength(1024);
            session.Property(s => s.CurrentOperation)
                .HasMaxLength(256);

            session.Property(s => s.StartedAtUtcTicks).HasColumnType("INTEGER");
            session.Property(s => s.LastHeartbeatAtUtcTicks).HasColumnType("INTEGER");
            session.Property(s => s.EndedAtUtcTicks).HasColumnType("INTEGER");
            session.Property(s => s.LastSequence).HasColumnType("INTEGER");

            session.Property(s => s.Version)
                .IsConcurrencyToken()
                .HasColumnType("INTEGER");

            session.HasIndex(s => s.RequestId)
                .HasDatabaseName("IX_AgentSessions_RequestId");

            session.HasIndex(s => s.ParentSessionId)
                .HasDatabaseName("IX_AgentSessions_ParentSessionId");
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

    private sealed class WorkspaceBindingIdConverter : ValueConverter<WorkspaceBindingId, Guid>
    {
        public WorkspaceBindingIdConverter()
            : base(id => id.Value, value => new WorkspaceBindingId(value))
        {
        }
    }
}
