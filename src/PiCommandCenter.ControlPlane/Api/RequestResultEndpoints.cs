using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.ControlPlane.Api;

/// <summary>
/// Browser-facing request result and event timeline (GET /api/requests/{id}/result|events).
/// </summary>
internal static class RequestResultEndpoints
{
    public static RouteGroupBuilder MapRequestResultEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/requests/{requestId:guid}").WithTags("Requests");
        group.MapGet("/result", GetResultAsync);
        group.MapGet("/events", ListEventsAsync);
        return group;
    }

    /// <summary>GET /api/requests/{requestId}/result</summary>
    private static async Task<Results<Ok<RequestResultDto>, NotFound<ProblemDetails>>> GetResultAsync(
        Guid requestId,
        ICompletionGateService gate,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await gate.GetResultAsync(new WorkRequestId(requestId), cancellationToken);
            if (result is null)
            {
                return TypedResults.NotFound(Problem(
                    StatusCodes.Status404NotFound,
                    "Request result not found",
                    $"No accepted result for request '{requestId}'."));
            }

            return TypedResults.Ok(result);
        }
        catch (RequestNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Request not found",
                $"No request with id '{requestId}' is registered."));
        }
    }

    /// <summary>GET /api/requests/{requestId}/events</summary>
    private static async Task<Results<Ok<SessionEventListResponse>, NotFound<ProblemDetails>>> ListEventsAsync(
        Guid requestId,
        IAgentSessionStore sessions,
        CancellationToken cancellationToken)
    {
        var events = await sessions.ListEventsAsync(new WorkRequestId(requestId), cancellationToken);
        return TypedResults.Ok(new SessionEventListResponse(events));
    }

    private static ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };
}

/// <summary>Response envelope for <c>GET /api/requests/{requestId}/events</c>.</summary>
internal sealed record SessionEventListResponse(IReadOnlyList<SessionEventDto> Events);
