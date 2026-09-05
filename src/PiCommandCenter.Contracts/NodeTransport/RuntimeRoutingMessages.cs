namespace PiCommandCenter.Contracts.NodeTransport;

/// <summary>One runtime/model choice in an ordered role route.</summary>
public sealed record RuntimeRouteCandidateMessage(string Model);

/// <summary>The ordered runtime candidates assigned to one agent role.</summary>
public sealed record RuntimeRoleRouteMessage(
    string Role,
    IReadOnlyList<RuntimeRouteCandidateMessage> Candidates);

/// <summary>One model reported as callable by a node runtime.</summary>
public sealed record RuntimeModelMessage(string Id, string DisplayName, string? Provider);

/// <summary>Models discovered for one provider, or its discovery error.</summary>
public sealed record RuntimeModelCatalogMessage(
    string Provider,
    IReadOnlyList<RuntimeModelMessage> Models,
    string? Error);

/// <summary>Live node-owned routing configuration.</summary>
public sealed record NodeRuntimeConfigurationMessage(
    Guid NodeId,
    IReadOnlyList<string> AllowedRoles,
    IReadOnlyList<RuntimeRoleRouteMessage> RoleRoutes);

/// <summary>Complete replacement for a node's ordered role routes.</summary>
public sealed record UpdateNodeRuntimeConfigurationMessage(
    IReadOnlyList<RuntimeRoleRouteMessage> RoleRoutes);
