using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.RuntimeRouting;

public interface INodeRuntimeRoutingStore
{
    NodeRuntimeConfigurationMessage Current { get; }
    Task<NodeRuntimeConfigurationMessage> UpdateAsync(
        UpdateNodeRuntimeConfigurationMessage update,
        CancellationToken cancellationToken = default);
}

/// <summary>Owns the live role routes and their durable node-local override.</summary>
public sealed class NodeRuntimeRoutingStore : INodeRuntimeRoutingStore, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly Guid _nodeId;
    private readonly string[] _allowedRoles;
    private readonly string[] _allowedProfiles;
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NodeRuntimeConfigurationMessage _current;

    public NodeRuntimeRoutingStore(IOptions<NodeOptions> node, IOptions<PiWorkerOptions> worker)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(worker);
        _nodeId = node.Value.Id;
        _allowedRoles = [.. worker.Value.AllowedChildRoles];
        _allowedProfiles = [.. worker.Value.AllowedRuntimeProfiles];
        _path = Path.Combine(worker.Value.AgentDataDirectory, "role-routes.json");

        var initial = ToRoutes(worker.Value.RoleRoutes);
        if (File.Exists(_path))
        {
            var saved = JsonSerializer.Deserialize<UpdateNodeRuntimeConfigurationMessage>(
                File.ReadAllText(_path), JsonOptions)
                ?? throw new InvalidOperationException($"Routing configuration '{_path}' is empty.");
            initial = Normalize(saved.RoleRoutes);
        }
        _current = Build(initial);
    }

    public NodeRuntimeConfigurationMessage Current => Volatile.Read(ref _current);
    public async Task<NodeRuntimeConfigurationMessage> UpdateAsync(
        UpdateNodeRuntimeConfigurationMessage update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var normalized = Normalize(update.RoleRoutes);
        var next = Build(normalized);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllTextAsync(
                    temporary,
                    JsonSerializer.Serialize(new UpdateNodeRuntimeConfigurationMessage(normalized), JsonOptions),
                    cancellationToken).ConfigureAwait(false);
                RestrictOwnerOnly(temporary);
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
            Volatile.Write(ref _current, next);

            return next;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose()
    {
        _writeGate.Dispose();
    }

    private NodeRuntimeConfigurationMessage Build(IReadOnlyList<RuntimeRoleRouteMessage> routes)
        => new(_nodeId, _allowedRoles, _allowedProfiles, routes);

    private IReadOnlyList<RuntimeRoleRouteMessage> Normalize(
        IReadOnlyList<RuntimeRoleRouteMessage>? routes)
    {
        if (routes is null)
        {
            throw new ArgumentException("Role routes are required.", nameof(routes));
        }
        var byRole = new Dictionary<string, RuntimeRoleRouteMessage>(StringComparer.Ordinal);
        foreach (var route in routes)
        {
            if (route is null || string.IsNullOrWhiteSpace(route.Role))
            {
                throw new ArgumentException("Every route must name a role.", nameof(routes));
            }
            var role = route.Role.Trim();
            if (!_allowedRoles.Contains(role, StringComparer.Ordinal))
            {
                throw new ArgumentException($"Role '{role}' is not allowed by this node.", nameof(routes));
            }
            if (!byRole.TryAdd(role, route))
            {
                throw new ArgumentException($"Role '{role}' appears more than once.", nameof(routes));
            }
        }

        var result = new List<RuntimeRoleRouteMessage>(_allowedRoles.Length);
        foreach (var role in _allowedRoles)
        {
            if (!byRole.TryGetValue(role, out var route) || route.Candidates is null || route.Candidates.Count == 0)
            {
                throw new ArgumentException($"Role '{role}' must contain at least one candidate.", nameof(routes));
            }
            if (route.Candidates.Count > 16)
            {
                throw new ArgumentException($"Role '{role}' cannot contain more than 16 candidates.", nameof(routes));
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var candidates = new List<RuntimeRouteCandidateMessage>(route.Candidates.Count);
            foreach (var candidate in route.Candidates)
            {
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.RuntimeProfile))
                {
                    throw new ArgumentException($"Every candidate for '{role}' must name a runtime profile.", nameof(routes));
                }
                var profile = candidate.RuntimeProfile.Trim();
                if (!_allowedProfiles.Contains(profile, StringComparer.Ordinal))
                {
                    throw new ArgumentException($"Runtime profile '{profile}' is not allowed by this node.", nameof(routes));
                }
                var model = candidate.Model?.Trim();
                if (candidate.Model is not null && string.IsNullOrEmpty(model))
                {
                    throw new ArgumentException($"Models for '{role}' must be null or non-empty.", nameof(routes));
                }
                if (model is { Length: > 256 })
                {
                    throw new ArgumentException($"A model for '{role}' exceeds 256 characters.", nameof(routes));
                }
                if (!seen.Add(profile + "\0" + model))
                {
                    throw new ArgumentException($"Role '{role}' contains duplicate candidate '{profile}/{model ?? "<default>"}'.", nameof(routes));
                }
                candidates.Add(new RuntimeRouteCandidateMessage(profile, model));
            }
            result.Add(new RuntimeRoleRouteMessage(role, candidates));
        }
        return result;
    }

    private static IReadOnlyList<RuntimeRoleRouteMessage> ToRoutes(
        IReadOnlyDictionary<string, AgentRoleRouteCandidate[]> routes)
        => routes.Select(pair => new RuntimeRoleRouteMessage(
            pair.Key,
            pair.Value.Select(candidate => new RuntimeRouteCandidateMessage(
                candidate.RuntimeProfile, candidate.Model)).ToArray())).ToArray();

    private static void RestrictOwnerOnly(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
