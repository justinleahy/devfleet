using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Domain;
using static PiCommandCenter.Api.ApiProblems;

namespace PiCommandCenter.Api;

/// <summary>
/// Project metadata and workspace-binding endpoints, mapped relative to the supplied group.
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
        group.MapPut("/projects/{projectId:guid}/workspace-binding", DesignateWorkspaceBindingAsync).WithTags("Projects");
        group.MapPost("/projects/{projectId:guid}/workspace-binding/validate", ValidateWorkspaceBindingAsync).WithTags("Projects");
        group.MapDelete("/projects/{projectId:guid}/workspace-binding", DeleteWorkspaceBindingAsync).WithTags("Projects");
    }

    private static async Task<Ok<ProjectListResponse>> ListAsync(
        IProjectCatalog catalog,
        CancellationToken cancellationToken)
    {
        var projects = await catalog.ListAsync(cancellationToken);
        return TypedResults.Ok(new ProjectListResponse(projects));
    }

    private static async Task<Results<Created<ProjectDto>, BadRequest<ProblemDetails>>> RegisterAsync(
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

    private static async Task<Results<
        Ok<WorkspaceBindingDto>,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>,
        BadRequest<ProblemDetails>>> DesignateWorkspaceBindingAsync(
        Guid projectId,
        [FromBody] WorkspaceBindingDesignationRequest request,
        IWorkspaceBindingCatalog catalog,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        try
        {
            var binding = await catalog.DesignateAsync(
                new ProjectId(projectId),
                new DesignateWorkspaceBindingCommand(new NodeId(request.NodeId), request.RepositoryPath),
                clock.GetUtcNow(),
                cancellationToken);
            return TypedResults.Ok(binding);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (NodeNotFoundException ex)
        {
            return TypedResults.NotFound(Problem(
                StatusCodes.Status404NotFound,
                "Node not found",
                ex.Message));
        }
        catch (WorkspaceBindingConflictException ex)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Workspace binding conflict",
                ex.Message));
        }
        catch (WorkspaceBindingInUseException ex)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Workspace binding in use",
                ex.Message));
        }
        catch (ArgumentException ex)
        {
            return TypedResults.BadRequest(Problem(
                StatusCodes.Status400BadRequest,
                "Validation failed",
                ex.Message));
        }
    }

    private static async Task<Results<
        Ok<WorkspaceBindingDto>,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>>> ValidateWorkspaceBindingAsync(
        Guid projectId,
        IWorkspaceBindingCatalog catalog,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await catalog.GetAsync(new ProjectId(projectId), cancellationToken) is null)
            {
                return TypedResults.Conflict(Problem(
                    StatusCodes.Status409Conflict,
                    "Workspace binding missing",
                    $"Project '{projectId}' does not have a workspace binding."));
            }

            var binding = await catalog.ValidateAsync(
                new ProjectId(projectId),
                clock.GetUtcNow(),
                cancellationToken);
            return TypedResults.Ok(binding);
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (WorkspaceBindingConflictException ex)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Workspace binding conflict",
                ex.Message));
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Workspace binding unavailable",
                ex.Message));
        }
    }

    private static async Task<Results<
        NoContent,
        NotFound<ProblemDetails>,
        Conflict<ProblemDetails>>> DeleteWorkspaceBindingAsync(
        Guid projectId,
        IWorkspaceBindingCatalog catalog,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        try
        {
            await catalog.DeleteAsync(
                new ProjectId(projectId),
                clock.GetUtcNow(),
                cancellationToken);
            return TypedResults.NoContent();
        }
        catch (ProjectNotFoundException)
        {
            return TypedResults.NotFound(ProjectNotFound(projectId));
        }
        catch (WorkspaceBindingInUseException ex)
        {
            return TypedResults.Conflict(Problem(
                StatusCodes.Status409Conflict,
                "Workspace binding in use",
                ex.Message));
        }
    }
}

/// <summary>Body for creating or replacing a project's workspace designation.</summary>
internal sealed record WorkspaceBindingDesignationRequest(Guid NodeId, string RepositoryPath);

/// <summary>Response envelope for <c>GET {prefix}/projects</c>.</summary>
internal sealed record ProjectListResponse(IReadOnlyList<ProjectDto> Projects);
