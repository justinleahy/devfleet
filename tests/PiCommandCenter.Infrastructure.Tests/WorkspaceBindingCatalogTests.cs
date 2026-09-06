using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Projects;

namespace PiCommandCenter.Infrastructure.Tests;

public sealed class WorkspaceBindingCatalogTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Designate_creates_the_projects_pending_binding()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var catalog = CreateCatalog(db);

        var binding = await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "  /srv/work/fleet  "),
            _clock.GetUtcNow());

        Assert.NotEqual(Guid.Empty, binding.Id);
        Assert.Equal(project.Id.Value, binding.ProjectId);
        Assert.Equal(nodeId.Value, binding.NodeId);
        Assert.Equal("/srv/work/fleet", binding.RepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, binding.Status);
        Assert.Equal(1, binding.ValidationRevision);
        Assert.Null(binding.CanonicalRepositoryPath);
        Assert.Null(binding.ValidationCode);
        Assert.Null(binding.ValidationDetail);
        Assert.Null(binding.ValidatedAt);
        Assert.Equal(binding, await catalog.GetAsync(project.Id));
    }

    [Fact]
    public async Task Designate_replaces_the_sole_binding_and_resets_validation_at_a_new_revision()
    {
        await using var db = CreateContext();
        var (project, firstNodeId) = await SeedProjectAndNodeAsync(db);
        var secondNodeId = AddNode(db);
        await db.SaveChangesAsync();
        var gateway = new StubValidationGateway((_, request) => Valid(request, "/canonical/first"));
        var catalog = CreateCatalog(db, gateway);
        var first = await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(firstNodeId, "/requested/first"),
            _clock.GetUtcNow());
        var valid = await catalog.ValidateAsync(project.Id, _clock.GetUtcNow().AddMinutes(1));

        await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(secondNodeId, "/requested/second"),
            _clock.GetUtcNow().AddMinutes(2));
        db.ChangeTracker.Clear();
        var replaced = await catalog.GetAsync(project.Id);
        Assert.NotNull(replaced);

        Assert.Equal(first.Id, replaced.Id);
        Assert.Equal(valid.CreatedAt, replaced.CreatedAt);
        Assert.Equal(secondNodeId.Value, replaced.NodeId);
        Assert.Equal("/requested/second", replaced.RepositoryPath);
        Assert.Equal(2, replaced.ValidationRevision);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, replaced.Status);
        Assert.Null(replaced.CanonicalRepositoryPath);
        Assert.Null(replaced.ValidationCode);
        Assert.Null(replaced.ValidationDetail);
        Assert.Null(replaced.ValidatedAt);
    }

    [Fact]
    public async Task Designation_path_uniqueness_is_scoped_to_the_node()
    {
        await using var db = CreateContext();
        var firstNodeId = AddNode(db);
        var secondNodeId = AddNode(db);
        var firstProject = AddProject(db);
        var secondProject = AddProject(db);
        var conflictingProject = AddProject(db);
        await db.SaveChangesAsync();
        var catalog = CreateCatalog(db);

        await catalog.DesignateAsync(
            firstProject.Id,
            new DesignateWorkspaceBindingCommand(firstNodeId, "/srv/work/shared"),
            _clock.GetUtcNow());
        var second = await catalog.DesignateAsync(
            secondProject.Id,
            new DesignateWorkspaceBindingCommand(secondNodeId, "/srv/work/shared"),
            _clock.GetUtcNow());

        Assert.Equal(secondNodeId.Value, second.NodeId);
        await Assert.ThrowsAsync<WorkspaceBindingConflictException>(() => catalog.DesignateAsync(
            conflictingProject.Id,
            new DesignateWorkspaceBindingCommand(firstNodeId, "/srv/work/shared"),
            _clock.GetUtcNow()));
    }

    [Fact]
    public async Task Validate_leaves_a_pending_binding_unchanged_when_the_node_is_offline()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var gateway = new StubValidationGateway((_, _) => null);
        var catalog = CreateCatalog(db, gateway);
        var designated = await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/srv/work/fleet"),
            _clock.GetUtcNow());

        var binding = await catalog.ValidateAsync(project.Id, _clock.GetUtcNow().AddMinutes(1));

        Assert.Equal(designated, binding);
        var call = Assert.Single(gateway.Calls);
        Assert.Equal(nodeId.Value, call.NodeId);
        Assert.Equal(designated.Id, call.Request.BindingId);
        Assert.Equal(project.Id.Value, call.Request.ProjectId);
        Assert.Equal(designated.ValidationRevision, call.Request.Revision);
        Assert.Equal(designated.RepositoryPath, call.Request.RepositoryPath);
        Assert.Equal(project.DefaultBranch, call.Request.DefaultBranch);
    }

    [Fact]
    public async Task Validate_persists_the_nodes_canonical_valid_result()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var gateway = new StubValidationGateway((_, request) => Valid(request, "/canonical/fleet"));
        var catalog = CreateCatalog(db, gateway);
        await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/fleet"),
            _clock.GetUtcNow());
        var validatedAt = _clock.GetUtcNow().AddMinutes(1);

        await catalog.ValidateAsync(project.Id, validatedAt);
        db.ChangeTracker.Clear();
        var binding = await catalog.GetAsync(project.Id);
        Assert.NotNull(binding);

        Assert.Equal(WorkspaceBindingStatus.Valid, binding.Status);
        Assert.Equal("/canonical/fleet", binding.CanonicalRepositoryPath);
        Assert.Equal(WorkspaceValidationCodes.Valid, binding.ValidationCode);
        Assert.Equal("Workspace validation succeeded.", binding.ValidationDetail);
        Assert.Equal(validatedAt, binding.ValidatedAt);
    }

    [Fact]
    public async Task Validate_rejects_a_canonical_path_already_valid_on_the_same_node()
    {
        await using var db = CreateContext();
        var nodeId = AddNode(db);
        var firstProject = AddProject(db);
        var aliasProject = AddProject(db);
        await db.SaveChangesAsync();
        var gateway = new StubValidationGateway(
            (_, request) => Valid(request, "/canonical/fleet"));
        var catalog = CreateCatalog(db, gateway);
        await catalog.DesignateAsync(
            firstProject.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/fleet"),
            _clock.GetUtcNow());
        var alias = await catalog.DesignateAsync(
            aliasProject.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/fleet/../fleet"),
            _clock.GetUtcNow());
        await catalog.ValidateAsync(firstProject.Id, _clock.GetUtcNow().AddMinutes(1));

        var exception = await Assert.ThrowsAsync<WorkspaceBindingConflictException>(
            () => catalog.ValidateAsync(aliasProject.Id, _clock.GetUtcNow().AddMinutes(2)));
        var rejectedEntity = await db.WorkspaceBindings
            .SingleAsync(binding => binding.ProjectId == aliasProject.Id);
        var rejected = await catalog.GetAsync(aliasProject.Id);

        Assert.Equal(nodeId, exception.NodeId);
        Assert.Equal("/canonical/fleet", exception.RepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, rejectedEntity.Status);
        Assert.Null(rejectedEntity.CanonicalRepositoryPath);
        Assert.Equal(alias, rejected);
        Assert.Null(rejected!.CanonicalRepositoryPath);
    }

    [Fact]
    public async Task Validate_persists_a_structured_invalid_result_without_a_canonical_path()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var gateway = new StubValidationGateway((_, request) => new WorkspaceBindingValidationResultMessage(
            request.BindingId,
            request.ProjectId,
            request.Revision,
            WorkspaceValidationStatuses.Invalid,
            WorkspaceValidationCodes.PathMissing,
            "Repository path does not exist.",
            CanonicalRepositoryPath: null));
        var catalog = CreateCatalog(db, gateway);
        await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/missing"),
            _clock.GetUtcNow());
        var validatedAt = _clock.GetUtcNow().AddMinutes(1);

        await catalog.ValidateAsync(project.Id, validatedAt);
        db.ChangeTracker.Clear();
        var binding = await catalog.GetAsync(project.Id);
        Assert.NotNull(binding);

        Assert.Equal(WorkspaceBindingStatus.Invalid, binding.Status);
        Assert.Null(binding.CanonicalRepositoryPath);
        Assert.Equal(WorkspaceValidationCodes.PathMissing, binding.ValidationCode);
        Assert.Equal("Repository path does not exist.", binding.ValidationDetail);
        Assert.Equal(validatedAt, binding.ValidatedAt);
    }

    [Fact]
    public async Task Validate_ignores_a_result_for_a_stale_revision()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var gateway = new StubValidationGateway((_, request) => Valid(
            request with { Revision = request.Revision + 1 },
            "/canonical/stale"));
        var catalog = CreateCatalog(db, gateway);
        var designated = await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/fleet"),
            _clock.GetUtcNow());

        var binding = await catalog.ValidateAsync(project.Id, _clock.GetUtcNow().AddMinutes(1));

        Assert.Equal(designated, binding);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, binding.Status);
        Assert.Null(binding.ValidatedAt);
    }

    [Theory]
    [InlineData(ExecutionAssignmentState.Starting)]
    [InlineData(ExecutionAssignmentState.Running)]
    [InlineData(ExecutionAssignmentState.Finalizing)]
    [InlineData(ExecutionAssignmentState.Cancelling)]
    [InlineData(ExecutionAssignmentState.RecoveryRequired)]
    public async Task Active_or_recovery_assignment_prevents_redesignation_and_deletion(
        ExecutionAssignmentState state)
    {
        await using var db = CreateContext();
        var (project, _, binding, catalog, _) = await SeedAssignedBindingAsync(db, state);
        var replacementNodeId = AddNode(db);
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<WorkspaceBindingInUseException>(() => catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(replacementNodeId, "/requested/replacement"),
            _clock.GetUtcNow().AddMinutes(10)));
        await Assert.ThrowsAsync<WorkspaceBindingInUseException>(() => catalog.DeleteAsync(
            project.Id,
            _clock.GetUtcNow().AddMinutes(11)));
        db.ChangeTracker.Clear();

        Assert.Equal(binding, await catalog.GetAsync(project.Id));
    }

    [Theory]
    [InlineData(ExecutionAssignmentState.Completed)]
    [InlineData(ExecutionAssignmentState.Failed)]
    [InlineData(ExecutionAssignmentState.Cancelled)]
    public async Task Terminal_assignment_history_allows_redesignation_and_deletion(
        ExecutionAssignmentState state)
    {
        await using var db = CreateContext();
        var (project, _, binding, catalog, assignment) = await SeedAssignedBindingAsync(db, state);
        var replacementNodeId = AddNode(db);
        await db.SaveChangesAsync();

        var replacement = await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(replacementNodeId, "/requested/replacement"),
            _clock.GetUtcNow().AddMinutes(10));
        await catalog.DeleteAsync(project.Id, _clock.GetUtcNow().AddMinutes(11));
        db.ChangeTracker.Clear();

        Assert.Equal(binding.Id, replacement.Id);
        Assert.Equal(binding.ValidationRevision + 1, replacement.ValidationRevision);
        Assert.Null(await catalog.GetAsync(project.Id));
        Assert.True(await db.ExecutionAssignments.AnyAsync(
            candidate => candidate.RequestId == assignment.RequestId));
    }

    [Fact]
    public async Task Delete_removes_a_binding_when_no_assignment_references_it()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var catalog = CreateCatalog(db);
        await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/srv/work/fleet"),
            _clock.GetUtcNow());

        await catalog.DeleteAsync(project.Id, _clock.GetUtcNow().AddMinutes(1));

        Assert.Null(await catalog.GetAsync(project.Id));
    }

    [Fact]
    public async Task Binding_mutations_publish_project_and_queued_request_invalidations_after_saving()
    {
        await using var db = CreateContext();
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var firstQueued = AddRequest(db, project);
        var secondQueued = AddRequest(db, project);
        var started = AddRequest(db, project);
        started.Start(_clock.GetUtcNow());
        await db.SaveChangesAsync();
        var notifier = new ProjectionNotifier();
        var changes = new List<ProjectionChange>();
        var persistedBindingStatuses = new List<WorkspaceBindingStatus?>();
        using var subscription = notifier.Subscribe(change =>
        {
            changes.Add(change);
            if (change == ProjectionChange.Project(project.Id.Value))
            {
                using var snapshot = CreateContext();
                persistedBindingStatuses.Add(snapshot.WorkspaceBindings
                    .AsNoTracking()
                    .Where(binding => binding.ProjectId == project.Id)
                    .Select(binding => (WorkspaceBindingStatus?)binding.Status)
                    .SingleOrDefault());
            }
        });
        var catalog = CreateCatalog(
            db,
            new StubValidationGateway((_, request) => Valid(request, "/canonical/fleet")),
            notifier);

        await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/fleet"),
            _clock.GetUtcNow());

        AssertEligibilityChanges(changes, project, firstQueued, secondQueued);
        changes.Clear();

        await catalog.ValidateAsync(project.Id, _clock.GetUtcNow().AddMinutes(1));

        AssertEligibilityChanges(changes, project, firstQueued, secondQueued);
        changes.Clear();

        await catalog.DeleteAsync(project.Id, _clock.GetUtcNow().AddMinutes(2));

        AssertEligibilityChanges(changes, project, firstQueued, secondQueued);
        Assert.Equal(
            [WorkspaceBindingStatus.PendingValidation, WorkspaceBindingStatus.Valid, null],
            persistedBindingStatuses);
    }

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

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private static WorkspaceBindingCatalog CreateCatalog(
        ControlPlaneDbContext db,
        StubValidationGateway? gateway = null,
        IProjectionNotifier? notifier = null) => new(
        db,
        gateway ?? new StubValidationGateway((_, _) => null),
        notifier ?? new ProjectionNotifier());

    private async Task<(Project Project, NodeId NodeId)> SeedProjectAndNodeAsync(ControlPlaneDbContext db)
    {
        var project = AddProject(db);
        var nodeId = AddNode(db);
        await db.SaveChangesAsync();
        return (project, nodeId);
    }

    private Project AddProject(ControlPlaneDbContext db)
    {
        var project = Project.Register(
            "Project " + Guid.NewGuid().ToString("N")[..6],
            "main",
            enabled: true,
            maxActiveWriteRequests: 2,
            maxReadOnlyRequests: 4,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: false,
            _clock.GetUtcNow());
        db.Projects.Add(project);
        return project;
    }

    private NodeId AddNode(ControlPlaneDbContext db)
    {
        var nodeId = TestNodes.NewNodeId();
        db.FleetNodes.Add(FleetNode.Register(
            nodeId,
            "node-" + nodeId.Value.ToString("N")[..6],
            "1.0.0",
            "{}",
            _clock.GetUtcNow()));
        return nodeId;
    }

    private WorkRequest AddRequest(ControlPlaneDbContext db, Project project)
    {
        var request = WorkRequest.Enqueue(
            project.Id,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Request " + Guid.NewGuid().ToString("N")[..6],
            "Do the thing",
            _clock.GetUtcNow());
        db.WorkRequests.Add(request);
        return request;
    }

    private async Task<(
        Project Project,
        NodeId NodeId,
        WorkspaceBindingDto Binding,
        WorkspaceBindingCatalog Catalog,
        ExecutionAssignment Assignment)> SeedAssignedBindingAsync(
        ControlPlaneDbContext db,
        ExecutionAssignmentState state)
    {
        var (project, nodeId) = await SeedProjectAndNodeAsync(db);
        var catalog = CreateCatalog(
            db,
            new StubValidationGateway((_, request) => Valid(request, "/canonical/fleet")));
        await catalog.DesignateAsync(
            project.Id,
            new DesignateWorkspaceBindingCommand(nodeId, "/requested/fleet"),
            _clock.GetUtcNow());
        var binding = await catalog.ValidateAsync(project.Id, _clock.GetUtcNow().AddMinutes(1));
        var request = AddRequest(db, project);
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            new WorkspaceBindingId(binding.Id),
            nodeId,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            "claim-token",
            _clock.GetUtcNow().AddMinutes(2),
            TimeSpan.FromMinutes(10));
        TransitionAssignment(assignment, state);
        db.ExecutionAssignments.Add(assignment);
        await db.SaveChangesAsync();
        return (project, nodeId, binding, catalog, assignment);
    }

    private void TransitionAssignment(
        ExecutionAssignment assignment,
        ExecutionAssignmentState state)
    {
        var transitionAt = _clock.GetUtcNow().AddMinutes(3);
        switch (state)
        {
            case ExecutionAssignmentState.Starting:
                return;
            case ExecutionAssignmentState.Running:
                assignment.MarkRunning(transitionAt);
                return;
            case ExecutionAssignmentState.Finalizing:
                assignment.MarkRunning(transitionAt);
                assignment.BeginFinalizing(transitionAt.AddMinutes(1));
                return;
            case ExecutionAssignmentState.Cancelling:
                assignment.BeginCancelling(transitionAt);
                return;
            case ExecutionAssignmentState.RecoveryRequired:
                assignment.MarkRecoveryRequired(transitionAt);
                return;
            case ExecutionAssignmentState.Completed:
                assignment.MarkRunning(transitionAt);
                assignment.BeginFinalizing(transitionAt.AddMinutes(1));
                assignment.Complete(transitionAt.AddMinutes(2));
                return;
            case ExecutionAssignmentState.Failed:
                assignment.MarkRunning(transitionAt);
                assignment.BeginFinalizing(transitionAt.AddMinutes(1));
                assignment.Fail(transitionAt.AddMinutes(2));
                return;
            case ExecutionAssignmentState.Cancelled:
                assignment.BeginCancelling(transitionAt);
                assignment.Cancel(transitionAt.AddMinutes(1));
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }

    private static void AssertEligibilityChanges(
        IReadOnlyCollection<ProjectionChange> changes,
        Project project,
        params WorkRequest[] queuedRequests)
    {
        Assert.Equal(queuedRequests.Length + 2, changes.Count);
        Assert.Equal(1, changes.Count(change => change == ProjectionChange.Fleet()));
        Assert.Equal(
            1,
            changes.Count(change => change == ProjectionChange.Project(project.Id.Value)));
        foreach (var request in queuedRequests)
        {
            Assert.Equal(
                1,
                changes.Count(change => change == ProjectionChange.Request(
                    project.Id.Value,
                    request.Id.Value)));
        }
    }

    private static WorkspaceBindingValidationResultMessage Valid(
        WorkspaceBindingValidationRequestMessage request,
        string canonicalRepositoryPath) => new(
        request.BindingId,
        request.ProjectId,
        request.Revision,
        WorkspaceValidationStatuses.Valid,
        WorkspaceValidationCodes.Valid,
        "Workspace validation succeeded.",
        canonicalRepositoryPath);

    private sealed class StubValidationGateway(
        Func<Guid, WorkspaceBindingValidationRequestMessage, WorkspaceBindingValidationResultMessage?> respond)
        : IWorkspaceValidationGateway
    {
        public List<(Guid NodeId, WorkspaceBindingValidationRequestMessage Request)> Calls { get; } = [];

        public Task<WorkspaceBindingValidationResultMessage?> ValidateAsync(
            Guid nodeId,
            WorkspaceBindingValidationRequestMessage request,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((nodeId, request));
            return Task.FromResult(respond(nodeId, request));
        }
    }
}
