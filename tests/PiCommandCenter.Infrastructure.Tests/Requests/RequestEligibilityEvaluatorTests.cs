using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Requests;

namespace PiCommandCenter.Infrastructure.Tests.Requests;

public sealed class RequestEligibilityEvaluatorTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly FakeTimeProvider _clock = TestNodes.Clock();

    [Fact]
    public async Task Disabled_project_precedes_a_missing_binding()
    {
        await using var db = CreateContext();
        var project = SeedProject(db, enabled: false);
        var request = SeedRequest(db, project);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(request.Id);

        AssertReason(decision, SchedulingReasonCodes.ProjectDisabled);
    }

    [Fact]
    public async Task Missing_binding_is_reported_without_fabricating_placement()
    {
        await using var db = CreateContext();
        var project = SeedProject(db);
        var request = SeedRequest(db, project);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(request.Id);

        AssertReason(decision, SchedulingReasonCodes.WorkspaceBindingMissing);
        Assert.Null(decision.EligibleBinding);
    }

    [Fact]
    public async Task Pending_validation_precedes_node_liveness()
    {
        await using var db = CreateContext();
        var project = SeedProject(db);
        var request = SeedRequest(db, project);
        var node = SeedNode(db, NodeStatus.Offline, executionStatusJson: null);
        SeedBinding(db, project, node.Id, WorkspaceBindingStatus.PendingValidation);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(request.Id);

        AssertReason(decision, SchedulingReasonCodes.WorkspaceValidationPending);
    }

    [Fact]
    public async Task Missing_path_validation_retains_safe_detail_and_precedes_node_liveness()
    {
        await using var db = CreateContext();
        var project = SeedProject(db);
        var request = SeedRequest(db, project);
        var node = SeedNode(db, NodeStatus.Offline, executionStatusJson: null);
        SeedBinding(
            db,
            project,
            node.Id,
            WorkspaceBindingStatus.Invalid,
            WorkspaceValidationCodes.PathMissing,
            "Repository path does not exist.");
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(request.Id);

        AssertReason(decision, SchedulingReasonCodes.WorkspaceInvalid);
        Assert.Equal("Repository path does not exist.", decision.Status.Detail);
        Assert.Contains("path", decision.Status.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Offline_node_is_reported_before_runtime_status()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db, nodeStatus: NodeStatus.Offline, executionStatusJson: "{not-json");
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.NodeOffline);
    }

    [Fact]
    public async Task Stale_heartbeat_is_offline_even_when_execution_status_is_fresh()
    {
        await using var db = CreateContext();
        var world = SeedWorld(
            db,
            heartbeatAt: _clock.GetUtcNow().AddSeconds(-31),
            executionStatusJson: SerializeStatus());
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.NodeOffline);
    }

    [Fact]
    public async Task Candidate_mismatch_fails_closed_as_node_offline()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db);
        var otherNode = SeedNode(db, NodeStatus.Online, SerializeStatus());
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id, otherNode.Id);

        AssertReason(decision, SchedulingReasonCodes.NodeOffline);
        Assert.Equal(otherNode.Id, decision.CandidateNodeId);
        Assert.Null(decision.EligibleBinding);
    }

    [Fact]
    public async Task Unavailable_route_precedes_unknown_route_and_capacity()
    {
        await using var db = CreateContext();
        var world = SeedWorld(
            db,
            executionStatusJson: SerializeStatus(
                availableSlots: 0,
                readiness: [RuntimeReadinessStatuses.Unknown, RuntimeReadinessStatuses.Unavailable]));
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.RuntimeUnavailable);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    [InlineData("no_routes")]
    [InlineData("stale_status")]
    [InlineData("future_status")]
    [InlineData("non_utc_status")]
    [InlineData("stale_route")]
    [InlineData("unknown_route")]
    public async Task Missing_corrupt_or_untrusted_runtime_evidence_is_unknown(string scenario)
    {
        await using var db = CreateContext();
        var now = _clock.GetUtcNow();
        var statusJson = scenario switch
        {
            "missing" => null,
            "corrupt" => "{not-json",
            "no_routes" => SerializeStatus(readiness: []),
            "stale_status" => SerializeStatus(statusAt: now.AddSeconds(-31)),
            "future_status" => SerializeStatus(statusAt: now.AddSeconds(1)),
            "non_utc_status" => SerializeStatus(statusAt: now.ToOffset(TimeSpan.FromHours(1))),
            "stale_route" => SerializeStatus(routeAt: now.AddSeconds(-31)),
            "unknown_route" => SerializeStatus(readiness: [RuntimeReadinessStatuses.Unknown]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        var world = SeedWorld(
            db,
            executionStatusJson: statusJson,
            omitExecutionStatus: scenario == "missing");
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.RuntimeUnknown);
    }

    [Fact]
    public async Task Advertised_zero_slots_precedes_project_concurrency()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db, executionStatusJson: SerializeStatus(availableSlots: 0));
        SeedAssignment(
            db,
            world.Project,
            world.Binding,
            world.Node.Id,
            WorkRequestKind.Development,
            ExecutionAssignmentState.Starting);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.NodeCapacityUnavailable);
    }

    [Fact]
    public async Task Unadvertised_recovery_assignment_occupies_node_capacity_after_lease_expiry()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db, executionStatusJson: SerializeStatus(availableSlots: 1));
        var otherProject = SeedProject(db);
        var otherBinding = SeedBinding(db, otherProject, world.Node.Id, WorkspaceBindingStatus.Valid);
        SeedAssignment(
            db,
            otherProject,
            otherBinding,
            world.Node.Id,
            WorkRequestKind.Analysis,
            ExecutionAssignmentState.RecoveryRequired,
            leaseExpired: true);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.NodeCapacityUnavailable);
    }

    [Fact]
    public async Task Development_uses_effective_cap_one_and_counts_expired_recovery_work()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db, maxActiveWriteRequests: 8);
        var active = SeedAssignment(
            db,
            world.Project,
            world.Binding,
            world.Node.Id,
            WorkRequestKind.Development,
            ExecutionAssignmentState.RecoveryRequired,
            leaseExpired: true);
        SetExecutionStatus(world.Node, SerializeStatus(
            availableSlots: 1,
            activeAssignmentIds: [active.RequestId.Value]));
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.ProjectConcurrencyUnavailable);
    }

    [Theory]
    [InlineData(WorkRequestKind.Analysis, WorkRequestKind.Review)]
    [InlineData(WorkRequestKind.Review, WorkRequestKind.Analysis)]
    public async Task Analysis_and_review_share_the_read_only_policy_cap(
        WorkRequestKind requestedKind,
        WorkRequestKind activeKind)
    {
        await using var db = CreateContext();
        var world = SeedWorld(db, requestKind: requestedKind, maxReadOnlyRequests: 1);
        var active = SeedAssignment(
            db,
            world.Project,
            world.Binding,
            world.Node.Id,
            activeKind,
            ExecutionAssignmentState.Running);
        SetExecutionStatus(world.Node, SerializeStatus(
            availableSlots: 1,
            activeAssignmentIds: [active.RequestId.Value]));
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        AssertReason(decision, SchedulingReasonCodes.ProjectConcurrencyUnavailable);
    }

    [Fact]
    public async Task Eligible_result_returns_the_validated_binding_for_the_designated_candidate()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db, requestKind: WorkRequestKind.Review);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id, world.Node.Id);

        Assert.Equal(SchedulingReasonCodes.Eligible, decision.Status.Code);
        Assert.True(decision.Status.IsEligible);
        Assert.Equal(world.Node.Id, decision.CandidateNodeId);
        var binding = Assert.IsType<PiCommandCenter.Application.Projects.WorkspaceBindingDto>(
            decision.EligibleBinding);
        Assert.Equal(world.Binding.Id.Value, binding.Id);
        Assert.Equal(world.Binding.CanonicalRepositoryPath, binding.CanonicalRepositoryPath);
        Assert.Equal(world.Binding.ValidationRevision, binding.ValidationRevision);
    }

    [Fact]
    public async Task Batch_evaluation_returns_one_decision_per_request()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db);
        var second = SeedRequest(db, world.Project, WorkRequestKind.Analysis);
        await SaveAsync(db);

        var decisions = await CreateEvaluator(db).EvaluateBatchAsync([world.Request.Id, second.Id]);

        Assert.Equal(2, decisions.Count);
        Assert.All(decisions.Values, decision =>
        {
            Assert.Equal(SchedulingReasonCodes.Eligible, decision.Status.Code);
            Assert.Null(decision.CandidateNodeId);
        });
    }

    [Fact]
    public async Task Terminal_assignment_is_retained_as_a_token_free_projection()
    {
        await using var db = CreateContext();
        var world = SeedWorld(db);
        CompleteRequest(world.Request);
        var assignedAt = _clock.GetUtcNow().AddMinutes(-10);
        var terminalAt = _clock.GetUtcNow().AddMinutes(-1);
        var assignment = ExecutionAssignment.Rehydrate(
            world.Request.Id,
            world.Project.Id,
            world.Binding.Id,
            world.Node.Id,
            world.Binding.CanonicalRepositoryPath!,
            world.Project.DefaultBranch,
            world.Binding.ValidationRevision,
            ExecutionAssignmentState.Completed,
            "secret-claim-token",
            assignedAt,
            assignedAt.AddMinutes(5),
            lastRenewedAt: assignedAt.AddMinutes(1),
            lastReconciledAt: null,
            terminalAt,
            version: 4);
        db.ExecutionAssignments.Add(assignment);
        await SaveAsync(db);

        var decision = await CreateEvaluator(db).EvaluateAsync(world.Request.Id);

        var projection = Assert.IsType<ExecutionAssignmentProjectionDto>(decision.Assignment);
        Assert.Equal(world.Request.Id.Value, projection.RequestId);
        Assert.Equal(world.Binding.Id.Value, projection.WorkspaceBindingId);
        Assert.Equal(ExecutionAssignmentState.Completed, projection.State);
        Assert.Equal(terminalAt, projection.TerminalAt);
        Assert.Null(typeof(ExecutionAssignmentProjectionDto).GetProperty("ClaimToken"));
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

    private RequestEligibilityEvaluator CreateEvaluator(ControlPlaneDbContext db) => new(
        _clock,
        Options.Create(new NodeLivenessOptions { HeartbeatSeconds = 10 }),
        db);

    private Project SeedProject(
        ControlPlaneDbContext db,
        bool enabled = true,
        int maxReadOnlyRequests = 4,
        int maxActiveWriteRequests = 2)
    {
        var project = Project.Register(
            "Project " + Guid.NewGuid().ToString("N")[..6],
            "main",
            enabled,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: false,
            _clock.GetUtcNow());
        db.Projects.Add(project);
        return project;
    }

    private WorkRequest SeedRequest(
        ControlPlaneDbContext db,
        Project project,
        WorkRequestKind kind = WorkRequestKind.Development)
    {
        var request = WorkRequest.Enqueue(
            project.Id,
            kind,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "Request " + Guid.NewGuid().ToString("N")[..6],
            "Do the thing",
            _clock.GetUtcNow());
        db.WorkRequests.Add(request);
        return request;
    }

    private FleetNode SeedNode(
        ControlPlaneDbContext db,
        NodeStatus status,
        string? executionStatusJson,
        DateTimeOffset? heartbeatAt = null)
    {
        var now = _clock.GetUtcNow();
        var node = FleetNode.Rehydrate(
            NodeId.New(),
            "node-" + Guid.NewGuid().ToString("N")[..6],
            "1.0.0",
            status,
            heartbeatAt ?? now,
            "{}",
            now.AddMinutes(-10),
            now,
            version: 1,
            executionStatusJson: executionStatusJson);
        db.FleetNodes.Add(node);
        return node;
    }

    private WorkspaceBinding SeedBinding(
        ControlPlaneDbContext db,
        Project project,
        NodeId nodeId,
        WorkspaceBindingStatus status,
        string validationCode = WorkspaceBinding.ValidValidationCode,
        string validationDetail = "Workspace is valid.")
    {
        var path = Path.Combine(Path.GetTempPath(), "pi-cc-eligibility", Guid.NewGuid().ToString("N"));
        var binding = WorkspaceBinding.Designate(project.Id, nodeId, path, _clock.GetUtcNow());
        if (status != WorkspaceBindingStatus.PendingValidation)
        {
            Assert.True(binding.ApplyValidationResult(
                nodeId,
                binding.ValidationRevision,
                status,
                validationCode,
                validationDetail,
                status == WorkspaceBindingStatus.Valid ? path : null,
                _clock.GetUtcNow()));
        }

        db.WorkspaceBindings.Add(binding);
        return binding;
    }

    private World SeedWorld(
        ControlPlaneDbContext db,
        WorkRequestKind requestKind = WorkRequestKind.Development,
        bool enabled = true,
        int maxReadOnlyRequests = 4,
        int maxActiveWriteRequests = 2,
        NodeStatus nodeStatus = NodeStatus.Online,
        DateTimeOffset? heartbeatAt = null,
        string? executionStatusJson = null,
        bool omitExecutionStatus = false)
    {
        var project = SeedProject(db, enabled, maxReadOnlyRequests, maxActiveWriteRequests);
        var request = SeedRequest(db, project, requestKind);
        var node = SeedNode(
            db,
            nodeStatus,
            omitExecutionStatus ? null : executionStatusJson ?? SerializeStatus(),
            heartbeatAt);
        var binding = SeedBinding(db, project, node.Id, WorkspaceBindingStatus.Valid);
        return new World(project, request, node, binding);
    }

    private ExecutionAssignment SeedAssignment(
        ControlPlaneDbContext db,
        Project project,
        WorkspaceBinding binding,
        NodeId nodeId,
        WorkRequestKind kind,
        ExecutionAssignmentState state,
        bool leaseExpired = false)
    {
        var request = SeedRequest(db, project, kind);
        request.Start(_clock.GetUtcNow());
        var assignedAt = _clock.GetUtcNow().AddMinutes(-10);
        var leaseExpiresAt = leaseExpired
            ? _clock.GetUtcNow().AddMinutes(-5)
            : _clock.GetUtcNow().AddMinutes(5);
        var assignment = ExecutionAssignment.Rehydrate(
            request.Id,
            project.Id,
            binding.Id,
            nodeId,
            binding.CanonicalRepositoryPath!,
            project.DefaultBranch,
            binding.ValidationRevision,
            state,
            "claim-" + Guid.NewGuid().ToString("N"),
            assignedAt,
            leaseExpiresAt,
            lastRenewedAt: null,
            lastReconciledAt: null,
            terminalAt: null,
            version: 1);
        db.ExecutionAssignments.Add(assignment);
        return assignment;
    }

    private void SetExecutionStatus(FleetNode node, string statusJson) => node.Heartbeat(
        "1.0.0",
        "{}",
        _clock.GetUtcNow(),
        executionStatusJson: statusJson);

    private string SerializeStatus(
        int availableSlots = 1,
        IReadOnlyList<string>? readiness = null,
        DateTimeOffset? statusAt = null,
        DateTimeOffset? routeAt = null,
        IReadOnlyList<Guid>? activeAssignmentIds = null)
    {
        var observedAt = statusAt ?? _clock.GetUtcNow();
        var routeObservedAt = routeAt ?? _clock.GetUtcNow();
        var routeReadiness = readiness ?? [RuntimeReadinessStatuses.Ready];
        var status = new NodeExecutionStatusDto(
            observedAt,
            availableSlots,
            activeAssignmentIds ?? [],
            "routing-v1",
            routeReadiness.Select((value, index) => new RuntimeRouteReadinessDto(
                "role-" + index,
                "codex/default",
                value,
                "native-observation",
                routeObservedAt,
                "routing-v1")).ToArray());
        return JsonSerializer.Serialize(status, TestRepositories.WebJson);
    }

    private static void CompleteRequest(WorkRequest request)
    {
        var at = request.CreatedAt.AddMinutes(1);
        request.Start(at);
        request.BeginPlanning(at);
        request.BeginExecuting(at);
        request.BeginReviewing(at);
        request.BeginVerifying(at);
        request.Complete(at);
    }

    private static void AssertReason(EligibilityDecision decision, string expectedCode)
    {
        Assert.Equal(expectedCode, decision.Status.Code);
        Assert.False(decision.Status.IsEligible);
        Assert.Null(decision.EligibleBinding);
    }

    private static async Task SaveAsync(ControlPlaneDbContext db)
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    private sealed record World(
        Project Project,
        WorkRequest Request,
        FleetNode Node,
        WorkspaceBinding Binding);
}
