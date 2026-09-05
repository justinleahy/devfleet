using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Domain.Reservations;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Reservation endpoints (SPEC §17.9): the human-only forced release of a reservation lease.
/// Forced release is destructive — it requires an explicit confirmation flag, a mandatory
/// reason, and a repository status snapshot for the audit trail — and issues a fresh fencing
/// token so any in-flight node work fails authorization.
/// </summary>
internal static class ReservationsEndpoints
{
    /// <param name="group">Route group the endpoints are mapped under (<c>/api</c> or <c>/api/v1</c>).</param>
    /// <param name="locationPrefix">Prefix for <c>Location</c> headers; no endpoint here emits one, kept for mapper uniformity.</param>
    public static void MapReservationsEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        group.MapPost("/reservations/{leaseId:guid}/force-release", ForceReleaseAsync).WithTags("Reservations");
    }

    /// <summary>POST {prefix}/reservations/{leaseId}/force-release</summary>
    private static async Task<Results<Ok<ReservationLeaseDto>, NotFound<ProblemDetails>, Conflict<ProblemDetails>, BadRequest<ProblemDetails>>> ForceReleaseAsync(
        Guid leaseId,
        [FromBody] ForceReleaseRequest body,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        if (body.Confirm != true)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Confirmation required",
                "Force release is destructive; the request must carry confirm=true."));
        }

        if (string.IsNullOrWhiteSpace(body.Reason) || string.IsNullOrWhiteSpace(body.RepositoryStatusSnapshot))
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                "Reason and RepositoryStatusSnapshot are required for the audit trail."));
        }

        try
        {
            var lease = await reservations.ForceReleaseAsync(
                new ForceReleaseReservationCommand(
                    leaseId,
                    body.Reason,
                    body.RepositoryStatusSnapshot,
                    string.IsNullOrWhiteSpace(body.RequestedBy) ? "human" : body.RequestedBy),
                cancellationToken);
            return TypedResults.Ok(lease);
        }
        catch (ReservationNotFoundException ex)
        {
            return TypedResults.NotFound(Problem(StatusCodes.Status404NotFound, "Reservation not found", ex.Message));
        }
        catch (InvalidLeaseStateException ex)
        {
            return TypedResults.Conflict(Problem(StatusCodes.Status409Conflict, "Invalid lease state", ex.Message));
        }
        catch (ReservationStateException ex)
        {
            return TypedResults.Conflict(Problem(StatusCodes.Status409Conflict, "Invalid lease state", ex.Message));
        }
        catch (ReservationValidationException ex)
        {
            return TypedResults.BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request", ex.Message));
        }
    }
}

/// <summary>Request body for <c>POST {prefix}/reservations/{leaseId}/force-release</c>.</summary>
internal sealed record ForceReleaseRequest(
    Guid ProjectId,
    string Reason,
    string RepositoryStatusSnapshot,
    string? RequestedBy,
    bool? Confirm);
