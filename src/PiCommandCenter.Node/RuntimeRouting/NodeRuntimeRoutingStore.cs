using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
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
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NodeRuntimeConfigurationMessage _current;

    public NodeRuntimeRoutingStore(IOptions<NodeOptions> node, IOptions<PiWorkerOptions> worker)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(worker);
        _nodeId = node.Value.Id;
        _allowedRoles = [.. worker.Value.AllowedChildRoles];
        _path = Path.Combine(worker.Value.AgentDataDirectory, "role-routes.json");

        var initial = ToRoutes(worker.Value.RoleRoutes);
        if (File.Exists(_path))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_path));
            if (ContainsRuntimeProfile(document.RootElement))
            {
                // Pre-selector routes named runtime profiles; they cannot be mapped onto
                // '<runtime>/<model>' selectors, so the configured routes take over.
                File.Delete(_path);
            }
            else
            {
                var saved = document.Deserialize<UpdateNodeRuntimeConfigurationMessage>(JsonOptions)
                    ?? throw new InvalidOperationException($"Routing configuration '{_path}' is empty.");
                initial = Normalize(saved.RoleRoutes);
            }
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
        => new(_nodeId, _allowedRoles, routes);

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
                if (!AgentModelSelector.TryParse(candidate?.Model, out var selector))
                {
                    throw new ArgumentException(
                        $"Candidate '{candidate?.Model}' for '{role}' is not a canonical '<runtime>/<model>' selector "
                        + $"(runtimes: {string.Join(", ", AgentModelSelector.Runtimes)}).",
                        nameof(routes));
                }
                if (!seen.Add(selector.Value))
                {
                    throw new ArgumentException($"Role '{role}' contains duplicate candidate '{selector.Value}'.", nameof(routes));
                }
                candidates.Add(new RuntimeRouteCandidateMessage(selector.Value));
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
                AgentModelSelector.Parse(candidate.Model).Value)).ToArray())).ToArray();

    private static bool ContainsRuntimeProfile(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(property =>
            string.Equals(property.Name, "runtimeProfile", StringComparison.OrdinalIgnoreCase)
            || ContainsRuntimeProfile(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsRuntimeProfile),
        _ => false,
    };

    private static void RestrictOwnerOnly(string path)
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}
