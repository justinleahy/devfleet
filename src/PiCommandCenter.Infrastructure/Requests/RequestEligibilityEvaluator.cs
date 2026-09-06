using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// Computes scheduling eligibility from persisted control-plane state without claiming work.
/// </summary>
public sealed class RequestEligibilityEvaluator : IRequestEligibilityEvaluator
{
    private const int MaxAssignmentCount = 200;
    private const int MaxRouteCount = 200;
    private const int MaxStatusTextLength = 128;
    private const int MaxExecutionStatusJsonLength = 131072;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _clock;
    private readonly ControlPlaneDbContext _db;
    private readonly TimeSpan _staleAfter;

    public RequestEligibilityEvaluator(
        TimeProvider clock,
        IOptions<NodeLivenessOptions> options,
        ControlPlaneDbContext db)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(db);

        _clock = clock;
        _db = db;
        _staleAfter = options.Value.StaleAfter;
    }

    public async Task<IReadOnlyDictionary<WorkRequestId, EligibilityDecision>> EvaluateBatchAsync(
        IReadOnlyCollection<WorkRequestId> requestIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestIds);

        var ids = requestIds.Distinct().ToArray();
        if (ids.Any(id => id.Value == Guid.Empty))
        {
            throw new ArgumentException("Request ids must not be empty.", nameof(requestIds));
        }

        if (ids.Length == 0)
        {
            return new Dictionary<WorkRequestId, EligibilityDecision>();
        }

        return await EvaluateCoreAsync(ids, candidateNodeId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EligibilityDecision> EvaluateAsync(
        WorkRequestId requestId,
        NodeId? candidateNodeId = null,
        CancellationToken cancellationToken = default)
    {
        if (requestId.Value == Guid.Empty)
        {
            throw new ArgumentException("Request id must not be empty.", nameof(requestId));
        }

        var decisions = await EvaluateCoreAsync([requestId], candidateNodeId, cancellationToken)
            .ConfigureAwait(false);
        return decisions[requestId];
    }

    private async Task<IReadOnlyDictionary<WorkRequestId, EligibilityDecision>> EvaluateCoreAsync(
        WorkRequestId[] requestIds,
        NodeId? candidateNodeId,
        CancellationToken cancellationToken)
    {
        var requests = await _db.WorkRequests
            .AsNoTracking()
            .Where(request => requestIds.Contains(request.Id))
            .Select(request => new RequestSnapshot(request.Id, request.ProjectId, request.Kind))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (requests.Count != requestIds.Length)
        {
            var foundIds = requests.Select(request => request.Id).ToHashSet();
            var missingId = requestIds.First(id => !foundIds.Contains(id));
            throw new RequestNotFoundException(missingId);
        }

        var projectIds = requests.Select(request => request.ProjectId).Distinct().ToArray();
        var projects = await _db.Projects
            .AsNoTracking()
            .Where(project => projectIds.Contains(project.Id))
            .Select(project => new ProjectSnapshot(
                project.Id,
                project.Enabled,
                project.MaxReadOnlyRequests))
            .ToDictionaryAsync(project => project.Id, cancellationToken)
            .ConfigureAwait(false);

        var bindings = await _db.WorkspaceBindings
            .AsNoTracking()
            .Where(binding => projectIds.Contains(binding.ProjectId))
            .ToDictionaryAsync(binding => binding.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        var nodeIds = bindings.Values.Select(binding => binding.NodeId).Distinct().ToArray();
        var nodes = await _db.FleetNodes
            .AsNoTracking()
            .Where(node => nodeIds.Contains(node.Id))
            .Select(node => new NodeSnapshot(
                node.Id,
                node.Status,
                node.LastHeartbeatAt,
                node.ExecutionStatusJson))
            .ToDictionaryAsync(node => node.Id, cancellationToken)
            .ConfigureAwait(false);

        var assignmentProjections = await _db.ExecutionAssignments
            .AsNoTracking()
            .Where(assignment => requestIds.Contains(assignment.RequestId))
            .Select(assignment => new ExecutionAssignmentProjectionDto(
                assignment.RequestId.Value,
                assignment.ProjectId.Value,
                assignment.WorkspaceBindingId.Value,
                assignment.NodeIdSnapshot.Value,
                assignment.CanonicalRepositoryPathSnapshot,
                assignment.DefaultBranchSnapshot,
                assignment.BindingValidationRevisionSnapshot,
                assignment.State,
                assignment.AssignedAt,
                assignment.LeaseExpiresAt,
                assignment.LastRenewedAt,
                assignment.LastReconciledAt,
                assignment.TerminalAt))
            .ToDictionaryAsync(
                assignment => new WorkRequestId(assignment.RequestId),
                cancellationToken)
            .ConfigureAwait(false);

        var activeAssignments = await (
                from assignment in _db.ExecutionAssignments.AsNoTracking()
                join assignedRequest in _db.WorkRequests.AsNoTracking()
                    on assignment.RequestId equals assignedRequest.Id
                where (projectIds.Contains(assignment.ProjectId)
                        || nodeIds.Contains(assignment.NodeIdSnapshot))
                    && assignment.State != ExecutionAssignmentState.Completed
                    && assignment.State != ExecutionAssignmentState.Failed
                    && assignment.State != ExecutionAssignmentState.Cancelled
                select new ActiveAssignmentSnapshot(
                    assignment.RequestId,
                    assignment.ProjectId,
                    assignment.NodeIdSnapshot,
                    assignedRequest.Kind))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var activeByProject = activeAssignments
            .GroupBy(assignment => assignment.ProjectId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var activeByNode = activeAssignments
            .GroupBy(assignment => assignment.NodeId)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var now = _clock.GetUtcNow().ToUniversalTime();
        var decisions = new Dictionary<WorkRequestId, EligibilityDecision>(requests.Count);

        foreach (var request in requests)
        {
            assignmentProjections.TryGetValue(request.Id, out var assignment);
            var projectAssignments = activeByProject.GetValueOrDefault(request.ProjectId) ?? [];
            decisions.Add(
                request.Id,
                Evaluate(
                    request,
                    candidateNodeId,
                    projects.GetValueOrDefault(request.ProjectId),
                    bindings.GetValueOrDefault(request.ProjectId),
                    nodes,
                    activeByNode,
                    projectAssignments,
                    assignment,
                    now));
        }

        return decisions;
    }

    private EligibilityDecision Evaluate(
        RequestSnapshot request,
        NodeId? candidateNodeId,
        ProjectSnapshot? project,
        WorkspaceBinding? binding,
        IReadOnlyDictionary<NodeId, NodeSnapshot> nodes,
        IReadOnlyDictionary<NodeId, ActiveAssignmentSnapshot[]> activeByNode,
        IReadOnlyList<ActiveAssignmentSnapshot> projectAssignments,
        ExecutionAssignmentProjectionDto? assignment,
        DateTimeOffset now)
    {
        EligibilityDecision Ineligible(string code, string detail, string action) => new(
            request.Id,
            candidateNodeId,
            new SchedulingStatusDto(code, detail, action, IsEligible: false),
            EligibleBinding: null,
            assignment);

        if (project is null || !project.Enabled)
        {
            return Ineligible(
                SchedulingReasonCodes.ProjectDisabled,
                "Project execution is disabled.",
                "Enable the project to allow scheduling.");
        }

        if (binding is null)
        {
            return Ineligible(
                SchedulingReasonCodes.WorkspaceBindingMissing,
                "No workspace is designated for this project.",
                "Designate a workspace.");
        }

        if (binding.Status == WorkspaceBindingStatus.PendingValidation)
        {
            return Ineligible(
                SchedulingReasonCodes.WorkspaceValidationPending,
                "Workspace validation is pending.",
                "Wait for validation or reconnect the designated node.");
        }

        if (!BindingIsValid(binding))
        {
            return Ineligible(
                SchedulingReasonCodes.WorkspaceInvalid,
                SafeValidationDetail(binding.ValidationDetail),
                string.Equals(
                    binding.ValidationCode,
                    WorkspaceValidationCodes.PathMissing,
                    StringComparison.Ordinal)
                    ? "Restore or redesignate the workspace path, then revalidate."
                    : "Fix the workspace validation failure, then revalidate.");
        }

        if (candidateNodeId is { } candidate && candidate != binding.NodeId)
        {
            return Ineligible(
                SchedulingReasonCodes.NodeOffline,
                "The candidate node is not the workspace's designated node.",
                "Use or reconnect the designated node.");
        }

        if (!nodes.TryGetValue(binding.NodeId, out var node)
            || node.Status != NodeStatus.Online
            || !IsFreshUtc(node.LastHeartbeatAt, now))
        {
            return Ineligible(
                SchedulingReasonCodes.NodeOffline,
                "The designated node is offline or its heartbeat is stale.",
                "Reconnect the designated node.");
        }

        var executionStatus = ParseExecutionStatus(node.ExecutionStatusJson);
        if (executionStatus is null
            || executionStatus.Routes.Count == 0
            || !IsFreshUtc(executionStatus.ObservedAt, now))
        {
            return Ineligible(
                SchedulingReasonCodes.RuntimeUnknown,
                "Runtime readiness is missing, stale, or invalid.",
                "Wait for fresh runtime readiness or inspect the node configuration.");
        }

        var hasUnavailableRoute = executionStatus.Routes.Any(route =>
            IsFreshUtc(route.ObservedAt, now)
            && string.Equals(route.Readiness, RuntimeReadinessStatuses.Unavailable, StringComparison.Ordinal));
        if (hasUnavailableRoute)
        {
            return Ineligible(
                SchedulingReasonCodes.RuntimeUnavailable,
                "At least one required runtime route is unavailable.",
                "Fix node routing or provider-native authentication.");
        }

        if (executionStatus.Routes.Any(route =>
                !IsFreshUtc(route.ObservedAt, now)
                || !string.Equals(route.Readiness, RuntimeReadinessStatuses.Ready, StringComparison.Ordinal)))
        {
            return Ineligible(
                SchedulingReasonCodes.RuntimeUnknown,
                "Runtime readiness is stale or unknown.",
                "Wait for fresh runtime readiness or inspect the node configuration.");
        }

        var advertisedAssignments = executionStatus.ActiveAssignmentIds.ToHashSet();
        var unadvertisedAssignments = activeByNode
            .GetValueOrDefault(binding.NodeId)?
            .Count(active => !advertisedAssignments.Contains(active.RequestId.Value)) ?? 0;
        if (executionStatus.AvailableRequestSlots <= unadvertisedAssignments)
        {
            return Ineligible(
                SchedulingReasonCodes.NodeCapacityUnavailable,
                "The designated node has no available request slots.",
                "Wait for node capacity or adjust trusted node capacity.");
        }

        var projectCapacityOccupied = request.Kind == WorkRequestKind.Development
            ? projectAssignments.Any(active => active.Kind == WorkRequestKind.Development)
            : projectAssignments.Count(active => active.Kind is WorkRequestKind.Analysis or WorkRequestKind.Review)
                >= project.MaxReadOnlyRequests;
        if (projectCapacityOccupied)
        {
            return Ineligible(
                SchedulingReasonCodes.ProjectConcurrencyUnavailable,
                request.Kind == WorkRequestKind.Development
                    ? "The project's single development slot is occupied."
                    : "The project's read-only request limit is occupied.",
                "Wait, cancel, or recover active project work.");
        }

        return new EligibilityDecision(
            request.Id,
            candidateNodeId,
            new SchedulingStatusDto(
                SchedulingReasonCodes.Eligible,
                "The request is eligible for assignment.",
                "No action is required.",
                IsEligible: true),
            ToDto(binding),
            assignment);
    }

    private bool IsFreshUtc(DateTimeOffset observedAt, DateTimeOffset now) =>
        observedAt.Offset == TimeSpan.Zero
        && observedAt <= now
        && now - observedAt <= _staleAfter;

    private static bool BindingIsValid(WorkspaceBinding binding) =>
        binding.Status == WorkspaceBindingStatus.Valid
        && binding.ValidationRevision > 0
        && binding.ValidatedAt is not null
        && string.Equals(binding.ValidationCode, WorkspaceBinding.ValidValidationCode, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(binding.CanonicalRepositoryPath)
        && Path.IsPathFullyQualified(binding.CanonicalRepositoryPath);

    private static string SafeValidationDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return "Workspace validation failed.";
        }

        var trimmed = detail.Trim();
        return trimmed.Length <= WorkspaceBinding.MaxValidationDetailLength
            && !trimmed.Any(char.IsControl)
                ? trimmed
                : "Workspace validation failed.";
    }

    private static NodeExecutionStatusDto? ParseExecutionStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxExecutionStatusJsonLength)
        {
            return null;
        }

        try
        {
            var status = JsonSerializer.Deserialize<NodeExecutionStatusDto>(json, SnapshotJsonOptions);
            return status is not null && ExecutionStatusIsValid(status) ? status : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static bool ExecutionStatusIsValid(NodeExecutionStatusDto status)
    {
        if (status.ObservedAt.Offset != TimeSpan.Zero
            || status.AvailableRequestSlots < 0
            || status.ActiveAssignmentIds is null
            || status.ActiveAssignmentIds.Count > MaxAssignmentCount
            || !IsBoundedText(status.RoutingRevision)
            || status.Routes is null
            || status.Routes.Count > MaxRouteCount)
        {
            return false;
        }

        var assignmentIds = new HashSet<Guid>();
        if (status.ActiveAssignmentIds.Any(id => id == Guid.Empty || !assignmentIds.Add(id)))
        {
            return false;
        }

        var routeKeys = new HashSet<(string Role, string CanonicalModel)>();
        foreach (var route in status.Routes)
        {
            if (route is null
                || !RouteIsValid(route, status.RoutingRevision)
                || !routeKeys.Add((route.Role, route.CanonicalModel)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RouteIsValid(RuntimeRouteReadinessDto route, string routingRevision) =>
        IsBoundedText(route.Role)
        && route.CanonicalModel is { Length: <= MaxStatusTextLength }
        && AgentModelSelector.TryParse(route.CanonicalModel, out var model)
        && string.Equals(model.Value, route.CanonicalModel, StringComparison.Ordinal)
        && route.Readiness is RuntimeReadinessStatuses.Ready
            or RuntimeReadinessStatuses.Unavailable
            or RuntimeReadinessStatuses.Unknown
        && IsBoundedText(route.EvidenceSource)
        && route.ObservedAt.Offset == TimeSpan.Zero
        && string.Equals(route.RoutingRevision, routingRevision, StringComparison.Ordinal);

    private static bool IsBoundedText(string? value) =>
        value is { Length: > 0 and <= MaxStatusTextLength }
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);

    private static WorkspaceBindingDto ToDto(WorkspaceBinding binding) => new(
        binding.Id.Value,
        binding.ProjectId.Value,
        binding.NodeId.Value,
        binding.RepositoryPath,
        binding.CanonicalRepositoryPath,
        binding.Status,
        binding.ValidationRevision,
        binding.ValidationCode,
        binding.ValidationDetail,
        binding.ValidatedAt,
        binding.CreatedAt,
        binding.UpdatedAt,
        binding.Version);

    private sealed record RequestSnapshot(
        WorkRequestId Id,
        ProjectId ProjectId,
        WorkRequestKind Kind);

    private sealed record ProjectSnapshot(
        ProjectId Id,
        bool Enabled,
        int MaxReadOnlyRequests);

    private sealed record NodeSnapshot(
        NodeId Id,
        NodeStatus Status,
        DateTimeOffset LastHeartbeatAt,
        string? ExecutionStatusJson);

    private sealed record ActiveAssignmentSnapshot(
        WorkRequestId RequestId,
        ProjectId ProjectId,
        NodeId NodeId,
        WorkRequestKind Kind);
}
