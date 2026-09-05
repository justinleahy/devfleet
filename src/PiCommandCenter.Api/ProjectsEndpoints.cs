using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Project endpoints: list, register, get, and validate, mapped relative to the supplied group.
/// Error mapping follows SPEC section 30: 400 validation, 404 missing, 409 duplicates.
/// </summary>
internal static class ProjectsEndpoints
{
    /// <param name="group">Route group the endpoints are mapped under (<c>/api</c> or <c>/api/v1</c>).</param>
    /// <param name="locationPrefix">Prefix for <c>Location</c> headers; equals the group prefix.</param>
    public static void MapProjectsEndpoints(this RouteGroupBuilder group, string locationPrefix)
    {
        group.MapGet("/projects", ListAsync).WithTags("Projects");
        group.MapPost("/projects", ([FromBody] RegisterProjectCommand command, IProjectCatalog catalog, CancellationToken cancellationToken) =>
            RegisterAsync(command, catalog, locationPrefix, cancellationToken)).WithTags("Projects");
        group.MapGet("/projects/{projectId:guid}", GetAsync).WithTags("Projects");
        group.MapPost("/projects/{projectId:guid}/validate", ValidateAsync).WithTags("Projects");
    }

    private static async Task<Ok<ProjectListResponse>> ListAsync(
        IProjectCatalog catalog,
        CancellationToken cancellationToken)
    {
        var projects = await catalog.ListAsync(cancellationToken);
        return TypedResults.Ok(new ProjectListResponse(projects));
    }

    private static async Task<Results<Created<ProjectDto>, Conflict<ProblemDetails>, BadRequest<ProblemDetails>>> RegisterAsync(
        RegisterProjectCommand command,
        IProjectCatalog catalog,
        string locationPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var project = await catalog.RegisterAsync(command, cancellationToken);
            return TypedResults.Created($"{locationPrefix}/projects/{project.Id}", project);
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
            return TypedResults.NotFound(ProjectNotFound(projectId));
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
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
    }
}

/// <summary>Response envelope for <c>GET {prefix}/projects</c>.</summary>
internal sealed record ProjectListResponse(IReadOnlyList<ProjectDto> Projects);
