using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.VerificationPolicy;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Tests;

public class ProjectCatalogTests
{
    private static RegisterProjectCommand Command(
        string displayName = "Fleet",
        string defaultBranch = "main",
        int maxActiveWriteRequests = 2,
        int maxReadOnlyRequests = 4,
        int maxChildAgentsPerRequest = 1) => new(
        DisplayName: displayName,
        DefaultBranch: defaultBranch,
        Enabled: true,
        MaxActiveWriteRequests: maxActiveWriteRequests,
        MaxReadOnlyRequests: maxReadOnlyRequests,
        MaxChildAgentsPerRequest: maxChildAgentsPerRequest,
        RequireCleanStart: true,
        CreateRequestBranch: true,
        CreateRequestCommit: false,
        AutoMerge: false);

    [Fact]
    public async Task Register_accepts_fleet_metadata_without_nodes_or_a_workspace_path()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);

        Assert.Empty(context.FleetNodes);
        Assert.Empty(context.WorkspaceBindings);

        var dto = await catalog.RegisterAsync(Command());

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal("Fleet", dto.DisplayName);
        Assert.Equal("main", dto.DefaultBranch);
        Assert.True(dto.Enabled);
        Assert.Equal(2, dto.MaxActiveWriteRequests);
        Assert.Equal(4, dto.MaxReadOnlyRequests);
        Assert.Equal(1, dto.MaxChildAgentsPerRequest);
        Assert.True(dto.RequireCleanStart);
        Assert.True(dto.CreateRequestBranch);
        Assert.False(dto.CreateRequestCommit);
        Assert.False(dto.AutoMerge);
        Assert.Equal(dto.CreatedAt, dto.UpdatedAt);
        Assert.Equal(1, dto.Version);
        Assert.Null(dto.Binding);
        Assert.Empty(context.FleetNodes);
        Assert.Empty(context.WorkspaceBindings);
    }

    [Fact]
    public async Task Validate_and_register_report_only_invalid_fleet_metadata()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var invalid = Command(
            displayName: "   ",
            defaultBranch: "feature branch",
            maxActiveWriteRequests: 0,
            maxReadOnlyRequests: -1,
            maxChildAgentsPerRequest: 0);

        var validReport = await catalog.ValidateAsync(Command(defaultBranch: "feature/project-catalog"));
        var invalidReport = await catalog.ValidateAsync(invalid);
        var exception = await Assert.ThrowsAsync<ProjectValidationException>(() => catalog.RegisterAsync(invalid));

        Assert.True(validReport.IsValid);
        Assert.Empty(validReport.Errors);
        Assert.False(invalidReport.IsValid);
        Assert.Equal(
            [
                "Display name must not be empty.",
                "Default branch 'feature branch' is not a valid branch name.",
                "MaxActiveWriteRequests must be a positive integer.",
                "MaxReadOnlyRequests must be a positive integer.",
                "MaxChildAgentsPerRequest must be a positive integer.",
            ],
            invalidReport.Errors);
        Assert.Equal(invalidReport.Errors, exception.Errors);
        Assert.Empty(context.Projects);
    }

    [Fact]
    public async Task Get_returns_an_unbound_project_and_throws_deterministically_when_missing()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());

        var fetched = await catalog.GetAsync(new ProjectId(registered.Id));

        Assert.Equal(registered, fetched);
        Assert.Null(fetched.Binding);
        Assert.Empty(context.FleetNodes);

        var missing = new ProjectId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<ProjectNotFoundException>(() => catalog.GetAsync(missing));
        Assert.Equal(missing.Value, exception.ProjectId);
    }

    [Fact]
    public async Task List_starts_empty_and_returns_unbound_projects_without_nodes()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);

        Assert.Empty(await catalog.ListAsync());

        await catalog.RegisterAsync(Command());
        await catalog.RegisterAsync(Command(displayName: "Second"));

        var projects = await catalog.ListAsync();

        Assert.Equal(2, projects.Count);
        Assert.All(projects, project => Assert.Null(project.Binding));
        Assert.Contains(projects, project => project.DisplayName == "Second");
        Assert.Empty(context.FleetNodes);
        Assert.Empty(context.WorkspaceBindings);
    }

    [Fact]
    public async Task List_and_get_include_the_designated_workspace_binding()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var project = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(project.Id);
        var nodeId = new NodeId(Guid.NewGuid());
        var now = DateTimeOffset.UtcNow;
        context.FleetNodes.Add(FleetNode.Register(nodeId, "Worker", "1.0", "{}", now));
        var binding = WorkspaceBinding.Designate(projectId, nodeId, "/node/workspaces/fleet", now);
        context.WorkspaceBindings.Add(binding);
        await context.SaveChangesAsync();

        var fetched = await catalog.GetAsync(projectId);
        var listed = Assert.Single(await catalog.ListAsync());

        var fetchedBinding = Assert.IsType<WorkspaceBindingDto>(fetched.Binding);
        Assert.Equal(binding.Id.Value, fetchedBinding.Id);
        Assert.Equal(project.Id, fetchedBinding.ProjectId);
        Assert.Equal(nodeId.Value, fetchedBinding.NodeId);
        Assert.Equal("/node/workspaces/fleet", fetchedBinding.RepositoryPath);
        Assert.Equal(WorkspaceBindingStatus.PendingValidation, fetchedBinding.Status);
        Assert.Null(fetchedBinding.CanonicalRepositoryPath);
        Assert.Equal(fetchedBinding, listed.Binding);
    }

    [Fact]
    public async Task Select_persists_a_trusted_profile_and_clear_returns_baseline_only()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);
        var binding = await SeedBindingAsync(context, projectId);

        var selected = await catalog.SelectTrustedVerificationProfileAsync(
            projectId,
            binding.Id,
            binding.NodeId,
            binding.ValidationRevision,
            registered.Version,
            "dotnet-ci",
            "rev-3");

        Assert.Equal("dotnet-ci", selected.TrustedVerificationProfileId);
        Assert.Equal("rev-3", selected.TrustedVerificationProfileRevision);
        Assert.True(selected.Version > registered.Version);

        var cleared = await catalog.SelectTrustedVerificationProfileAsync(
            projectId,
            binding.Id,
            binding.NodeId,
            binding.ValidationRevision,
            selected.Version,
            profileId: null,
            profileRevision: null);

        Assert.Null(cleared.TrustedVerificationProfileId);
        Assert.Null(cleared.TrustedVerificationProfileRevision);
        var fetched = await catalog.GetAsync(projectId);
        Assert.Null(fetched.TrustedVerificationProfileId);
        Assert.Null(fetched.TrustedVerificationProfileRevision);
    }

    [Fact]
    public async Task Select_throws_when_the_project_is_missing()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);

        await Assert.ThrowsAsync<ProjectNotFoundException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                new ProjectId(Guid.NewGuid()),
                WorkspaceBindingId.New(),
                NodeId.New(),
                validationRevision: 1,
                expectedProjectVersion: 1,
                "dotnet-ci",
                "rev-3"));
    }

    [Fact]
    public async Task Select_rejects_when_the_workspace_binding_is_missing()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                projectId,
                WorkspaceBindingId.New(),
                NodeId.New(),
                validationRevision: 1,
                registered.Version,
                "dotnet-ci",
                "rev-3"));

        var fetched = await catalog.GetAsync(projectId);
        Assert.Null(fetched.TrustedVerificationProfileId);
        Assert.Null(fetched.TrustedVerificationProfileRevision);
        Assert.Equal(registered.Version, fetched.Version);
    }

    [Fact]
    public async Task Select_rejects_when_the_binding_id_changes_before_persist()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);
        var original = await SeedBindingAsync(context, projectId);
        var fenceId = original.Id;
        var fenceNodeId = original.NodeId;
        var fenceRevision = original.ValidationRevision;

        context.WorkspaceBindings.Remove(original);
        await context.SaveChangesAsync();
        await SeedBindingAsync(context, projectId);

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                projectId,
                fenceId,
                fenceNodeId,
                fenceRevision,
                registered.Version,
                "dotnet-ci",
                "rev-3"));

        var fetched = await catalog.GetAsync(projectId);
        Assert.Null(fetched.TrustedVerificationProfileId);
        Assert.Null(fetched.TrustedVerificationProfileRevision);
    }

    [Fact]
    public async Task Select_rejects_when_the_binding_node_changes_before_persist()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);
        var binding = await SeedBindingAsync(context, projectId);
        var fenceId = binding.Id;
        var fenceNodeId = binding.NodeId;
        var fenceRevision = binding.ValidationRevision;

        var reboundNodeId = NodeId.New();
        context.FleetNodes.Add(FleetNode.Register(reboundNodeId, "Rebound", "1.0", "{}", DateTimeOffset.UtcNow));
        binding.Redesignate(reboundNodeId, "/node/workspaces/rebound", DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                projectId,
                fenceId,
                fenceNodeId,
                fenceRevision,
                registered.Version,
                "dotnet-ci",
                "rev-3"));

        var fetched = await catalog.GetAsync(projectId);
        Assert.Null(fetched.TrustedVerificationProfileId);
        Assert.Null(fetched.TrustedVerificationProfileRevision);
    }

    [Fact]
    public async Task Select_rejects_when_the_validation_revision_changes_before_persist()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);
        var binding = await SeedBindingAsync(context, projectId);
        var fenceId = binding.Id;
        var fenceNodeId = binding.NodeId;
        var fenceRevision = binding.ValidationRevision;

        binding.Redesignate(binding.NodeId, "/node/workspaces/fleet-revalidated", DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                projectId,
                fenceId,
                fenceNodeId,
                fenceRevision,
                registered.Version,
                "dotnet-ci",
                "rev-3"));

        var fetched = await catalog.GetAsync(projectId);
        Assert.Null(fetched.TrustedVerificationProfileId);
        Assert.Null(fetched.TrustedVerificationProfileRevision);
    }

    [Fact]
    public async Task Clear_rejects_when_the_binding_fence_changes_before_persist()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);
        var binding = await SeedBindingAsync(context, projectId);
        var selected = await catalog.SelectTrustedVerificationProfileAsync(
            projectId,
            binding.Id,
            binding.NodeId,
            binding.ValidationRevision,
            registered.Version,
            "dotnet-ci",
            "rev-3");
        var fenceId = binding.Id;
        var fenceNodeId = binding.NodeId;
        var fenceRevision = binding.ValidationRevision;

        binding.Redesignate(binding.NodeId, "/node/workspaces/fleet-cleared", DateTimeOffset.UtcNow);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                projectId,
                fenceId,
                fenceNodeId,
                fenceRevision,
                selected.Version,
                profileId: null,
                profileRevision: null));

        var fetched = await catalog.GetAsync(projectId);
        Assert.Equal("dotnet-ci", fetched.TrustedVerificationProfileId);
        Assert.Equal("rev-3", fetched.TrustedVerificationProfileRevision);
        Assert.Equal(selected.Version, fetched.Version);
    }

    [Fact]
    public async Task Select_rejects_when_the_project_version_changes_before_persist()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context);
        var registered = await catalog.RegisterAsync(Command());
        var projectId = new ProjectId(registered.Id);
        var binding = await SeedBindingAsync(context, projectId);
        var selected = await catalog.SelectTrustedVerificationProfileAsync(
            projectId,
            binding.Id,
            binding.NodeId,
            binding.ValidationRevision,
            registered.Version,
            "dotnet-ci",
            "rev-3");

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalog.SelectTrustedVerificationProfileAsync(
                projectId,
                binding.Id,
                binding.NodeId,
                binding.ValidationRevision,
                registered.Version,
                "other",
                "rev-9"));

        var fetched = await catalog.GetAsync(projectId);
        Assert.Equal("dotnet-ci", fetched.TrustedVerificationProfileId);
        Assert.Equal("rev-3", fetched.TrustedVerificationProfileRevision);
        Assert.Equal(selected.Version, fetched.Version);
    }

    [Fact]
    public async Task Concurrent_select_and_redesignation_never_commit_stale_node_selection()
    {
        var sqlitePath = TestRepositories.CreateSqliteFile();
        Guid projectIdValue;
        WorkspaceBindingId fenceId;
        NodeId fenceNodeId;
        long fenceRevision;
        long expectedVersion;

        await using (var seed = TestRepositories.CreateContext(sqlitePath))
        {
            var seedCatalog = TestRepositories.CreateCatalog(seed);
            var registered = await seedCatalog.RegisterAsync(Command());
            var seededProjectId = new ProjectId(registered.Id);
            var binding = await SeedBindingAsync(seed, seededProjectId);
            projectIdValue = registered.Id;
            fenceId = binding.Id;
            fenceNodeId = binding.NodeId;
            fenceRevision = binding.ValidationRevision;
            expectedVersion = registered.Version;
        }

        var projectId = new ProjectId(projectIdValue);
        await using var contextA = TestRepositories.CreateContext(sqlitePath);
        await using var contextB = TestRepositories.CreateContext(sqlitePath);
        var catalogA = TestRepositories.CreateCatalog(contextA);

        var reboundNodeId = NodeId.New();
        contextB.FleetNodes.Add(FleetNode.Register(reboundNodeId, "Rebound", "1.0", "{}", DateTimeOffset.UtcNow));
        await contextB.SaveChangesAsync();
        var current = contextB.WorkspaceBindings.ToList().Single(candidate => candidate.ProjectId == projectId);
        current.Redesignate(reboundNodeId, "/node/workspaces/rebound", DateTimeOffset.UtcNow);
        await contextB.SaveChangesAsync();

        await Assert.ThrowsAsync<VerificationPolicySelectionException>(() =>
            catalogA.SelectTrustedVerificationProfileAsync(
                projectId,
                fenceId,
                fenceNodeId,
                fenceRevision,
                expectedVersion,
                "dotnet-ci",
                "rev-3"));

        var persisted = await catalogA.GetAsync(projectId);
        var persistedBinding = Assert.IsType<WorkspaceBindingDto>(persisted.Binding);
        Assert.Equal(reboundNodeId.Value, persistedBinding.NodeId);
        Assert.NotEqual(fenceRevision, persistedBinding.ValidationRevision);
        Assert.Null(persisted.TrustedVerificationProfileId);
        Assert.Null(persisted.TrustedVerificationProfileRevision);
        Assert.Equal(expectedVersion, persisted.Version);
    }


    private static async Task<WorkspaceBinding> SeedBindingAsync(
        ControlPlaneDbContext context,
        ProjectId projectId)
    {
        var nodeId = NodeId.New();
        var now = DateTimeOffset.UtcNow;
        context.FleetNodes.Add(FleetNode.Register(nodeId, "Worker", "1.0", "{}", now));
        var binding = WorkspaceBinding.Designate(projectId, nodeId, "/node/workspaces/fleet", now);
        context.WorkspaceBindings.Add(binding);
        await context.SaveChangesAsync();
        return binding;
    }
}
