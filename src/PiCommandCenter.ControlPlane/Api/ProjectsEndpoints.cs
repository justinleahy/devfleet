using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;

namespace PiCommandCenter.ControlPlane.Api;

/// <summary>
/// Milestone 1 project endpoints: list, register, get, and validate.
/// Error mapping follows SPEC section 30: 400 validation, 404 missing, 409 duplicates.
/// </summary>
internal static class ProjectsEndpoints
{
    public static RouteGroupBuilder MapProjectsEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/projects")
            .WithTags("Projects");

        group.MapGet("/", ListAsync);
        group.MapPost("/", RegisterAsync);
        group.MapGet("/{projectId:guid}", GetAsync);
        group.MapPost("/{projectId:guid}/validate", ValidateAsync);

        return group;
    }

    private static async Task<Ok<ProjectListResponse>> ListAsync(
        IProjectCatalog catalog,
        CancellationToken cancellationToken)
    {
        var projects = await catalog.ListAsync(cancellationToken);
        return TypedResults.Ok(new ProjectListResponse(projects));
    }

    private static async Task<Results<Created<ProjectDto>, Conflict<ProblemDetails>, BadRequest<ProblemDetails>>> RegisterAsync(
        [FromBody] RegisterProjectCommand command,
        IProjectCatalog catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = await catalog.RegisterAsync(command, cancellationToken);
            return TypedResults.Created($"/api/projects/{project.Id}", project);
        }
        catch (ProjectValidationException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Validation failed",
                string.Join(' ', ex.Errors)));
        }
        catch (DuplicateProjectException ex)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Duplicate project",
                $"A project is already registered at '{ex.RepositoryPath}'."));
        }
    }

    private static async Task<Results<Ok<ProjectDto>, NotFound<ProblemDetails>>> GetAsync(
        Guid projectId,
        IProjectCatalog catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = await catalog.GetAsync(new ProjectId(projectId), cancellationToken);
            return TypedResults.Ok(project);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Project not found",
                $"No project with id '{projectId}' is registered."));
        }
    }

    private static async Task<Results<Ok<ProjectValidationReport>, NotFound<ProblemDetails>>> ValidateAsync(
        Guid projectId,
        IProjectCatalog catalog,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = await catalog.GetAsync(new ProjectId(projectId), cancellationToken);
            var command = new RegisterProjectCommand(
                project.DisplayName,
                project.RepositoryPath,
                project.DefaultBranch,
                project.Enabled,
                project.MaxActiveWriteRequests,
                project.MaxReadOnlyRequests,
                project.MaxChildAgentsPerRequest,
                project.RequireCleanStart,
                project.CreateRequestBranch,
                project.CreateRequestCommit,
                project.AutoMerge);
            var report = await catalog.ValidateAsync(command, cancellationToken);
            return TypedResults.Ok(report);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Project not found",
                $"No project with id '{projectId}' is registered."));
        }
    }

    private static ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };
}

/// <summary>Response envelope for <c>GET /api/projects</c>.</summary>
internal sealed record ProjectListResponse(IReadOnlyList<ProjectDto> Projects);
