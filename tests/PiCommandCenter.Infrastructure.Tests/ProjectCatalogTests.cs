using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;

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
}
