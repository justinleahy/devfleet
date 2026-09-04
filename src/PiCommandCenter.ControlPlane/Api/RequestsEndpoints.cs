using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;

namespace PiCommandCenter.ControlPlane.Api;

/// <summary>
/// Milestone 1 request endpoints: enqueue and list a project's persisted work requests,
/// ordered by priority descending then creation time ascending by the application layer.
/// </summary>
internal static class RequestsEndpoints
{
    public static RouteGroupBuilder MapRequestsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/projects/{projectId:guid}/requests")
            .WithTags("Requests");

        group.MapGet("/", ListAsync);
        group.MapPost("/", EnqueueAsync);

        return group;
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
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Project not found",
                $"No project with id '{projectId}' is registered."));
        }
    }

    private static async Task<Results<Created<WorkRequestDto>, NotFound<ProblemDetails>, BadRequest<ProblemDetails>>> EnqueueAsync(
        Guid projectId,
        [FromBody] QueueWorkRequestCommand command,
        IRequestQueue queue,
        CancellationToken cancellationToken)
    {
        try
        {
            var request = await queue.EnqueueAsync(new ProjectId(projectId), command, cancellationToken);
            return TypedResults.Created($"/api/projects/{projectId}/requests/{request.Id}", request);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Project not found",
                $"No project with id '{projectId}' is registered."));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ex.Message));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };
}

/// <summary>Response envelope for <c>GET /api/projects/{projectId}/requests</c>.</summary>
internal sealed record WorkRequestListResponse(IReadOnlyList<WorkRequestDto> Requests);
