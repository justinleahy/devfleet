using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Infrastructure.Tests;

public class ProjectCatalogTests
{
    private static RegisterProjectCommand Command(
        string repositoryPath,
        string displayName = "Fleet",
        string defaultBranch = "main") => new(
        DisplayName: displayName,
        RepositoryPath: repositoryPath,
        DefaultBranch: defaultBranch,
        Enabled: true,
        MaxActiveWriteRequests: 2,
        MaxReadOnlyRequests: 4,
        MaxChildAgentsPerRequest: 1,
        RequireCleanStart: true,
        CreateRequestBranch: true,
        CreateRequestCommit: false,
        AutoMerge: false);

    [Fact]
    public async Task Register_accepts_a_real_git_repository_inside_an_approved_root()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var dto = await catalog.RegisterAsync(Command(repositoryPath: repositoryPath));

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(Project.CanonicalizePath(repositoryPath), dto.RepositoryPath);
        Assert.Equal("Fleet", dto.DisplayName);
        Assert.Equal("main", dto.DefaultBranch);
        Assert.True(dto.Enabled);
        Assert.Equal(2, dto.MaxActiveWriteRequests);
        Assert.Equal(4, dto.MaxReadOnlyRequests);
        Assert.Equal(1, dto.MaxChildAgentsPerRequest);
        Assert.Equal(dto.CreatedAt, dto.UpdatedAt);
        Assert.Equal(1, dto.Version);
    }

    [Fact]
    public async Task Register_rejects_paths_outside_every_approved_root_with_validation_errors()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var outsideRoot = TestRepositories.InitGitRepository(TestRepositories.CreateTempDirectory());
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var exception = await Assert.ThrowsAsync<ProjectValidationException>(
            () => catalog.RegisterAsync(Command(repositoryPath: outsideRoot)));

        Assert.NotEmpty(exception.Errors);
    }

    [Fact]
    public async Task Validate_reports_a_usable_report_for_paths_inside_and_outside_the_approved_root()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var inside = TestRepositories.InitGitRepository(approvedRoot);
        var outside = TestRepositories.InitGitRepository(TestRepositories.CreateTempDirectory());
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var valid = await catalog.ValidateAsync(Command(repositoryPath: inside));
        var invalid = await catalog.ValidateAsync(Command(repositoryPath: outside));

        Assert.True(valid.IsValid, string.Join("; ", valid.Errors));
        Assert.Empty(valid.Errors);
        Assert.False(invalid.IsValid);
        Assert.NotEmpty(invalid.Errors);
    }

    [Fact]
    public async Task Register_rejects_a_duplicate_canonical_path_even_when_written_differently()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var first = await catalog.RegisterAsync(Command(repositoryPath: repositoryPath));

        // Same repository, path spelled with a trailing slash: still the same canonical path.
        var exception = await Assert.ThrowsAsync<DuplicateProjectException>(
            () => catalog.RegisterAsync(Command(displayName: "Again", repositoryPath: repositoryPath + "/")));

        Assert.Equal(Project.CanonicalizePath(repositoryPath), exception.RepositoryPath);

        // And the catalog still holds exactly one project: the original.
        var projects = await catalog.ListAsync();
        var project = Assert.Single(projects);
        Assert.Equal(first.Id, project.Id);
    }

    [Fact]
    public async Task Get_returns_the_registered_project_and_throws_deterministically_when_missing()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var registered = await catalog.RegisterAsync(Command(repositoryPath: repositoryPath));
        var fetched = await catalog.GetAsync(new ProjectId(registered.Id));

        Assert.Equal(registered.Id, fetched.Id);
        Assert.Equal(registered.RepositoryPath, fetched.RepositoryPath);

        var missing = new ProjectId(Guid.NewGuid());
        var exception = await Assert.ThrowsAsync<ProjectNotFoundException>(() => catalog.GetAsync(missing));
        Assert.Equal(missing.Value, exception.ProjectId);
    }

    [Fact]
    public async Task List_starts_empty_and_reflects_registrations()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        Assert.Empty(await catalog.ListAsync());

        var firstPath = TestRepositories.InitGitRepository(approvedRoot);
        var secondPath = TestRepositories.InitGitRepository(approvedRoot);
        await catalog.RegisterAsync(Command(repositoryPath: firstPath));
        await catalog.RegisterAsync(Command(repositoryPath: secondPath, displayName: "Second"));

        var projects = await catalog.ListAsync();
        Assert.Equal(2, projects.Count);
        Assert.Contains(projects, p => p.DisplayName == "Second");
    }

    [Fact]
    public async Task Register_accepts_a_trailing_separator_and_rejects_a_symlink_alias()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var dto = await catalog.RegisterAsync(Command(repositoryPath: repositoryPath + Path.DirectorySeparatorChar));

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)),
            dto.RepositoryPath);
    }

    [Fact]
    public async Task Register_rejects_a_symlinked_repository_directory_as_an_alias()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var realPath = TestRepositories.InitGitRepository(approvedRoot);
        var aliasPath = Path.Combine(approvedRoot, "alias-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateSymbolicLink(aliasPath, realPath);
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);

        var exception = await Assert.ThrowsAsync<ProjectValidationException>(
            () => catalog.RegisterAsync(Command(repositoryPath: aliasPath)));

        Assert.Contains(
            exception.Errors,
            error => error.Contains("symlink alias", StringComparison.Ordinal));
    }
}
