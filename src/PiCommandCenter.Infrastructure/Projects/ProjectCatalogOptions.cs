using Microsoft.Extensions.Options;

namespace PiCommandCenter.Infrastructure.Projects;

/// <summary>
/// Configuration for the project catalog: the filesystem roots a registered repository
/// must live under, and the node identifier stamped onto newly registered projects.
/// </summary>
public sealed class ProjectCatalogOptions
{
    public const string SectionName = "Projects";

    /// <summary>Approved filesystem roots; "~" expands to the current user's home.</summary>
    public IReadOnlyList<string> ApprovedRoots { get; init; } = ["~/Developer"];

    /// <summary>
    /// Node id stamped onto new projects. When unset, a stable id is derived from the
    /// machine name so registrations survive restarts.
    /// </summary>
    public Guid? NodeId { get; init; }
}
