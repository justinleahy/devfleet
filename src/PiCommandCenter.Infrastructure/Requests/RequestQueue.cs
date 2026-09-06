using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// EF Core backed request queue. Listing and dequeue readiness are defined by the
/// database: priority descending, then creation time ascending, served by the
/// IX_WorkRequests_Priority_CreatedAt index.
/// </summary>
public sealed class RequestQueue(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IRequestEligibilityEvaluator eligibilityEvaluator,
    IProjectionNotifier notifier) : IRequestQueue
{
    public async Task<IReadOnlyList<WorkRequestDto>> ListAsync(
        ProjectId projectId,
        CancellationToken cancellationToken = default)
    {
        await EnsureProjectExistsAsync(projectId, cancellationToken);

        var requests = await db.WorkRequests
            .AsNoTracking()
            .Where(r => r.ProjectId == projectId)
            .OrderByDescending(r => r.Priority)
            .ThenBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);

        var decisions = await eligibilityEvaluator.EvaluateBatchAsync(
            requests.Select(request => request.Id).ToArray(),
            cancellationToken);

        return requests
            .Select(request => ToDto(request, decisions[request.Id]))
            .ToList();
    }

    public async Task<WorkRequestDto> GetAsync(
        WorkRequestId requestId,
        CancellationToken cancellationToken = default)
    {
        var request = await db.WorkRequests
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new RequestNotFoundException(requestId);

        var decision = await eligibilityEvaluator.EvaluateAsync(
            requestId,
            cancellationToken: cancellationToken);
        return ToDto(request, decision);
    }

    public async Task<WorkRequestDto> EnqueueAsync(
        ProjectId projectId,
        QueueWorkRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        await EnsureProjectExistsAsync(projectId, cancellationToken);

        var request = WorkRequest.Enqueue(
            projectId,
            command.Kind,
            command.Priority,
            command.RiskLevel,
            command.Title,
            command.Prompt,
            clock.GetUtcNow());

        db.WorkRequests.Add(request);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException(
                $"Work request for project '{projectId}' could not be enqueued.", ex);
        }

        notifier.Publish(ProjectionChange.Project(projectId.Value));

        var decision = await eligibilityEvaluator.EvaluateAsync(
            request.Id,
            cancellationToken: cancellationToken);
        return ToDto(request, decision);
    }

    private async Task EnsureProjectExistsAsync(ProjectId projectId, CancellationToken cancellationToken)
    {
        if (!await db.Projects.AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw new ProjectNotFoundException(projectId.Value);
        }
    }

    private static WorkRequestDto ToDto(WorkRequest request, EligibilityDecision decision) => new(
        request.Id.Value,
        request.ProjectId.Value,
        (int)request.Kind,
        request.Kind.ToString(),
        (int)request.Priority,
        request.Priority.ToString(),
        (int)request.RiskLevel,
        request.RiskLevel.ToString(),
        (int)request.Status,
        request.Status.ToString(),
        request.BlockedPhase.HasValue ? (int)request.BlockedPhase.Value : null,
        request.BlockedPhase?.ToString(),
        request.Title,
        request.Prompt,
        request.CreatedAt,
        request.UpdatedAt,
        request.Version,
        request.Status == WorkRequestStatus.Queued && decision.Assignment is null
            ? decision.Status
            : null,
        decision.Assignment);
}
