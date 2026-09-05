using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.ControlPlane.Api;

/// <summary>
/// Browser-facing reservation endpoints (SPEC §17.9): the human-only forced release of a
/// reservation lease. Forced release is destructive — it requires an explicit confirmation
/// flag, a mandatory reason, and a repository status snapshot for the audit trail — and
/// issues a fresh fencing token so any in-flight node work fails authorization.
/// </summary>
internal static class ReservationsEndpoints
{
    public static RouteGroupBuilder MapReservationsEndpoints(this IEndpointRouteBuilder routes)
    {
        var reservations = routes.MapGroup("/api/reservations").WithTags("Reservations");
        reservations.MapPost("/{leaseId:guid}/force-release", ForceReleaseAsync);
        return reservations;
    }

    /// <summary>POST /api/reservations/{leaseId}/force-release</summary>
    private static async Task<Results<Ok<ReservationLeaseDto>, NotFound<ProblemDetails>, Conflict<ProblemDetails>, BadRequest<ProblemDetails>>> ForceReleaseAsync(
        Guid leaseId,
        [FromBody] ForceReleaseRequest body,
        IReservationService reservations,
        CancellationToken cancellationToken)
    {
        if (body.Confirm != true)
        {
            return TypedResults.BadRequest(Problem(
                "Confirmation required",
                "Force release is destructive; the request must carry confirm=true."));
        }

        if (string.IsNullOrWhiteSpace(body.Reason) || string.IsNullOrWhiteSpace(body.RepositoryStatusSnapshot))
        {
            return TypedResults.BadRequest(Problem(
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
            return TypedResults.NotFound(Problem("Reservation not found", ex.Message, StatusCodes.Status404NotFound));
        }
        catch (InvalidLeaseStateException ex)
        {
            return TypedResults.Conflict(Problem("Invalid lease state", ex.Message, StatusCodes.Status409Conflict));
        }
        catch (ReservationStateException ex)
        {
            return TypedResults.Conflict(Problem("Invalid lease state", ex.Message, StatusCodes.Status409Conflict));
        }
        catch (ReservationValidationException ex)
        {
            return TypedResults.BadRequest(Problem("Invalid request", ex.Message));
        }
    }

    private static ProblemDetails Problem(string title, string detail, int status = StatusCodes.Status400BadRequest) => new()
    {
        Title = title,
        Detail = detail,
        Status = status,
    };
}

/// <summary>Request body for <c>POST /api/reservations/{leaseId}/force-release</c>.</summary>
internal sealed record ForceReleaseRequest(
    Guid ProjectId,
    string Reason,
    string RepositoryStatusSnapshot,
    string? RequestedBy,
    bool? Confirm);
