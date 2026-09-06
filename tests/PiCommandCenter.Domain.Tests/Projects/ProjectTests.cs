using PiCommandCenter.Domain.Projects;

namespace PiCommandCenter.Domain.Tests;

public class ProjectTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    private static Project Register(
        string displayName = "Fleet",
        string defaultBranch = "main",
        int write = 2,
        int read = 4,
        int children = 1,
        DateTimeOffset? createdAt = null)
    {
        return Project.Register(
            displayName,
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
        Assert.Equal(1, project.Version);
        Assert.Equal(Now, project.CreatedAt);
        Assert.Equal(Now, project.UpdatedAt);
    }

    [Fact]
    public void Register_normalizes_fleet_metadata()
    {
        var project = Register(
            displayName: "  Fleet  ",
            defaultBranch: "  main  ");

        Assert.Equal("Fleet", project.DisplayName);
        Assert.Equal("main", project.DefaultBranch);
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

        Assert.Equal(512, project.MaxActiveWriteRequests);
        Assert.Equal(512, project.MaxReadOnlyRequests);
        Assert.Equal(512, project.MaxChildAgentsPerRequest);
    }

    [Fact]
    public void Register_keeps_the_requested_policy()
    {
        var project = Project.Register(
            "Fleet",
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
    public void Rehydrate_preserves_metadata_policy_identity_timestamps_and_version()
    {
        var id = ProjectId.New();
        var createdAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddDays(2);

        var project = Project.Rehydrate(
            id,
            "Fleet",
            "main",
            enabled: false,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 2,
            maxChildAgentsPerRequest: 3,
            requireCleanStart: false,
            createRequestBranch: true,
            createRequestCommit: false,
            autoMerge: true,
            createdAt,
            updatedAt,
            version: 7);

        Assert.Equal(id, project.Id);
        Assert.Equal("Fleet", project.DisplayName);
        Assert.Equal("main", project.DefaultBranch);
        Assert.False(project.Enabled);
        Assert.Equal(1, project.MaxActiveWriteRequests);
        Assert.Equal(2, project.MaxReadOnlyRequests);
        Assert.Equal(3, project.MaxChildAgentsPerRequest);
        Assert.False(project.RequireCleanStart);
        Assert.True(project.CreateRequestBranch);
        Assert.False(project.CreateRequestCommit);
        Assert.True(project.AutoMerge);
        Assert.Equal(createdAt, project.CreatedAt);
        Assert.Equal(updatedAt, project.UpdatedAt);
        Assert.Equal(7, project.Version);
    }

    [Fact]
    public void Update_rewrites_metadata_and_policy_and_bumps_version()
    {
        var project = Register();
        var later = Now.AddHours(1);

        project.Update(
            "Renamed",
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
        Assert.Equal("develop", project.DefaultBranch);
        Assert.False(project.Enabled);
        Assert.Equal(3, project.MaxActiveWriteRequests);
        Assert.Equal(6, project.MaxReadOnlyRequests);
        Assert.Equal(2, project.MaxChildAgentsPerRequest);
        Assert.False(project.RequireCleanStart);
        Assert.False(project.CreateRequestBranch);
        Assert.False(project.CreateRequestCommit);
        Assert.True(project.AutoMerge);
        Assert.Equal(later, project.UpdatedAt);
        Assert.Equal(2, project.Version);
    }

    [Fact]
    public void Update_rejects_invalid_metadata_without_mutating_state()
    {
        var project = Register();

        Assert.Throws<ArgumentException>(() => project.Update(
            " ",
            "develop",
            enabled: false,
            maxActiveWriteRequests: 1,
            maxReadOnlyRequests: 1,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: true,
            Now.AddMinutes(1)));

        Assert.Equal("Fleet", project.DisplayName);
        Assert.Equal("main", project.DefaultBranch);
        Assert.True(project.Enabled);
        Assert.Equal(Now, project.UpdatedAt);
        Assert.Equal(1, project.Version);
    }
}
