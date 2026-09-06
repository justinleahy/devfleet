using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure.Nodes;

/// <summary>
/// EF Core backed node registry. Registration is an upsert keyed by the node's stable id, so a
/// node reconnecting after a restart refreshes its identity instead of forking a new row.
/// </summary>
public sealed class NodeRegistry(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier,
    VerificationPolicyUpgradeMigrator? policyUpgradeMigrator = null) : INodeRegistry
{
    private const int MaxAssignmentCount = 200;
    private const int MaxRouteCount = 200;
    private const int MaxVerificationProfileCount = 32;
    private const int MaxVerificationCommandCount = 32;
    private const int MaxStatusTextLength = 128;
    private const int MaxExecutionStatusJsonLength = 131072;
    private const int MinVerificationTimeoutSeconds = 1;
    private const int MaxVerificationTimeoutSeconds = 86_400;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<NodeDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await db.FleetNodes
            .OrderBy(n => n.DisplayName)
            .ThenBy(n => n.Id)
            .ToListAsync(cancellationToken);

        return nodes.Select(ToDto).ToList();
    }

    public async Task<NodeDto?> GetAsync(NodeId id, CancellationToken cancellationToken = default)
    {
        var node = await db.FleetNodes
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

        return node is null ? null : ToDto(node);
    }

    public async Task<NodeDto> RegisterAsync(
        RegisterNodeCommand command,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.FleetNodes
            .SingleOrDefaultAsync(n => n.Id == command.Id, cancellationToken);

        if (existing is null)
        {
            var node = FleetNode.Register(
                command.Id,
                command.DisplayName,
                command.AgentVersion,
                command.CapabilitiesJson,
                at);

            db.FleetNodes.Add(node);
            await db.SaveChangesAsync(cancellationToken);
            notifier.Publish(ProjectionChange.Fleet());
            return ToDto(node);
        }

        // Re-registration from a live node refreshes identity metadata and brings it online.
        existing.RefreshRegistration(command.DisplayName, command.AgentVersion, command.CapabilitiesJson, at);
        var eligibilityChanges = await GetEligibilityChangesAsync(existing.Id, cancellationToken);
        await SaveWithConcurrencyRetryAsync(
            existing,
            now => existing.RefreshRegistration(command.DisplayName, command.AgentVersion, command.CapabilitiesJson, now),
            cancellationToken);
        PublishFleetAndEligibilityChanges(eligibilityChanges);
        return ToDto(existing);
    }

    public async Task<NodeDto> HeartbeatAsync(
        NodeHeartbeatCommand command,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var resourceSnapshotJson = SerializeResources(command.Resources);
        var executionStatusJson = SerializeExecutionStatus(command.ExecutionStatus);
        var node = await db.FleetNodes
            .SingleOrDefaultAsync(n => n.Id == command.Id, cancellationToken)
            ?? throw new NodeNotFoundException(command.Id);

        node.Heartbeat(
            node.AgentVersion,
            node.CapabilitiesJson,
            at,
            resourceSnapshotJson,
            executionStatusJson);
        var eligibilityChanges = await GetEligibilityChangesAsync(node.Id, cancellationToken);
        await SaveWithConcurrencyRetryAsync(
            node,
            now => node.Heartbeat(
                node.AgentVersion,
                node.CapabilitiesJson,
                now,
                resourceSnapshotJson,
                executionStatusJson),
            cancellationToken);
        if (command.ExecutionStatus?.VerificationPolicy is { } catalog
            && policyUpgradeMigrator is not null)
        {
            await policyUpgradeMigrator.MigrateAfterHeartbeatAsync(
                command.Id,
                catalog,
                cancellationToken);
        }
        PublishFleetAndEligibilityChanges(eligibilityChanges);
        return ToDto(node);
    }

    public async Task MarkStaleOfflineAsync(
        NodeId id,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var node = await db.FleetNodes
            .SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (node is null)
        {
            return;
        }

        node.MarkOffline(at);
        if (db.Entry(node).State == EntityState.Modified)
        {
            var eligibilityChanges = await GetEligibilityChangesAsync(node.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            PublishFleetAndEligibilityChanges(eligibilityChanges);
        }
    }

    private async Task<IReadOnlyList<ProjectionChange>> GetEligibilityChangesAsync(
        NodeId nodeId,
        CancellationToken cancellationToken)
    {
        var projectIds = await db.WorkspaceBindings
            .AsNoTracking()
            .Where(binding => binding.NodeId == nodeId)
            .Select(binding => binding.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var queuedRequests = await db.WorkRequests
            .AsNoTracking()
            .Where(request => projectIds.Contains(request.ProjectId)
                && request.Status == WorkRequestStatus.Queued)
            .Select(request => new { request.ProjectId, RequestId = request.Id })
            .ToListAsync(cancellationToken);
        var changes = new List<ProjectionChange>(projectIds.Count + queuedRequests.Count);
        foreach (var projectId in projectIds)
        {
            changes.Add(ProjectionChange.Project(projectId.Value));
        }

        foreach (var request in queuedRequests)
        {
            changes.Add(ProjectionChange.Request(
                request.ProjectId.Value,
                request.RequestId.Value));
        }

        return changes;
    }

    private void PublishFleetAndEligibilityChanges(IReadOnlyList<ProjectionChange> changes)
    {
        notifier.Publish(ProjectionChange.Fleet());
        foreach (var change in changes)
        {
            notifier.Publish(change);
        }
    }

    /// <summary>
    /// Re-reads the current row on an optimistic-concurrency loss and replays the mutation,
    /// so a racing heartbeat cannot fail a registration or heartbeat call.
    /// </summary>
    private async Task SaveWithConcurrencyRetryAsync(
        FleetNode node,
        Action<DateTimeOffset> replay,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(node).Reload();
                replay(clock.GetUtcNow());
            }
        }
    }

    private static NodeDto ToDto(FleetNode node) => new(
        node.Id.Value,
        node.DisplayName,
        node.AgentVersion,
        node.LastHeartbeatAt,
        node.Status,
        node.CapabilitiesJson,
        node.Version,
        DeserializeResources(node.ResourceSnapshotJson),
        DeserializeExecutionStatus(node.ExecutionStatusJson));

    private static string? SerializeResources(NodeResourceSnapshotDto? resources)
    {
        if (resources is null)
        {
            return null;
        }

        ValidateResources(resources);
        return JsonSerializer.Serialize(resources, SnapshotJsonOptions);
    }

    private static NodeResourceSnapshotDto? DeserializeResources(string? resourceSnapshotJson)
    {
        if (resourceSnapshotJson is null)
        {
            return null;
        }

        try
        {
            var resources = JsonSerializer.Deserialize<NodeResourceSnapshotDto>(
                resourceSnapshotJson,
                SnapshotJsonOptions);
            return resources is not null && ResourcesAreValid(resources) ? resources : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SerializeExecutionStatus(NodeExecutionStatusDto? executionStatus)
    {
        if (executionStatus is null)
        {
            return null;
        }

        if (!ExecutionStatusIsValid(executionStatus))
        {
            throw new ArgumentException("Execution status values are invalid.", nameof(executionStatus));
        }

        var json = JsonSerializer.Serialize(executionStatus, SnapshotJsonOptions);
        if (json.Length > MaxExecutionStatusJsonLength)
        {
            throw new ArgumentException("Execution status is too large.", nameof(executionStatus));
        }

        return json;
    }

    private static NodeExecutionStatusDto? DeserializeExecutionStatus(string? executionStatusJson)
    {
        if (executionStatusJson is null || executionStatusJson.Length > MaxExecutionStatusJsonLength)
        {
            return null;
        }

        try
        {
            var executionStatus = JsonSerializer.Deserialize<NodeExecutionStatusDto>(
                executionStatusJson,
                SnapshotJsonOptions);
            return executionStatus is not null && ExecutionStatusIsValid(executionStatus)
                ? executionStatus
                : null;
        }
        catch (JsonException)
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
            || !IsBoundedStableText(status.RoutingRevision)
            || status.Routes is null
            || status.Routes.Count > MaxRouteCount)
        {
            return false;
        }

        var assignmentIds = new HashSet<Guid>();
        foreach (var assignmentId in status.ActiveAssignmentIds)
        {
            if (assignmentId == Guid.Empty || !assignmentIds.Add(assignmentId))
            {
                return false;
            }
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

        return VerificationPolicyIsValid(status.VerificationPolicy);
    }

    private static bool VerificationPolicyIsValid(VerificationPolicyCatalogMessage? policy)
    {
        if (policy is null)
        {
            return true;
        }

        if (policy.ObservedAt.Offset != TimeSpan.Zero
            || !policy.BaselineAvailable
            || !IsBoundedStableText(policy.BaselineVersion)
            || policy.Profiles is null
            || policy.Profiles.Count > MaxVerificationProfileCount)
        {
            return false;
        }

        var profileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in policy.Profiles)
        {
            if (profile is null
                || !IsBoundedStableText(profile.Id)
                || !IsBoundedStableText(profile.Revision)
                || !IsBoundedStableText(profile.DisplayLabel)
                || string.Equals(profile.Id, VerificationBaselineIds.ProfileId, StringComparison.Ordinal)
                || profile.Commands is null
                || profile.Commands.Count is 0 or > MaxVerificationCommandCount
                || !profileIds.Add(profile.Id))
            {
                return false;
            }

            var commandIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in profile.Commands)
            {
                if (command is null
                    || !IsBoundedStableText(command.Id)
                    || VerificationBaselineIds.IsReservedCommandId(command.Id)
                    || !IsBoundedStableText(command.DisplayLabel)
                    || !IsBoundedStableText(command.WorkingDirectoryLabel)
                    || command.TimeoutSeconds is < MinVerificationTimeoutSeconds
                        or > MaxVerificationTimeoutSeconds
                    || !commandIds.Add(command.Id))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool RouteIsValid(RuntimeRouteReadinessDto route, string routingRevision) =>
        IsBoundedCanonicalText(route.Role)
        && route.CanonicalModel is { Length: <= MaxStatusTextLength }
        && AgentModelSelector.TryParse(route.CanonicalModel, out var model)
        && model.Value == route.CanonicalModel
        && route.Readiness is "ready" or "unavailable" or "unknown"
        && IsBoundedStableText(route.EvidenceSource)
        && route.ObservedAt.Offset == TimeSpan.Zero
        && string.Equals(route.RoutingRevision, routingRevision, StringComparison.Ordinal);

    private static bool IsBoundedCanonicalText(string? value) =>
        value is { Length: > 0 and <= MaxStatusTextLength }
        && !char.IsWhiteSpace(value[0])
        && !char.IsWhiteSpace(value[^1]);

    private static bool IsBoundedStableText(string? value) =>
        IsBoundedCanonicalText(value);

    private static void ValidateResources(NodeResourceSnapshotDto resources)
    {
        if (!ResourcesAreValid(resources))
        {
            throw new ArgumentException("Resource snapshot values are invalid.", nameof(resources));
        }
    }

    private static bool ResourcesAreValid(NodeResourceSnapshotDto resources) =>
        resources.ObservedAt.Offset == TimeSpan.Zero
        && IsValidPercentage(resources.CpuUsagePercent)
        && IsValidNonNegative(resources.LoadAverageOneMinute)
        && IsValidNonNegative(resources.UptimeSeconds)
        && IsValidBytePair(resources.MemoryUsedBytes, resources.MemoryTotalBytes)
        && IsValidBytePair(resources.DiskUsedBytes, resources.DiskTotalBytes);

    private static bool IsValidPercentage(double? value) =>
        value is not { } number || (double.IsFinite(number) && number is >= 0 and <= 100);

    private static bool IsValidNonNegative(double? value) =>
        value is not { } number || (double.IsFinite(number) && number >= 0);

    private static bool IsValidBytePair(long? used, long? total)
    {
        if (used is < 0 || total is <= 0)
        {
            return false;
        }

        return used is not { } usedBytes
            || total is not { } totalBytes
            || usedBytes <= totalBytes;
    }
}
