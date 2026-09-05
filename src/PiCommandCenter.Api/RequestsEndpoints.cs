using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Request endpoints: enqueue and list a project's persisted work requests,
/// ordered by priority descending then creation time ascending by the application layer.
/// </summary>
internal static class RequestsEndpoints
{
    /// <param name="group">Route group the endpoints are mapped under (<c>/api</c> or <c>/api/v1</c>).</param>
    /// <param name="locationPrefix">Prefix for <c>Location</c> headers; equals the group prefix.</param>
    public static void MapRequestsEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        group.MapGet("/projects/{projectId:guid}/requests", ListAsync).WithTags("Requests");
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
            return TypedResults.Created($"{locationPrefix}/projects/{projectId}/requests/{request.Id}", request);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ex.Message));
        }
    }
}

/// <summary>Response envelope for <c>GET {prefix}/projects/{projectId}/requests</c>.</summary>
internal sealed record WorkRequestListResponse(IReadOnlyList<WorkRequestDto> Requests);
