using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Domain.Tests;

public class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static Project Register(
        string displayName = "Fleet",
        string repositoryPath = "/tmp/fleet",
        string defaultBranch = "main",
        int write = 2,
        int read = 4,
        int children = 1,
        DateTimeOffset? createdAt = null)
    {
        return Project.Register(
            Domain.NodeId.New(),
            displayName,
            repositoryPath,
            defaultBranch,
            enabled: true,
            maxActiveWriteRequests: write,
            maxReadOnlyRequests: read,
            maxChildAgentsPerRequest: children,
            requireCleanStart: true,
            createRequestBranch: true,
            createRequestCommit: true,
            autoMerge: false,
            createdAt ?? Now);
    }

    [Fact]
    public void Register_assigns_a_new_id_and_starts_at_version_one()
    {
        var project = Register();

        Assert.NotEqual(Guid.Empty, project.Id.Value);
        Assert.NotEqual(Guid.Empty, project.NodeId.Value);
        Assert.Equal(1, project.Version);
        Assert.Equal(Now, project.CreatedAt);
        Assert.Equal(Now, project.UpdatedAt);
    }

    [Fact]
    public void Register_normalizes_text_fields()
    {
        var project = Register(
            displayName: "  Fleet  ",
            repositoryPath: "  /tmp/fleet  ",
            defaultBranch: "  main  ");

        Assert.Equal("Fleet", project.DisplayName);
        Assert.Equal("/tmp/fleet", project.RepositoryPath);
        Assert.Equal("main", project.DefaultBranch);
    }

    [Fact]
    public void Canonicalize_path_trims_whitespace_and_collapses_trailing_separator()
    {
        Assert.Equal("/tmp/fleet", Project.CanonicalizePath("  /tmp/fleet\t"));
        Assert.Throws<ArgumentException>(() => Project.CanonicalizePath("   "));

        var expected = Path.TrimEndingDirectorySeparator(Path.GetFullPath("/tmp/repo"));
        Assert.Equal(expected, Project.CanonicalizePath("/tmp/repo"));
        Assert.Equal(expected, Project.CanonicalizePath("/tmp/repo" + Path.DirectorySeparatorChar));
        Assert.Equal(expected, Project.CanonicalizePath("/tmp/repo" + Path.DirectorySeparatorChar + "  "));
    }

    [Fact]
    public void Canonicalize_path_preserves_filesystem_root()
    {
        var root = Path.GetPathRoot(Path.GetFullPath("/"))!;
        var canonical = Project.CanonicalizePath(root + "  ");

        Assert.Equal(Path.TrimEndingDirectorySeparator(root), canonical);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_blank_display_names(string displayName)
    {
        Assert.Throws<ArgumentException>(() => Register(displayName: displayName));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_blank_repository_paths(string repositoryPath)
    {
        Assert.Throws<ArgumentException>(() => Register(repositoryPath: repositoryPath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_rejects_blank_default_branches(string defaultBranch)
    {
        Assert.Throws<ArgumentException>(() => Register(defaultBranch: defaultBranch));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    [InlineData(1, -3, 1)]
    public void Register_rejects_non_positive_concurrency_limits(int write, int read, int children)
    {
        Assert.Throws<ArgumentException>(() => Register(write: write, read: read, children: children));
    }

    [Fact]
    public void Register_clamps_concurrency_limits_to_the_domain_maximum()
    {
        var project = Register(write: 5_000, read: 5_000, children: 5_000);

        Assert.True(project.MaxActiveWriteRequests <= 512);
        Assert.True(project.MaxReadOnlyRequests <= 512);
        Assert.True(project.MaxChildAgentsPerRequest <= 512);
        Assert.Equal(512, project.MaxActiveWriteRequests);
    }

    [Fact]
    public void Register_keeps_the_requested_flags()
    {
        var project = Project.Register(
            Domain.NodeId.New(),
            "Fleet",
            "/tmp/fleet",
            "main",
            enabled: false,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 1,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: true,
            Now);

        Assert.False(project.Enabled);
        Assert.False(project.RequireCleanStart);
        Assert.False(project.CreateRequestBranch);
        Assert.False(project.CreateRequestCommit);
        Assert.True(project.AutoMerge);
    }

    [Fact]
    public void Rehydrate_preserves_identity_timestamps_and_version()
    {
        var id = ProjectId.New();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddDays(2);

        var project = Project.Rehydrate(
            id,
            Domain.NodeId.New(),
            "Fleet",
            "/tmp/fleet",
            "main",
            enabled: true,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 2,
            maxChildAgentsPerRequest: 3,
            requireCleanStart: true,
            createRequestBranch: true,
            createRequestCommit: false,
            autoMerge: false,
            createdAt,
            updatedAt,
            version: 7);

        Assert.Equal(id, project.Id);
        Assert.Equal(createdAt, project.CreatedAt);
        Assert.Equal(updatedAt, project.UpdatedAt);
        Assert.Equal(7, project.Version);
    }

    [Fact]
    public void Update_rewrites_state_and_bumps_version()
    {
        var project = Register();
        var later = Now.AddHours(1);

        project.Update(
            "Renamed",
            "/tmp/renamed",
            "develop",
            enabled: false,
            maxActiveWriteRequests: 3,
            maxReadOnlyRequests: 6,
            maxChildAgentsPerRequest: 2,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: true,
            later);

        Assert.Equal("Renamed", project.DisplayName);
        Assert.Equal("/tmp/renamed", project.RepositoryPath);
        Assert.Equal("develop", project.DefaultBranch);
        Assert.False(project.Enabled);
        Assert.Equal(3, project.MaxActiveWriteRequests);
        Assert.Equal(6, project.MaxReadOnlyRequests);
        Assert.Equal(2, project.MaxChildAgentsPerRequest);
        Assert.True(project.AutoMerge);
        Assert.Equal(later, project.UpdatedAt);
        Assert.Equal(2, project.Version);
    }

    [Fact]
    public void Update_rejects_invalid_state_without_bumping_version()
    {
        var project = Register();

        Assert.Throws<ArgumentException>(() => project.Update(
            " ",
            "/tmp/fleet",
            "main",
            enabled: true,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 1,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: true,
            createRequestBranch: true,
            createRequestCommit: true,
            autoMerge: false,
            Now.AddMinutes(1)));

        Assert.Equal(1, project.Version);
        Assert.Equal("Fleet", project.DisplayName);
    }
}
