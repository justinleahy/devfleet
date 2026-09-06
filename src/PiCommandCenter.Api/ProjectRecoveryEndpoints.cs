using System.Security.Claims;

using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Recovery;
using PiCommandCenter.Domain;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Project recovery diagnosis, durable start, progress, recheck, manual confirmation, and hold resume.
/// </summary>
internal static class ProjectRecoveryEndpoints
{
    /// <param name="group">Route group the endpoints are mapped under (<c>/api</c> or <c>/api/v1</c>).</param>
    /// <param name="locationPrefix">Prefix for <c>Location</c> headers; equals the group prefix.</param>
    public static void MapProjectRecoveryEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        group.MapGet("/projects/{projectId:guid}/recovery", DiagnoseAsync).WithTags("Recovery");
        group.MapPost("/projects/{projectId:guid}/recoveries", (
            Guid projectId,
            [FromBody] StartProjectRecoveryRequest body,
            IProjectRecoveryService recoveries,
            IRecoveryAttemptDispatcher dispatcher,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
            StartAsync(projectId, body, recoveries, dispatcher, httpContext, locationPrefix, cancellationToken)).WithTags("Recovery");
        group.MapGet("/projects/{projectId:guid}/recoveries/{recoveryId:guid}", GetAsync).WithTags("Recovery");
        group.MapPost("/projects/{projectId:guid}/recoveries/{recoveryId:guid}/recheck", RecheckAsync).WithTags("Recovery");
        group.MapPost("/projects/{projectId:guid}/recoveries/{recoveryId:guid}/confirm-manual", ConfirmManualAsync).WithTags("Recovery");
        group.MapPost("/projects/{projectId:guid}/recovery/resume", ResumeAsync).WithTags("Recovery");

    }

    private static async Task<Results<Ok<ProjectRecoveryDiagnosisDto>, NotFound<ProblemDetails>>> DiagnoseAsync(
        Guid projectId,
        IProjectRecoveryService recoveries,
        CancellationToken cancellationToken)
    {
        try
        {
            var diagnosis = await recoveries.GetDiagnosisAsync(new ProjectId(projectId), cancellationToken);
            return TypedResults.Ok(ProjectRecoveryDiagnosisDto.From(diagnosis));
        }
        catch (Exception ex) when (IsProjectMissing(ex, projectId))
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
    }

