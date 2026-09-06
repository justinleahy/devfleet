using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Requests;

/// <summary>
/// Coordinates work-request and execution-assignment cancellation in one database transaction.
/// Node notification happens only after this service returns successfully.
/// </summary>
public sealed class RequestCancellationService(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier) : IRequestCancellationService
{
    private const string DefaultReason = "cancelled-by-operator";

    public async Task<RequestCancellationResult> CancelAsync(
        WorkRequestId requestId,
        CancelWorkRequestCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var transaction = await db.Database
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        var request = await db.WorkRequests
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new RequestNotFoundException(requestId);
        var assignment = await db.ExecutionAssignments
            .SingleOrDefaultAsync(candidate => candidate.RequestId == requestId, cancellationToken)
            .ConfigureAwait(false);
        var reason = string.IsNullOrWhiteSpace(command.Reason)
            ? DefaultReason
            : command.Reason.Trim();

        if (assignment is null)
        {
            if (request.Status == WorkRequestStatus.Queued)
            {
                request.CancelQueued(clock.GetUtcNow().ToUniversalTime());
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (request.Status != WorkRequestStatus.Cancelled)
            {
                ThrowRejectedOrInconsistent(request);
            }
        }
        else if (request.Status == WorkRequestStatus.Cancelled
            && assignment.State == ExecutionAssignmentState.Cancelled)
        {
            // Exact retry after quiescence terminalized both records.
        }
        else
        {
            EnsureNonterminalPair(request, assignment);
            var now = clock.GetUtcNow().ToUniversalTime();
            if (request.Status != WorkRequestStatus.Cancelling)
            {
                request.BeginCancelling(now);
            }

            if (assignment.State != ExecutionAssignmentState.Cancelling)
            {
                assignment.BeginCancelling(now);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        notifier.Publish(ProjectionChange.Project(request.ProjectId.Value));
        notifier.Publish(ProjectionChange.Request(request.ProjectId.Value, request.Id.Value));

        return new RequestCancellationResult(
            request.Id,
            request.ProjectId,
            request.Status,
            assignment?.State,
            assignment?.NodeIdSnapshot,
            reason);
    }

    private static void EnsureNonterminalPair(
        WorkRequest request,
        ExecutionAssignment assignment)
    {
        if (request.Status is WorkRequestStatus.Completed
            or WorkRequestStatus.Failed
            or WorkRequestStatus.Cancelled)
        {
            ThrowRejectedOrInconsistent(request);
        }

        if (request.Status == WorkRequestStatus.Queued)
        {
            throw new InvalidOperationException(
                $"Assigned work request '{request.Id.Value}' cannot remain queued.");
        }

        if (assignment.State is ExecutionAssignmentState.Completed
            or ExecutionAssignmentState.Failed
            or ExecutionAssignmentState.Cancelled)
        {
            throw new InvalidOperationException(
                $"Work request '{request.Id.Value}' is nonterminal but its execution assignment is '{assignment.State}'.");
        }
    }

    private static void ThrowRejectedOrInconsistent(WorkRequest request)
    {
        if (request.Status is WorkRequestStatus.Completed or WorkRequestStatus.Failed)
        {
            throw new RequestCancellationRejectedException(request.Id, request.Status);
        }

        throw new InvalidOperationException(
            $"Work request '{request.Id.Value}' in status '{request.Status}' has no matching cancellable assignment.");
    }
}
