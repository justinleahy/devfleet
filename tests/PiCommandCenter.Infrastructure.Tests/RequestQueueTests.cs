using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Projects;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests;

public class RequestQueueTests
{
    private static RegisterProjectCommand RegisterCommand() => new(
        DisplayName: "Fleet",
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

    private static async Task<TestWorld> CreateWorldAsync()
    {
        var clock = TestNodes.Clock();
        var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var catalog = new ProjectCatalog(clock, context, new ProjectionNotifier());
        var queue = CreateQueue(clock, context, out var evaluator);
        var project = await catalog.RegisterAsync(RegisterCommand());
        return new TestWorld(context, queue, evaluator, clock, project.Id);
    }

    [Fact]
    public async Task Enqueue_without_a_workspace_binding_reports_the_same_reason_from_list_and_get()
    {
        var world = await CreateWorldAsync();
        using var context = world.Context;
        Assert.Empty(context.FleetNodes);
        Assert.Empty(context.WorkspaceBindings);

        var enqueued = await world.Queue.EnqueueAsync(
            new ProjectId(world.ProjectId),
            EnqueueCommand(RequestPriority.Normal));
        var listed = Assert.Single(await world.Queue.ListAsync(new ProjectId(world.ProjectId)));
        var fetched = await world.Queue.GetAsync(new WorkRequestId(enqueued.Id));

        Assert.NotEqual(Guid.Empty, enqueued.Id);
        Assert.Equal(world.ProjectId, enqueued.ProjectId);
        Assert.Equal((int)WorkRequestStatus.Queued, enqueued.Status);
        Assert.Equal(nameof(WorkRequestStatus.Queued), enqueued.StatusName);
        Assert.Null(enqueued.BlockedPhase);
        Assert.Null(enqueued.BlockedPhaseName);
        Assert.Equal(nameof(WorkRequestKind.Development), enqueued.KindName);
        Assert.Equal(nameof(RequestPriority.Normal), enqueued.PriorityName);
        Assert.Equal(nameof(RiskLevel.Standard), enqueued.RiskLevelName);
        Assert.Equal("Do work", enqueued.Title);
        Assert.Equal(1, enqueued.Version);
        Assert.Equal(enqueued.CreatedAt, enqueued.UpdatedAt);
        Assert.Equal(SchedulingReasonCodes.WorkspaceBindingMissing, enqueued.SchedulingStatus?.Code);
        Assert.Equal(enqueued.SchedulingStatus, listed.SchedulingStatus);
        Assert.Equal(enqueued.SchedulingStatus, fetched.SchedulingStatus);
        Assert.Null(enqueued.Assignment);
        Assert.Null(listed.Assignment);
        Assert.Null(fetched.Assignment);
        Assert.Empty(context.FleetNodes);
        Assert.Empty(context.WorkspaceBindings);
        Assert.Equal(
            [new WorkRequestId(enqueued.Id), new WorkRequestId(enqueued.Id)],
            world.Evaluator.SingularRequests);
        var batch = Assert.Single(world.Evaluator.BatchRequests);
        Assert.Equal([new WorkRequestId(enqueued.Id)], batch);
    }

    [Fact]
    public async Task Eligible_request_projection_comes_from_the_evaluator()
    {
        var world = await CreateWorldAsync();
        using var context = world.Context;
        await SeedEligibleWorkspaceAsync(world);

        var enqueued = await world.Queue.EnqueueAsync(
            new ProjectId(world.ProjectId),
            EnqueueCommand(RequestPriority.Normal));
        var listed = Assert.Single(await world.Queue.ListAsync(new ProjectId(world.ProjectId)));
        var fetched = await world.Queue.GetAsync(new WorkRequestId(enqueued.Id));

        var scheduling = Assert.IsType<SchedulingStatusDto>(enqueued.SchedulingStatus);
        Assert.Equal(SchedulingReasonCodes.Eligible, scheduling.Code);
        Assert.True(scheduling.IsEligible);
        Assert.Equal(scheduling, listed.SchedulingStatus);
        Assert.Equal(scheduling, fetched.SchedulingStatus);
        Assert.Null(enqueued.Assignment);
    }

    [Fact]
    public async Task Terminal_request_retains_its_assignment_snapshot_after_binding_edits()
    {
        var world = await CreateWorldAsync();
        using var context = world.Context;
        var (node, binding) = await SeedEligibleWorkspaceAsync(world);
        var enqueued = await world.Queue.EnqueueAsync(
            new ProjectId(world.ProjectId),
            EnqueueCommand(RequestPriority.Normal));
        var request = await context.WorkRequests.SingleAsync(
            candidate => candidate.Id == new WorkRequestId(enqueued.Id));
        var project = await context.Projects.SingleAsync(
            candidate => candidate.Id == new ProjectId(world.ProjectId));
        var assignedAt = world.Clock.GetUtcNow().AddMinutes(1);
        const string originalPath = "/srv/work/fleet";
        var assignment = ExecutionAssignment.Create(
            request.Id,
            project.Id,
            binding.Id,
            node.Id,
            originalPath,
            project.DefaultBranch,
            binding.ValidationRevision,
            "secret-claim-token",
            assignedAt,
            TimeSpan.FromMinutes(5));
        context.ExecutionAssignments.Add(assignment);

        request.Start(assignedAt);
        request.BeginPlanning(assignedAt.AddSeconds(1));
        request.BeginExecuting(assignedAt.AddSeconds(2));
        request.BeginReviewing(assignedAt.AddSeconds(3));
        request.BeginVerifying(assignedAt.AddSeconds(4));
        request.Complete(assignedAt.AddSeconds(5));
        assignment.MarkRunning(assignedAt.AddSeconds(1));
        assignment.BeginFinalizing(assignedAt.AddSeconds(2));
        assignment.Complete(assignedAt.AddSeconds(3));
        binding.Redesignate(node.Id, "/srv/work/replacement", assignedAt.AddSeconds(6));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var fetched = await world.Queue.GetAsync(request.Id);
        var projected = Assert.IsType<ExecutionAssignmentProjectionDto>(fetched.Assignment);

        Assert.Null(fetched.SchedulingStatus);
        Assert.Equal(binding.Id.Value, projected.WorkspaceBindingId);
        Assert.Equal(node.Id.Value, projected.NodeIdSnapshot);
        Assert.Equal(originalPath, projected.CanonicalRepositoryPathSnapshot);
        Assert.Equal("main", projected.DefaultBranchSnapshot);
        Assert.Equal(1, projected.BindingValidationRevisionSnapshot);
        Assert.Equal(ExecutionAssignmentState.Completed, projected.State);
        Assert.Equal(assignedAt.AddSeconds(3), projected.TerminalAt);
    }

    [Fact]
    public async Task Listing_orders_by_priority_descending_then_creation_ascending()
    {
        var world = await CreateWorldAsync();
        using var context = world.Context;
        var project = new ProjectId(world.ProjectId);

        await world.Queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Normal, title: "normal-first"));
        await Task.Delay(60);
        await world.Queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Urgent, title: "urgent"));
        await Task.Delay(60);
        await world.Queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Normal, title: "normal-second"));
        await Task.Delay(60);
        await world.Queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.High, title: "high"));
        await Task.Delay(60);
        await world.Queue.EnqueueAsync(project, EnqueueCommand(RequestPriority.Low, title: "low"));

        var ordered = await world.Queue.ListAsync(project);

        Assert.Equal(
            new[] { "urgent", "high", "normal-first", "normal-second", "low" },
            ordered.Select(request => request.Title).ToArray());
        Assert.Equal(
            new[] { RequestPriority.Urgent, RequestPriority.High, RequestPriority.Normal, RequestPriority.Normal, RequestPriority.Low },
            ordered.Select(request => (RequestPriority)request.Priority).ToArray());
        Assert.True(ordered.Zip(ordered.Skip(1)).All(pair =>
            pair.First.Priority > pair.Second.Priority
            || (pair.First.Priority == pair.Second.Priority && pair.First.CreatedAt <= pair.Second.CreatedAt)),
            "queue order must be priority descending then CreatedAt ascending");
        var batch = Assert.Single(world.Evaluator.BatchRequests);
        Assert.Equal(
            ordered.Select(request => new WorkRequestId(request.Id)),
            batch);
    }

    [Fact]
    public async Task Queue_operations_fail_deterministically_for_a_missing_project()
    {
        using var context = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var queue = CreateQueue(TimeProvider.System, context, out _);
        var missing = new ProjectId(Guid.NewGuid());

        await Assert.ThrowsAsync<ProjectNotFoundException>(() => queue.ListAsync(missing));
        await Assert.ThrowsAsync<ProjectNotFoundException>(
            () => queue.EnqueueAsync(missing, EnqueueCommand(RequestPriority.Normal)));
    }

    [Fact]
    public async Task Enqueue_rejects_blank_titles_and_prompts()
    {
        var world = await CreateWorldAsync();
        using var context = world.Context;
        var project = new ProjectId(world.ProjectId);

        await Assert.ThrowsAsync<ArgumentException>(() => world.Queue.EnqueueAsync(
            project,
            EnqueueCommand(RequestPriority.Normal) with { Title = "   " }));
        await Assert.ThrowsAsync<ArgumentException>(() => world.Queue.EnqueueAsync(
            project,
            EnqueueCommand(RequestPriority.Normal) with { Prompt = "" }));
    }

    [Fact]
    public async Task Enqueued_requests_persist_across_dbcontext_instances()
    {
        var sqlitePath = TestRepositories.CreateSqliteFile();

        Guid projectId;
        Guid requestId;
        DateTimeOffset queuedAt;
        using (var writeContext = TestRepositories.CreateContext(sqlitePath))
        {
            var catalog = TestRepositories.CreateCatalog(writeContext);
            var queue = CreateQueue(TimeProvider.System, writeContext, out _);
            var project = await catalog.RegisterAsync(RegisterCommand());
            projectId = project.Id;
            var request = await queue.EnqueueAsync(
                new ProjectId(projectId),
                EnqueueCommand(RequestPriority.Urgent, title: "survivor"));
            requestId = request.Id;
            queuedAt = request.CreatedAt;
        }

        using (var readContext = TestRepositories.CreateContext(sqlitePath, createSchema: false))
        {
            var catalog = TestRepositories.CreateCatalog(readContext);
            var queue = CreateQueue(TimeProvider.System, readContext, out _);

            var project = await catalog.GetAsync(new ProjectId(projectId));
            Assert.Equal(projectId, project.Id);
            Assert.Equal("Fleet", project.DisplayName);
            Assert.Equal(1, project.Version);
            Assert.Null(project.Binding);

            var requests = await queue.ListAsync(new ProjectId(projectId));
            var request = Assert.Single(requests);
            Assert.Equal(requestId, request.Id);
            Assert.Equal(projectId, request.ProjectId);
            Assert.Equal("survivor", request.Title);
            Assert.Equal((int)WorkRequestStatus.Queued, request.Status);
            Assert.Equal(nameof(WorkRequestStatus.Queued), request.StatusName);
            Assert.Equal(1, request.Version);
            Assert.Equal(queuedAt, request.CreatedAt);
            Assert.Equal(queuedAt, request.UpdatedAt);
        }
    }

    private static RequestQueue CreateQueue(
        TimeProvider clock,
        ControlPlaneDbContext context,
        out RecordingEligibilityEvaluator evaluator)
    {
        evaluator = new RecordingEligibilityEvaluator(
            new RequestEligibilityEvaluator(
                clock,
                Options.Create(new NodeLivenessOptions()),
                context));
        return new RequestQueue(clock, context, evaluator, new ProjectionNotifier());
    }

    private static async Task<(FleetNode Node, WorkspaceBinding Binding)> SeedEligibleWorkspaceAsync(
        TestWorld world)
    {
        var now = world.Clock.GetUtcNow();
        var node = FleetNode.Register(NodeId.New(), "ready-node", "1.0.0", "{}", now);
        var executionStatus = new NodeExecutionStatusDto(
            now,
            AvailableRequestSlots: 1,
            ActiveAssignmentIds: [],
            RoutingRevision: "routing-v1",
            Routes:
            [
                new RuntimeRouteReadinessDto(
                    "root",
                    "codex/default",
                    "ready",
                    "native-auth",
                    now,
                    "routing-v1"),
            ]);
        node.Heartbeat(
            "1.0.0",
            "{}",
            now,
            executionStatusJson: JsonSerializer.Serialize(executionStatus, TestRepositories.WebJson));
        world.Context.FleetNodes.Add(node);

        var binding = WorkspaceBinding.Designate(
            new ProjectId(world.ProjectId),
            node.Id,
            "/srv/work/fleet",
            now);
        Assert.True(binding.ApplyValidationResult(
            node.Id,
            binding.ValidationRevision,
            WorkspaceBindingStatus.Valid,
            WorkspaceBinding.ValidValidationCode,
            "Workspace and runtime are ready.",
            "/srv/work/fleet",
            now));
        world.Context.WorkspaceBindings.Add(binding);
        await world.Context.SaveChangesAsync();
        return (node, binding);
    }

    private sealed record TestWorld(
        ControlPlaneDbContext Context,
        RequestQueue Queue,
        RecordingEligibilityEvaluator Evaluator,
        FakeTimeProvider Clock,
        Guid ProjectId);

    private sealed class RecordingEligibilityEvaluator(IRequestEligibilityEvaluator inner)
        : IRequestEligibilityEvaluator
    {
        public List<IReadOnlyList<WorkRequestId>> BatchRequests { get; } = [];

        public List<WorkRequestId> SingularRequests { get; } = [];

        public Task<IReadOnlyDictionary<WorkRequestId, EligibilityDecision>> EvaluateBatchAsync(
            IReadOnlyCollection<WorkRequestId> requestIds,
            CancellationToken cancellationToken = default)
        {
            BatchRequests.Add(requestIds.ToArray());
            return inner.EvaluateBatchAsync(requestIds, cancellationToken);
        }

        public Task<EligibilityDecision> EvaluateAsync(
            WorkRequestId requestId,
            NodeId? candidateNodeId = null,
            CancellationToken cancellationToken = default)
        {
            SingularRequests.Add(requestId);
            return inner.EvaluateAsync(requestId, candidateNodeId, cancellationToken);
        }
    }
}
