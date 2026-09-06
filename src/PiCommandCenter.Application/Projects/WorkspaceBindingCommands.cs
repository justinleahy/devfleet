using PiCommandCenter.Domain;

namespace PiCommandCenter.Application.Projects;

/// <summary>
/// Designates a node-local repository path as a project's workspace binding.
/// </summary>
public sealed record DesignateWorkspaceBindingCommand(
    NodeId NodeId,
    string RepositoryPath);