    private static async Task<Results<
        Accepted<ProjectRecoveryStartResponse>,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>,
        BadRequest<ProblemDetails>>> StartAsync(

        Guid projectId,
        StartProjectRecoveryRequest body,
        IProjectRecoveryService recoveries,
        IRecoveryAttemptDispatcher dispatcher,
        HttpContext httpContext,
        string locationPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var started = await recoveries.StartAsync(
                new ProjectId(projectId),
                new StartProjectRecoveryCommand(
                    body.InventoryRevision,
                    "Administrator requested project recovery.",
                    AuthenticatedActor(httpContext),
                    body.IdempotencyKey),
                cancellationToken);
            if (started.Operation is { } startedOperation)
            {
                await dispatcher.DispatchAsync(
                    new ProjectId(projectId),
                    startedOperation.Id,
                    cancellationToken);
            }

            var dto = ProjectRecoveryStartResponse.From(started);
            var location = started.Operation is { } operation
                ? $"{locationPrefix}/projects/{projectId}/recoveries/{operation.Id}"
                : $"{locationPrefix}/projects/{projectId}/recovery";
            return TypedResults.Accepted(location, dto);

        }
        catch (Exception ex) when (IsProjectMissing(ex, projectId))
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
        catch (Exception ex) when (TryConflict(ex) is { } conflict)
        {
            return conflict;
        }
    }

    private static async Task<Results<Ok<ProjectRecoveryOperationDto>, NotFound<ProblemDetails>>> GetAsync(
        Guid projectId,
        Guid recoveryId,
        IProjectRecoveryService recoveries,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await recoveries.GetOperationAsync(
                new ProjectId(projectId),
                recoveryId,
                cancellationToken);
            return TypedResults.Ok(ProjectRecoveryOperationDto.From(operation));
        }
        catch (Exception ex) when (IsProjectMissing(ex, projectId))
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (RecoveryOperationNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Recovery not found",
                $"No recovery with id '{recoveryId}' exists for project '{projectId}'."));
        }
    }

    private static async Task<Results<
        Ok<ProjectRecoveryOperationDto>,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>,
        BadRequest<ProblemDetails>>> RecheckAsync(

        Guid projectId,
        Guid recoveryId,
        [FromBody] RecheckProjectRecoveryRequest body,
        IProjectRecoveryService recoveries,
        IRecoveryAttemptDispatcher dispatcher,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await recoveries.RecheckAsync(
                new ProjectId(projectId),
                recoveryId,
                body.ExpectedOperationVersion,
                body.IdempotencyKey,
                cancellationToken);
            await dispatcher.DispatchAsync(new ProjectId(projectId), operation.Id, cancellationToken);
            return TypedResults.Ok(ProjectRecoveryOperationDto.From(operation));

        }
        catch (Exception ex) when (IsProjectMissing(ex, projectId))
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (RecoveryOperationNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Recovery not found",
                $"No recovery with id '{recoveryId}' exists for project '{projectId}'."));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ex.Message));
        }
        catch (Exception ex) when (TryConflict(ex) is { } conflict)
        {
            return conflict;
        }
    }

    private static async Task<Results<
        Ok<ProjectRecoveryOperationDto>,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>,
        BadRequest<ProblemDetails>>> ConfirmManualAsync(
        Guid projectId,
        Guid recoveryId,
        [FromBody] ConfirmManualProjectRecoveryRequest body,
        IManualProjectRecoveryService recoveries,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var operation = await recoveries.ConfirmManualAsync(
                new ProjectId(projectId),
                new ConfirmManualProjectRecoveryCommand(
                    recoveryId,
                    body.ExpectedOperationVersion,
                    body.ExpectedAttempt,
                    body.ExactProjectName,
                    "Administrator confirmed manual recovery after evidence review.",
                    AuthenticatedActor(httpContext),
                    body.IdempotencyKey,
                    body.ConfirmOriginalExecutionCannotResume,
                    body.WriterAccessPrevented,
                    body.AcknowledgeEvidenceGaps,
                    body.ProcessStopEvidence,
                    body.RepositoryStatusSnapshot,
                    body.RepositoryStatusSource,
                    body.RepositoryCollectedAt,
                    body.ReservationAndEventGapAccounting),
                cancellationToken);
            return TypedResults.Ok(ProjectRecoveryOperationDto.From(operation));
        }
        catch (Exception ex) when (IsProjectMissing(ex, projectId))
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (RecoveryOperationNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Recovery not found",
                $"No recovery with id '{recoveryId}' exists for project '{projectId}'."));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ex.Message));
        }
        catch (Exception ex) when (TryConflict(ex) is { } conflict)
        {
            return conflict;
        }
    }

    private static async Task<Results<
        NoContent,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>,
        BadRequest<ProblemDetails>>> ResumeAsync(
        Guid projectId,
        [FromBody] ResumeProjectRecoveryRequest body,
        IProjectRecoveryService recoveries,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        try
        {
            await recoveries.ResumeAsync(
                new ProjectId(projectId),
                body.OperationId,
                body.ExpectedHoldVersion,
                AuthenticatedActor(httpContext),
                cancellationToken);
            return TypedResults.NoContent();
        }
        catch (Exception ex) when (IsProjectMissing(ex, projectId))
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (RecoveryOperationNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Recovery not found",
                $"No recovery with id '{body.OperationId}' exists for project '{projectId}'."));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Invalid request",
                ex.Message));
        }
        catch (Exception ex) when (TryConflict(ex) is { } conflict)
        {
            return conflict;
        }
    }
    private static string AuthenticatedActor(HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? context.User.Identity?.Name
        ?? throw new InvalidOperationException("Authenticated administrator identity is missing.");


    private static bool IsProjectMissing(Exception exception, Guid projectId) =>
        exception is ProjectNotFoundException
        || (exception is InvalidOperationException
            && exception.Message.Contains(projectId.ToString(), StringComparison.Ordinal));

    private static Conflict<ProblemDetails>? TryConflict(Exception exception) => exception switch
    {
        RecoveryInventoryConflictException ex => TypedResults.Conflict(Problem(
            StatusCodes.Status409Conflict,
            "Recovery inventory conflict",
            ex.Message)),
        RecoveryRevisionConflictException ex => TypedResults.Conflict(Problem(
            StatusCodes.Status409Conflict,
            "Recovery revision conflict",
            ex.Message)),
        RecoveryOperationConflictException ex => TypedResults.Conflict(Problem(
            StatusCodes.Status409Conflict,
            "Recovery operation conflict",
            ex.Message)),
        RecoveryIdempotencyConflictException ex => TypedResults.Conflict(Problem(
            StatusCodes.Status409Conflict,
            "Recovery idempotency conflict",
            ex.Message)),
        RecoveryNotReadyException ex => TypedResults.Conflict(Problem(
            StatusCodes.Status409Conflict,
            "Recovery not ready",
            ex.Message)),
        _ => null,
    };
}

