using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Projects;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests;

public class RequestQueueTests
{
    private static RegisterProjectCommand RegisterCommand(string repositoryPath) => new(
        DisplayName: "Fleet",
        RepositoryPath: repositoryPath,
        DefaultBranch: "main",
        Enabled: true,
        MaxActiveWriteRequests: 2,
        MaxReadOnlyRequests: 4,
        MaxChildAgentsPerRequest: 1,
        RequireCleanStart: true,
        CreateRequestBranch: true,
        CreateRequestCommit: false,
        AutoMerge: false);

    private static QueueWorkRequestCommand EnqueueCommand(
        RequestPriority priority,
        string title = "Do work",
        WorkRequestKind kind = WorkRequestKind.Development) => new(
        Kind: kind,
        Priority: priority,
        RiskLevel: RiskLevel.Standard,
        Title: title,
        Prompt: "Fix the thing");

    private static async Task<(ProjectCatalog Catalog, RequestQueue Queue, Guid ProjectId)> CreateWorldAsync()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);
        var queue = TestRepositories.CreateQueue(context);
        var project = await catalog.RegisterAsync(RegisterCommand(repositoryPath));
        return (catalog, queue, project.Id);
    }

    [Fact]
    public async Task Enqueued_requests_start_queued_and_carry_their_classification()
    {
        var (_, queue, projectId) = await CreateWorldAsync();

        var dto = await queue.EnqueueAsync(new ProjectId(projectId), EnqueueCommand(RequestPriority.Normal));

        Assert.NotEqual(Guid.Empty, dto.Id);
        Assert.Equal(projectId, dto.ProjectId);
        Assert.Equal((int)WorkRequestStatus.Queued, dto.Status);
        Assert.Equal(nameof(WorkRequestStatus.Queued), dto.StatusName);
        Assert.Null(dto.BlockedPhase);
        Assert.Null(dto.BlockedPhaseName);
        Assert.Equal(nameof(WorkRequestKind.Development), dto.KindName);
        Assert.Equal(nameof(RequestPriority.Normal), dto.PriorityName);
        Assert.Equal(nameof(RiskLevel.Standard), dto.RiskLevelName);
        Assert.Equal("Do work", dto.Title);
        Assert.Equal(1, dto.Version);
        Assert.Equal(dto.CreatedAt, dto.UpdatedAt);
    }

    [Fact]
    public async Task Listing_orders_by_priority_descending_then_creation_ascending()
    {
        var (_, queue, projectId) = await CreateWorldAsync();
        var project = new ProjectId(projectId);

        await queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Normal, title: "normal-first"));
        await Task.Delay(60);
        await queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Urgent, title: "urgent"));
        await Task.Delay(60);
        await queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Normal, title: "normal-second"));
        await Task.Delay(60);
        await queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.High, title: "high"));
        await Task.Delay(60);
        await queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Low, title: "low"));

        var ordered = await queue.ListAsync(project);

        Assert.Equal(
            new[] { "urgent", "high", "normal-first", "normal-second", "low" },
            ordered.Select(r => r.Title).ToArray());

        // Priority ordering wins over creation order in both directions.
        Assert.Equal(
            new[] { RequestPriority.Urgent, RequestPriority.High, RequestPriority.Normal, RequestPriority.Normal, RequestPriority.Low },
            ordered.Select(r => (RequestPriority)r.Priority).ToArray());
        Assert.True(ordered.Zip(ordered.Skip(1)).All(pair =>
            pair.First.Priority > pair.Second.Priority
            || (pair.First.Priority == pair.Second.Priority && pair.First.CreatedAt <= pair.Second.CreatedAt)),
            "queue order must be priority descending then CreatedAt ascending");
    }

    [Fact]
    public async Task Queue_operations_fail_deterministically_for_a_missing_project()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);
        await catalog.RegisterAsync(RegisterCommand(repositoryPath));
        var queue = TestRepositories.CreateQueue(context);
        var missing = new ProjectId(Guid.NewGuid());

        await Assert.ThrowsAsync<ProjectNotFoundException>(() => queue.ListAsync(missing));
        await Assert.ThrowsAsync<ProjectNotFoundException>(
            () => queue.EnqueueAsync(missing, EnqueueCommand(RequestPriority.Normal)));
    }

    [Fact]
    public async Task Enqueue_rejects_blank_titles_and_prompts()
    {
        var (_, queue, projectId) = await CreateWorldAsync();
        var project = new ProjectId(projectId);

        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(
            project,
            EnqueueCommand(RequestPriority.Normal) with { Title = "   " }));
        await Assert.ThrowsAsync<ArgumentException>(() => queue.EnqueueAsync(
            project,
            EnqueueCommand(RequestPriority.Normal) with { Prompt = "" }));
    }

    [Fact]
    public async Task Enqueued_requests_persist_across_dbcontext_instances()
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        var sqlitePath = TestRepositories.CreateSqliteFile();

        Guid projectId;
        Guid requestId;
        using (var writeContext = TestRepositories.CreateContext(sqlitePath))
        {
            var catalog = TestRepositories.CreateCatalog(writeContext, approvedRoot);
            var queue = TestRepositories.CreateQueue(writeContext);
            var project = await catalog.RegisterAsync(RegisterCommand(repositoryPath));
            projectId = project.Id;
            var request = await queue.EnqueueAsync(new ProjectId(projectId), EnqueueCommand(RequestPriority.Urgent, title: "survivor"));
            requestId = request.Id;
        }

        // A completely separate context over the same SQLite file must observe the persisted rows.
        using (var readContext = TestRepositories.CreateContext(sqlitePath, createSchema: false))
        {
            var catalog = TestRepositories.CreateCatalog(readContext, approvedRoot);
            var queue = TestRepositories.CreateQueue(readContext);

            var project = await catalog.GetAsync(new ProjectId(projectId));
            Assert.Equal("Fleet", project.DisplayName);
            Assert.Equal(1, project.Version);

            var requests = await queue.ListAsync(new ProjectId(projectId));
            var request = Assert.Single(requests);
            Assert.Equal(requestId, request.Id);
            Assert.Equal("survivor", request.Title);
            Assert.Equal(nameof(WorkRequestStatus.Queued), request.StatusName);
            Assert.Equal(1, request.Version);
        }
    }
}
