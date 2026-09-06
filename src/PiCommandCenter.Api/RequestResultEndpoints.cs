using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain.Requests;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Request result and event timeline (<c>GET {prefix}/requests/{id}/result|events</c>).
/// </summary>
internal static class RequestResultEndpoints
{
    /// <param name="group">Route group the endpoints are mapped under (<c>/api</c> or <c>/api/v1</c>).</param>
    /// <param name="locationPrefix">Prefix for <c>Location</c> headers; no endpoint here emits one, kept for mapper uniformity.</param>
    public static void MapRequestResultEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        group.MapGet("/requests/{requestId:guid}/result", GetResultAsync).WithTags("Requests");
        group.MapGet("/requests/{requestId:guid}/events", ListEventsAsync).WithTags("Requests");
    }

    private static async Task<Results<Ok<RequestResultDto>, NotFound<ProblemDetails>>> GetResultAsync(
        Guid requestId,
        IAssignmentTerminalizationService gate,
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

    private static async Task<Results<Ok<SessionEventListResponse>, NotFound<ProblemDetails>>> ListEventsAsync(
        Guid requestId,
        IAgentSessionStore sessions,
        CancellationToken cancellationToken)
    {
        var events = await sessions.ListEventsAsync(new WorkRequestId(requestId), cancellationToken);
        return TypedResults.Ok(new SessionEventListResponse(events));
    }
}

/// <summary>Response envelope for <c>GET {prefix}/requests/{requestId}/events</c>.</summary>
internal sealed record SessionEventListResponse(IReadOnlyList<SessionEventDto> Events);