/// <summary>Body for <c>POST {prefix}/projects/{projectId}/recoveries</c>.</summary>
internal sealed record StartProjectRecoveryRequest(
    string InventoryRevision,
    string IdempotencyKey);

/// <summary>Body for <c>POST {prefix}/projects/{projectId}/recoveries/{recoveryId}/recheck</c>.</summary>
internal sealed record RecheckProjectRecoveryRequest(
    long ExpectedOperationVersion,
    string IdempotencyKey);

/// <summary>Body for <c>POST {prefix}/projects/{projectId}/recoveries/{recoveryId}/confirm-manual</c>.</summary>
internal sealed record ConfirmManualProjectRecoveryRequest(
    long ExpectedOperationVersion,
    int ExpectedAttempt,
    string ExactProjectName,
    string IdempotencyKey,
    bool ConfirmOriginalExecutionCannotResume,
    bool WriterAccessPrevented,
    bool AcknowledgeEvidenceGaps,
    string ProcessStopEvidence,
    string RepositoryStatusSnapshot,
    string RepositoryStatusSource,
    DateTimeOffset RepositoryCollectedAt,
    string ReservationAndEventGapAccounting);

/// <summary>Body for <c>POST {prefix}/projects/{projectId}/recovery/resume</c>.</summary>
internal sealed record ResumeProjectRecoveryRequest(
    Guid OperationId,
    long ExpectedHoldVersion);

internal sealed record ProjectRecoveryStartResponse(bool NoOp, ProjectRecoveryOperationDto? Operation)
{
    public static ProjectRecoveryStartResponse From(ProjectRecoveryStartResult result) =>
        new(result.NoOp, result.Operation is null ? null : ProjectRecoveryOperationDto.From(result.Operation));
}

internal sealed record ProjectRecoveryDiagnosisDto(
    Guid ProjectId,
    long ProjectVersion,
    string InventoryRevision,
    bool HoldPresent,
    Guid? HoldOperationId,
    long? HoldVersion,
    ProjectRecoveryOperationDto? LatestOperation,
    IReadOnlyList<ProjectRecoveryAssignmentSnapshotDto> NonterminalAssignments,
    IReadOnlyList<ProjectRecoveryReservationSnapshotDto> UnresolvedReservations)
{
    public static ProjectRecoveryDiagnosisDto From(ProjectRecoveryDiagnosis diagnosis) => new(
        diagnosis.ProjectId.Value,
        diagnosis.ProjectVersion,
        diagnosis.InventoryRevision,
        diagnosis.HoldPresent,
        diagnosis.HoldOperationId,
        diagnosis.HoldVersion,
        diagnosis.LatestOperation is null ? null : ProjectRecoveryOperationDto.From(diagnosis.LatestOperation),
        diagnosis.NonterminalAssignments.Select(ProjectRecoveryAssignmentSnapshotDto.From).ToList(),
        diagnosis.UnresolvedReservations.Select(ProjectRecoveryReservationSnapshotDto.From).ToList());
}

