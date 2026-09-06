using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Nodes;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure.Tests.Verification;

public sealed class VerificationPolicyUpgradeMigratorTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();
    private readonly ListLogger _logger = new();

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task History_and_advertisement_migrates_once_and_audits()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var notifier = new RecordingNotifier();
        var migrator = CreateMigrator(db, notifier);

        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        Assert.Equal("default", project.TrustedVerificationProfileId);
        Assert.Equal("rev-1", project.TrustedVerificationProfileRevision);

        var audits = await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync();
        var audit = Assert.Single(audits);
        Assert.Equal(world.ProjectId, audit.ProjectId);
        Assert.Equal("default", audit.ProfileId);
        Assert.Equal("rev-1", audit.ProfileRevision);
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReason, audit.Reason);
        Assert.Equal(_clock.GetUtcNow().UtcTicks, audit.MigratedAtUtcTicks);

        Assert.Single(_logger.Messages, message =>
            message.Contains("AUDIT verification-policy-default-migration", StringComparison.Ordinal)
            && message.Contains(world.ProjectId.Value.ToString(), StringComparison.Ordinal)
            && message.Contains("default", StringComparison.Ordinal));
        Assert.Contains(notifier.Changes, change => change == ProjectionChange.Project(world.ProjectId.Value));
        Assert.Contains(
            notifier.Changes,
            change => change == ProjectionChange.Request(world.ProjectId.Value, world.RequestId.Value));
    }

    [Fact]
    public async Task Existing_audit_row_excludes_project_via_sqlite_join()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        db.Set<VerificationPolicyUpgradeAuditRow>().Add(new VerificationPolicyUpgradeAuditRow
        {
            ProjectId = world.ProjectId,
            ProfileId = "default",
            ProfileRevision = "prior",
            Reason = VerificationPolicyUpgradeMigrator.AuditReason,
            MigratedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = CreateMigrator(db, new RecordingNotifier());
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal("prior", audit.ProfileRevision);
    }

    [Fact]
    public async Task First_catalog_without_default_is_audited_and_never_reconsidered()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var migrator = CreateMigrator(db, new RecordingNotifier());

        await migrator.MigrateAfterHeartbeatAsync(
            world.NodeId,
            CatalogWithoutDefault());
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-late"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReasonDefaultUnavailable, audit.Reason);
        Assert.Empty(_logger.Messages);
    }

    [Fact]
    public async Task Advertisement_without_history_audits_without_migrating()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db, includeHistory: false);
        var migrator = CreateMigrator(db, new RecordingNotifier());

        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReasonNoHistory, audit.Reason);
        Assert.Empty(_logger.Messages);
    }


    [Fact]
    public async Task Non_default_history_audits_without_migrating()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db, historyProfileId: "suite");
        var migrator = CreateMigrator(db, new RecordingNotifier());

        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReasonNoHistory, audit.Reason);
    }


    [Fact]
    public async Task Only_designated_projects_with_default_history_migrate()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var sibling = TestNodes.SeedProject(db, _clock);
        db.WorkspaceBindings.Add(
            WorkspaceBinding.Designate(
                sibling.Id,
                world.NodeId,
                Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
                _clock.GetUtcNow()));
        TestNodes.SeedRequest(db, sibling, _clock);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = CreateMigrator(db, new RecordingNotifier());
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        var migrated = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        Assert.Equal("default", migrated.TrustedVerificationProfileId);
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == sibling.Id));
        var audits = await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync();
        Assert.Equal(2, audits.Count);
        Assert.Equal(
            VerificationPolicyUpgradeMigrator.AuditReason,
            Assert.Single(audits, row => row.ProjectId == world.ProjectId).Reason);
        Assert.Equal(
            VerificationPolicyUpgradeMigrator.AuditReasonNoHistory,
            Assert.Single(audits, row => row.ProjectId == sibling.Id).Reason);
    }



    [Fact]
    public async Task Missing_catalog_does_nothing()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var migrator = CreateMigrator(db, new RecordingNotifier());

        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, catalog: null);

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        Assert.Empty(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
    }

    [Fact]
    public async Task Explicit_selection_is_untouched_and_audited()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        project.SelectTrustedVerificationProfile("suite", "suite-rev", _clock.GetUtcNow());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = CreateMigrator(db, new RecordingNotifier());
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        var reloaded = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        Assert.Equal("suite", reloaded.TrustedVerificationProfileId);
        Assert.Equal("suite-rev", reloaded.TrustedVerificationProfileRevision);
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReasonExplicitSelection, audit.Reason);
        Assert.Equal("suite", audit.ProfileId);
        Assert.Equal("suite-rev", audit.ProfileRevision);
        Assert.Empty(_logger.Messages);
    }


    [Fact]
    public async Task Other_nodes_cannot_migrate_the_project()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var stranger = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, stranger, _clock);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = CreateMigrator(db, new RecordingNotifier());
        await migrator.MigrateAfterHeartbeatAsync(stranger, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        Assert.Empty(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
    }


    [Fact]
    public async Task Cleared_selection_after_migration_is_never_reselected()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var migrator = CreateMigrator(db, new RecordingNotifier());
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        project.ClearTrustedVerificationProfile(_clock.GetUtcNow());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-2"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReason, audit.Reason);
        Assert.Equal("rev-1", audit.ProfileRevision);
    }

    [Fact]
    public async Task Cleared_selection_after_explicit_evaluation_is_never_migrated()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        project.SelectTrustedVerificationProfile("default", "rev-user", _clock.GetUtcNow());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var migrator = CreateMigrator(db, new RecordingNotifier());
        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        project = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        project.ClearTrustedVerificationProfile(_clock.GetUtcNow());
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await migrator.MigrateAfterHeartbeatAsync(world.NodeId, CatalogWithDefault("rev-1"));

        db.ChangeTracker.Clear();
        AssertUnchanged(await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId));
        var audit = Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
        Assert.Equal(VerificationPolicyUpgradeMigrator.AuditReasonExplicitSelection, audit.Reason);
    }

    [Fact]
    public async Task Heartbeat_with_valid_catalog_invokes_migration()
    {
        await using var db = CreateContext();
        var world = await SeedEligibleAsync(db);
        var registry = new NodeRegistry(_clock, db, new ProjectionNotifier(), CreateMigrator(db, new RecordingNotifier()));
        var observedAt = _clock.GetUtcNow();
        var status = new NodeExecutionStatusDto(
            observedAt,
            AvailableRequestSlots: 2,
            ActiveAssignmentIds: [],
            RoutingRevision: "route-1",
            Routes: [],
            VerificationPolicy: CatalogWithDefault("rev-2", observedAt));

        await registry.HeartbeatAsync(new NodeHeartbeatCommand(world.NodeId, [], ExecutionStatus: status), observedAt);

        db.ChangeTracker.Clear();
        var project = await db.Projects.SingleAsync(candidate => candidate.Id == world.ProjectId);
        Assert.Equal("default", project.TrustedVerificationProfileId);
        Assert.Equal("rev-2", project.TrustedVerificationProfileRevision);
        Assert.Single(await db.Set<VerificationPolicyUpgradeAuditRow>().ToListAsync());
    }

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private VerificationPolicyUpgradeMigrator CreateMigrator(
        ControlPlaneDbContext db,
        IProjectionNotifier notifier) =>
        new(_clock, db, notifier, _logger);

    private async Task<World> SeedEligibleAsync(
        ControlPlaneDbContext db,
        bool includeHistory = true,
        string historyProfileId = "default")
    {
        var nodeId = TestNodes.NewNodeId();
        TestNodes.SeedNode(db, nodeId, _clock);
        var project = TestNodes.SeedProject(db, _clock);
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, path, _clock.GetUtcNow());
        db.WorkspaceBindings.Add(binding);
        var request = TestNodes.SeedRequest(db, project, _clock);
        if (includeHistory)
        {
            db.VerificationRuns.Add(new VerificationRunRow
            {
                Id = Guid.NewGuid(),
                RequestId = request.Id.Value,
                ProfileId = historyProfileId,
                CommandId = "tests",
                Status = "Passed",
                ExitCode = 0,
                StartedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
                CompletedAtUtcTicks = _clock.GetUtcNow().UtcTicks,
                Mandatory = true,
                Fingerprint = "sha256:aaaaaaaa",
                PolicyRevision = "old",
                RunKind = "ProjectCheck",
                AttemptId = Guid.NewGuid(),
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new World(nodeId, project.Id, request.Id);
    }

    private static void AssertUnchanged(Project project)
    {
        Assert.Null(project.TrustedVerificationProfileId);
        Assert.Null(project.TrustedVerificationProfileRevision);
    }

    private static VerificationPolicyCatalogMessage CatalogWithDefault(
        string revision,
        DateTimeOffset? observedAt = null) =>
        new(
            observedAt ?? new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
            BaselineAvailable: true,
            BaselineVersion: "1",
            Profiles:
            [
                new VerificationPolicyProfileMessage(
                    "default",
                    revision,
                    "Default suite",
                    [
                        new VerificationPolicyCommandMessage(
                            "tests",
                            "Tests",
                            "repository",
                            Mandatory: true,
                            TimeoutSeconds: 30),
                    ]),
            ]);

    private static VerificationPolicyCatalogMessage CatalogWithoutDefault() =>
        new(
            new DateTimeOffset(2026, 9, 4, 8, 0, 0, TimeSpan.Zero),
            BaselineAvailable: true,
            BaselineVersion: "1",
            Profiles:
            [
                new VerificationPolicyProfileMessage(
                    "suite",
                    "rev-9",
                    "Suite",
                    [
                        new VerificationPolicyCommandMessage(
                            "tests",
                            "Tests",
                            "repository",
                            Mandatory: true,
                            TimeoutSeconds: 30),
                    ]),
            ]);

    private sealed record World(NodeId NodeId, ProjectId ProjectId, WorkRequestId RequestId);

    private sealed class RecordingNotifier : IProjectionNotifier
    {
        public List<ProjectionChange> Changes { get; } = [];

        public void Publish(ProjectionChange change) => Changes.Add(change);

        public IDisposable Subscribe(Action<ProjectionChange> handler) => new Noop();

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class ListLogger : ILogger<VerificationPolicyUpgradeMigrator>
    {
        public List<string> Messages { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => new Noop();

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }

        private sealed class Noop : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
