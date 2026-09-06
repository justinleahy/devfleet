using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Projects;

/// <summary>Persists each project's sole revisioned node-local workspace designation.</summary>
public sealed class WorkspaceBindingCatalog(
    ControlPlaneDbContext db,
    IWorkspaceValidationGateway validationGateway,
    IProjectionNotifier notifier) : IWorkspaceBindingCatalog
{
    public async Task<WorkspaceBindingDto?> GetAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureProjectExistsAsync(projectId, cancellationToken);

        var binding = await db.WorkspaceBindings
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken);
        return binding is null ? null : ToDto(binding);
    }

    public async Task<WorkspaceBindingDto> DesignateAsync(
        ProjectId projectId,
        DesignateWorkspaceBindingCommand command,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await EnsureProjectExistsAsync(projectId, cancellationToken);

        if (!await db.FleetNodes.AnyAsync(node => node.Id == command.NodeId, cancellationToken))
        {
            throw new NodeNotFoundException(command.NodeId);
        }

        var designation = WorkspaceBinding.Designate(
            projectId,
            command.NodeId,
            command.RepositoryPath,
            at);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var conflict = await db.WorkspaceBindings.AnyAsync(
            binding => binding.ProjectId != projectId
                && binding.NodeId == designation.NodeId
                && binding.RepositoryPath == designation.RepositoryPath,
            cancellationToken);
        if (conflict)
        {
            throw new WorkspaceBindingConflictException(designation.NodeId, designation.RepositoryPath);
        }

        var binding = await db.WorkspaceBindings
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken);
        if (binding is null)
        {
            binding = designation;
            db.WorkspaceBindings.Add(binding);
        }
        else
        {
            await ThrowIfBindingInUseAsync(binding.Id, cancellationToken);
            binding.Redesignate(designation.NodeId, designation.RepositoryPath, at);
        }

        var eligibilityChanges = await GetEligibilityChangesAsync(projectId, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            RestoreUncommittedBinding(binding);
            throw new WorkspaceBindingConflictException(designation.NodeId, designation.RepositoryPath);
        }

        PublishEligibilityChanges(eligibilityChanges);
        return ToDto(binding);
    }

    public async Task<WorkspaceBindingDto> ValidateAsync(
        ProjectId projectId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .SingleOrDefaultAsync(candidate => candidate.Id == projectId, cancellationToken)
            ?? throw new ProjectNotFoundException(projectId.Value);
        var binding = await db.WorkspaceBindings
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project '{projectId}' does not have a workspace binding.");

        var validatingNodeId = binding.NodeId;
        var request = new WorkspaceBindingValidationRequestMessage(
            binding.Id.Value,
            projectId.Value,
            binding.ValidationRevision,
            binding.RepositoryPath,
            project.DefaultBranch);
        var result = await validationGateway.ValidateAsync(
            validatingNodeId.Value,
            request,
            cancellationToken);
        if (result is null)
        {
            return ToDto(binding);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Entry(binding).ReloadAsync(cancellationToken);
        if (result.BindingId != request.BindingId
            || result.ProjectId != request.ProjectId
            || result.Revision != request.Revision
            || binding.Id.Value != request.BindingId
            || binding.ProjectId.Value != request.ProjectId
            || binding.NodeId != validatingNodeId
            || binding.ValidationRevision != request.Revision
            || !TryMapStatus(result.Status, out var status))
        {
            return ToDto(binding);
        }

        binding.ApplyValidationResult(
            validatingNodeId,
            result.Revision,
            status,
            result.ValidationCode,
            result.Detail,
            result.CanonicalRepositoryPath,
            at);
        if (binding.CanonicalRepositoryPath is { } canonicalRepositoryPath
            && await db.WorkspaceBindings.AnyAsync(
                candidate => candidate.Id != binding.Id
                    && candidate.NodeId == validatingNodeId
                    && candidate.CanonicalRepositoryPath == canonicalRepositoryPath,
                cancellationToken))
        {
            RestoreUncommittedBinding(binding);
            throw new WorkspaceBindingConflictException(validatingNodeId, canonicalRepositoryPath);
        }

        var eligibilityChanges = await GetEligibilityChangesAsync(projectId, cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var conflictPath = binding.CanonicalRepositoryPath ?? binding.RepositoryPath;
            RestoreUncommittedBinding(binding);
            throw new WorkspaceBindingConflictException(validatingNodeId, conflictPath);
        }

        PublishEligibilityChanges(eligibilityChanges);
        return ToDto(binding);
    }

    public async Task DeleteAsync(
        ProjectId projectId,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        await EnsureProjectExistsAsync(projectId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var binding = await db.WorkspaceBindings
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId, cancellationToken);
        if (binding is null)
        {
            return;
        }

        await ThrowIfBindingInUseAsync(binding.Id, cancellationToken);
        var eligibilityChanges = await GetEligibilityChangesAsync(projectId, cancellationToken);
        db.WorkspaceBindings.Remove(binding);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        PublishEligibilityChanges(eligibilityChanges);
    }

    private async Task ThrowIfBindingInUseAsync(
        WorkspaceBindingId bindingId,
        CancellationToken cancellationToken)
    {
        var inUse = await db.ExecutionAssignments.AnyAsync(
            assignment => assignment.WorkspaceBindingId == bindingId
                && assignment.State != ExecutionAssignmentState.Completed
                && assignment.State != ExecutionAssignmentState.Failed
                && assignment.State != ExecutionAssignmentState.Cancelled,
            cancellationToken);
        if (inUse)
        {
            throw new WorkspaceBindingInUseException(bindingId);
        }
    }

    private void RestoreUncommittedBinding(WorkspaceBinding binding)
    {
        var entry = db.Entry(binding);
        if (entry.State == EntityState.Added)
        {
            entry.State = EntityState.Detached;
            return;
        }

        entry.CurrentValues.SetValues(entry.OriginalValues);
        entry.State = EntityState.Unchanged;
    }

    private async Task<IReadOnlyList<ProjectionChange>> GetEligibilityChangesAsync(
        ProjectId projectId,
        CancellationToken cancellationToken)
    {
        var requestIds = await db.WorkRequests
            .AsNoTracking()
            .Where(request => request.ProjectId == projectId
                && request.Status == WorkRequestStatus.Queued)
            .Select(request => request.Id)
            .ToListAsync(cancellationToken);
        var changes = new List<ProjectionChange>(requestIds.Count + 1)
        {
            ProjectionChange.Project(projectId.Value),
        };
        foreach (var requestId in requestIds)
        {
            changes.Add(ProjectionChange.Request(projectId.Value, requestId.Value));
        }

        return changes;
    }

    private void PublishEligibilityChanges(IReadOnlyList<ProjectionChange> changes)
    {
        notifier.Publish(ProjectionChange.Fleet());
        foreach (var change in changes)
        {
            notifier.Publish(change);
        }
    }

    private async Task EnsureProjectExistsAsync(ProjectId projectId, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            throw new ProjectNotFoundException(projectId.Value);
        }
    }

    private static bool TryMapStatus(string status, out WorkspaceBindingStatus bindingStatus)
    {
        switch (status)
        {
            case WorkspaceValidationStatuses.Valid:
                bindingStatus = WorkspaceBindingStatus.Valid;
                return true;
            case WorkspaceValidationStatuses.Invalid:
                bindingStatus = WorkspaceBindingStatus.Invalid;
                return true;
            default:
                bindingStatus = default;
                return false;
        }
    }

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
}
