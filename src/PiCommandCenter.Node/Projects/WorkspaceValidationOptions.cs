namespace PiCommandCenter.Node.Projects;

/// <summary>Node-local filesystem policy for workspace binding validation.</summary>
public sealed class WorkspaceValidationOptions
{
    public const string SectionName = "Projects";

    /// <summary>Absolute filesystem roots under which workspace repositories may reside.</summary>
    public IReadOnlyList<string> ApprovedRoots { get; set; } = ["~/Developer"];
}
