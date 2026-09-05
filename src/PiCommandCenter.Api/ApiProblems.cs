using Microsoft.AspNetCore.Mvc;

namespace PiCommandCenter.Api;

/// <summary>Builds the <see cref="ProblemDetails"/> payloads shared by every endpoint mapper.</summary>
internal static class ApiProblems
{
    public static ProblemDetails Problem(int status, string title, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
    };

    public static ProblemDetails ProjectNotFound(Guid projectId) => Problem(
        StatusCodes.Status404NotFound,
        "Project not found",
        $"No project with id '{projectId}' is registered.");
}
