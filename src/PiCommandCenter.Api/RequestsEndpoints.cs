using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Requests;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Request endpoints for enqueuing, listing, and getting persisted work requests.
/// </summary>
internal static class RequestsEndpoints
{
    /// <param name="group">Route group the endpoints are mapped under (<c>/api</c> or <c>/api/v1</c>).</param>
    /// <param name="locationPrefix">Prefix for <c>Location</c> headers; equals the group prefix.</param>
    public static void MapRequestsEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        group.MapGet("/projects/{projectId:guid}/requests", ListAsync).WithTags("Requests");
        group.MapGet("/requests/{requestId:guid}", GetAsync).WithTags("Requests");
        group.MapPost("/requests/{requestId:guid}/cancel", CancelAsync).WithTags("Requests");
        group.MapPost("/projects/{projectId:guid}/requests", (Guid projectId, [FromBody] QueueWorkRequestCommand command, IRequestQueue queue, CancellationToken cancellationToken) =>
            EnqueueAsync(projectId, command, queue, locationPrefix, cancellationToken)).WithTags("Requests");
    }

    private static async Task<Results<Ok<WorkRequestListResponse>, NotFound<ProblemDetails>>> ListAsync(
        Guid projectId,
        IRequestQueue queue,
        CancellationToken cancellationToken)
    {
        try
        {
            var requests = await queue.ListAsync(new ProjectId(projectId), cancellationToken);
            return TypedResults.Ok(new WorkRequestListResponse(requests));
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
    }

    private static async Task<Results<Ok<WorkRequestDto>, NotFound<ProblemDetails>>> GetAsync(
        Guid requestId,
        IRequestQueue queue,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = await queue.GetAsync(new WorkRequestId(requestId), cancellationToken);
            return TypedResults.Ok(request);
        }
        catch (RequestNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Request not found",
                $"No request with id '{requestId}' is registered."));
        }
    }

    private static async Task<Results<Created<WorkRequestDto>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>> EnqueueAsync(
        Guid projectId,
        QueueWorkRequestCommand command,
        IRequestQueue queue,
        string locationPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = await queue.EnqueueAsync(new ProjectId(projectId), command, cancellationToken);
            return TypedResults.Created($"{locationPrefix}/requests/{request.Id}", request);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (RequestNotFoundException ex)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Request not found",
                $"No request with id '{ex.Id.Value}' is registered."));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ex.Message));
        }
    }

    /// <summary>
    /// Durably closes request admission before best-effort notification of the retained owner.
    /// Assigned work remains Cancelling until the node's quiescence terminalizer confirms it.
    /// </summary>
    private static async Task<Results<
        Ok<RequestCancellationResponse>,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>>> CancelAsync(
        Guid requestId,
        [FromBody] CancelWorkRequestCommand? command,
        IRequestCancellationService cancellations,
        INativeApiRealtimeGateway realtime,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await cancellations.CancelAsync(
                new WorkRequestId(requestId),
                command ?? new CancelWorkRequestCommand(Reason: null),
                cancellationToken);
            if (result is
                {
                    AssignmentState: ExecutionAssignmentState.Cancelling,
                    AssignedNodeId: { } nodeId,
                })
            {
                await realtime.CancelAssignmentAsync(
                    nodeId.Value,
                    requestId,
                    result.Reason,
                    cancellationToken);
            }

            return TypedResults.Ok(new RequestCancellationResponse(
                requestId,
                result.RequestStatus.ToString(),
                result.AssignmentState?.ToString()));
        }
        catch (RequestNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Request not found",
                $"No request with id '{requestId}' is registered."));
        }
        catch (RequestCancellationRejectedException exception)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Request cannot be cancelled",
                exception.Message));
        }
    }
}

/// <summary>Durable state returned by <c>POST {prefix}/requests/{requestId}/cancel</c>.</summary>
public sealed record RequestCancellationResponse(
    Guid RequestId,
    string RequestStatus,
    string? AssignmentState);

/// <summary>Response envelope for <c>GET {prefix}/projects/{projectId}/requests</c>.</summary>
internal sealed record WorkRequestListResponse(IReadOnlyList<WorkRequestDto> Requests);