internal sealed record ProjectRecoveryOperationDto(
    Guid Id,
    Guid ProjectId,
    string Status,
    int Attempt,
    long Version,
    string InventoryRevision,
    string Reason,
    string Actor,
    string? Stage,
    string? BlockerCodesJson,
    string? EvidenceJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    DateTimeOffset? Deadline,
    IReadOnlyList<ProjectRecoveryAssignmentTargetDto> AssignmentTargets,
    IReadOnlyList<ProjectRecoveryReservationTargetDto> ReservationTargets)
{
    public static ProjectRecoveryOperationDto From(ProjectRecoveryOperation operation) => new(
        operation.Id,
        operation.ProjectId.Value,
        operation.Status.ToString(),
        operation.Attempt,
        operation.Version,
        operation.InventoryRevision,
        operation.Reason,
        operation.Actor,
        operation.Stage,
        operation.BlockerCodesJson,
        operation.EvidenceJson,
        operation.CreatedAt,
        operation.UpdatedAt,
        operation.CompletedAt,
        operation.Deadline,
        operation.AssignmentTargets.Select(ProjectRecoveryAssignmentTargetDto.From).ToList(),
        operation.ReservationTargets.Select(ProjectRecoveryReservationTargetDto.From).ToList());
}

internal sealed record ProjectRecoveryAssignmentTargetDto(
    Guid RequestId,
    long CapturedVersion,
    string CapturedState,
    long BindingRevision,
    string? Outcome,
    string? EvidenceJson)
{
    public static ProjectRecoveryAssignmentTargetDto From(ProjectRecoveryAssignmentTarget target) => new(
        target.RequestId.Value,
        target.CapturedVersion,
        target.CapturedState,
        target.BindingRevision,
        target.Outcome,
        target.EvidenceJson);
}

internal sealed record ProjectRecoveryReservationTargetDto(
    Guid LeaseId,
    long CapturedVersion,
    string CapturedState,
    string? Outcome,
    string? EvidenceJson)
{
    public static ProjectRecoveryReservationTargetDto From(ProjectRecoveryReservationTarget target) => new(
        target.LeaseId,
        target.CapturedVersion,
        target.CapturedState,
        target.Outcome,
        target.EvidenceJson);
}

internal sealed record ProjectRecoveryAssignmentSnapshotDto(
    Guid RequestId,
    long Version,
    string State,
    long BindingRevision,
    Guid? AssignedNodeId,
    string? AssignedNodeDisplayName,
    string? CanonicalRepositoryPath,
    DateTimeOffset? AssignedAt,
    DateTimeOffset? LastRenewedAt,
    DateTimeOffset? LastReconciledAt,
    DateTimeOffset? LeaseExpiresAt,
    DateTimeOffset? NodeLastContact,
    string? NodeStatus)
{
    public static ProjectRecoveryAssignmentSnapshotDto From(ProjectRecoveryAssignmentSnapshot snapshot) => new(
        snapshot.RequestId.Value,
        snapshot.Version,
        snapshot.State,
        snapshot.BindingRevision,
        snapshot.AssignedNodeId,
        snapshot.AssignedNodeDisplayName,
        snapshot.CanonicalRepositoryPath,
        snapshot.AssignedAt,
        snapshot.LastRenewedAt,
        snapshot.LastReconciledAt,
        snapshot.LeaseExpiresAt,
        snapshot.NodeLastContact,
        snapshot.NodeStatus);
}

internal sealed record ProjectRecoveryReservationSnapshotDto(
    Guid LeaseId,
    long Version,
    string State,
    Guid? RequestId,
    string? OwnerSessionId,
    string? Reason,
    DateTimeOffset? ExpiresAt)
{
    public static ProjectRecoveryReservationSnapshotDto From(ProjectRecoveryReservationSnapshot snapshot) => new(
        snapshot.LeaseId,
        snapshot.Version,
        snapshot.State,
        snapshot.RequestId,
        snapshot.OwnerSessionId,
        snapshot.Reason,
        snapshot.ExpiresAt);
}
