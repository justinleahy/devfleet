using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;

namespace PiCommandCenter.Node.RuntimeRouting;

public interface IRuntimeReadinessProbe
{
    Task<IReadOnlyDictionary<string, string>> ObserveAsync(
        IReadOnlyCollection<AgentModelSelector> candidates,
        CancellationToken cancellationToken);
}

/// <summary>
/// Uses each adapter's existing executable and provider-native model/authentication surface to
/// determine whether a configured selector is launchable. Probe output is reduced to typed states;
/// raw provider output and credentials never leave the node.
/// </summary>
internal sealed class RuntimeReadinessProbe : IRuntimeReadinessProbe
{
    private readonly PiWorkerOptions _pi;
    private readonly ClaudeCodeOptions _claude;
    private readonly AntigravityOptions _antigravity;
    private readonly IRuntimeModelCommandRunner _runner;
    private readonly IMuseModelCatalogReader _muse;

    public RuntimeReadinessProbe(
        IOptions<PiWorkerOptions> pi,
        IOptions<ClaudeCodeOptions> claude,
        IOptions<AntigravityOptions> antigravity,
        IRuntimeModelCommandRunner runner,
        IMuseModelCatalogReader muse)
    {
        ArgumentNullException.ThrowIfNull(pi);
        ArgumentNullException.ThrowIfNull(claude);
        ArgumentNullException.ThrowIfNull(antigravity);
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _muse = muse ?? throw new ArgumentNullException(nameof(muse));
        _pi = pi.Value;
        _claude = claude.Value;
        _antigravity = antigravity.Value;
    }

    public async Task<IReadOnlyDictionary<string, string>> ObserveAsync(
        IReadOnlyCollection<AgentModelSelector> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var unique = candidates
            .DistinctBy(candidate => candidate.Value, StringComparer.Ordinal)
            .ToArray();
        var results = new Dictionary<string, string>(unique.Length, StringComparer.Ordinal);
        var pi = ObservePiAsync(unique.Where(candidate => candidate.UsesPiRuntime).ToArray(), cancellationToken);
        var claude = ObserveClaudeAsync(
            unique.Where(candidate => candidate.Provider == AgentModelSelector.ClaudeCode).ToArray(),
            cancellationToken);
        var antigravity = ObserveAntigravityAsync(
            unique.Where(candidate => candidate.Provider == AgentModelSelector.Antigravity).ToArray(),
            cancellationToken);
        var muse = ObserveMuseAsync(
            unique.Where(candidate => candidate.Provider == AgentModelSelector.Muse).ToArray(),
            cancellationToken);

        foreach (var observation in await Task.WhenAll(pi, claude, antigravity, muse).ConfigureAwait(false))
        {
            foreach (var pair in observation)
            {
                results[pair.Key] = pair.Value;
            }
        }

        return results;
    }

    private async Task<IReadOnlyDictionary<string, string>> ObservePiAsync(
        IReadOnlyCollection<AgentModelSelector> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Empty;
        }

        if (string.IsNullOrWhiteSpace(_pi.NodeExecutable)
            || string.IsNullOrWhiteSpace(_pi.WorkerPath)
            || !File.Exists(_pi.WorkerPath)
            || string.IsNullOrWhiteSpace(_pi.AgentDataDirectory)
            || !Directory.Exists(_pi.AgentDataDirectory))
        {
            return All(candidates, RuntimeReadinessStatuses.Unavailable);
        }

        var scriptDirectory = Path.GetDirectoryName(_pi.WorkerPath);
        if (string.IsNullOrEmpty(scriptDirectory))
        {
            return All(candidates, RuntimeReadinessStatuses.Unavailable);
        }

        var catalogScript = Path.Combine(scriptDirectory, "modelCatalog.ts");
        if (!File.Exists(catalogScript))
        {
            return All(candidates, RuntimeReadinessStatuses.Unavailable);
        }

        var result = await _runner.RunAsync(
            _pi.NodeExecutable,
            [catalogScript],
            _pi.AgentDataDirectory,
            cancellationToken).ConfigureAwait(false);
        var failure = FailureStatus(result);
        if (failure is not null)
        {
            return All(candidates, failure);
        }

        if (!TryParsePiModels(result.StandardOutput, out var evidence))
        {
            return All(candidates, RuntimeReadinessStatuses.Unknown);
        }

        return MatchPiEvidence(candidates, evidence);
    }

    private async Task<IReadOnlyDictionary<string, string>> ObserveClaudeAsync(
        IReadOnlyCollection<AgentModelSelector> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Empty;
        }

        if (string.IsNullOrWhiteSpace(_claude.Executable))
        {
            return All(candidates, RuntimeReadinessStatuses.Unavailable);
        }

        var result = await _runner.RunAsync(
            _claude.Executable,
            ["auth", "status"],
            Path.GetTempPath(),
            cancellationToken).ConfigureAwait(false);
        var failure = FailureStatus(result);
        if (failure is not null)
        {
            return All(candidates, failure);
        }

        return candidates.ToDictionary(
            candidate => candidate.Value,
            candidate => RuntimeModelDiscovery.IsMaintainedClaudeCodeModel(candidate)
                ? RuntimeReadinessStatuses.Ready
                : RuntimeReadinessStatuses.Unknown,
            StringComparer.Ordinal);
    }

    private async Task<IReadOnlyDictionary<string, string>> ObserveAntigravityAsync(
        IReadOnlyCollection<AgentModelSelector> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Empty;
        }

        if (string.IsNullOrWhiteSpace(_antigravity.Executable)
            || string.IsNullOrWhiteSpace(_pi.AgentDataDirectory)
            || !Directory.Exists(_pi.AgentDataDirectory))
        {
            return All(candidates, RuntimeReadinessStatuses.Unavailable);
        }

        var result = await _runner.RunAsync(
            _antigravity.Executable,
            ["models"],
            _pi.AgentDataDirectory,
            cancellationToken).ConfigureAwait(false);
        var failure = FailureStatus(result);
        if (failure is not null)
        {
            return All(candidates, failure);
        }

        var available = result.StandardOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2, StringSplitOptions.TrimEntries)[0])
            .Where(model => model.Length > 0)
            .Select(model => AgentModelSelector.Antigravity + "/" + model)
            .Where(model => AgentModelSelector.TryParse(model, out _))
            .ToHashSet(StringComparer.Ordinal);

        // The supported agy CLI exposes model discovery but no non-interactive auth/status
        // command. A matching catalog entry therefore remains fail-closed rather than proving
        // that the cached credential is currently usable.
        return MatchAvailable(candidates, available, RuntimeReadinessStatuses.Unknown);
    }

    private async Task<IReadOnlyDictionary<string, string>> ObserveMuseAsync(
        IReadOnlyCollection<AgentModelSelector> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return Empty;
        }

        var result = await _muse.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return All(candidates, RuntimeReadinessStatuses.Unavailable);
        }

        // MSP model/list is discovery-only: even a concrete match does not prove that the host's
        // login can launch it. Muse exposes no separate supported authentication observation.
        return All(candidates, RuntimeReadinessStatuses.Unknown);
    }

    private static IReadOnlyDictionary<string, string> MatchAvailable(
        IEnumerable<AgentModelSelector> candidates,
        IReadOnlySet<string> available,
        string availableStatus = RuntimeReadinessStatuses.Ready)
    {
        return candidates.ToDictionary(
            candidate => candidate.Value,
            candidate => available.Contains(candidate.Value)
                ? availableStatus
                : RuntimeReadinessStatuses.Unavailable,
            StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string> MatchPiEvidence(
        IEnumerable<AgentModelSelector> candidates,
        PiCatalogEvidence evidence)
    {
        return candidates.ToDictionary(
            candidate => candidate.Value,
            candidate =>
            {
                if (!evidence.Available.Contains(candidate.Value))
                {
                    return RuntimeReadinessStatuses.Unavailable;
                }

                return evidence.AuthByProvider.GetValueOrDefault(
                    candidate.Provider,
                    RuntimeReadinessStatuses.Unknown);
            },
            StringComparer.Ordinal);
    }

    private static bool TryParsePiModels(string output, out PiCatalogEvidence evidence)
    {
        var available = new HashSet<string>(StringComparer.Ordinal);
        var authByProvider = new Dictionary<string, string>(StringComparer.Ordinal);
        evidence = new PiCatalogEvidence(available, authByProvider);
        try
        {
            using var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.String
                    || !AgentModelSelector.TryParse(id.GetString(), out var selector)
                    || !selector.UsesPiRuntime)
                {
                    continue;
                }

                available.Add(selector.Value);
                var authStatus = PiAuthStatus(entry);
                if (authByProvider.TryGetValue(selector.Provider, out var prior)
                    && !string.Equals(prior, authStatus, StringComparison.Ordinal))
                {
                    authByProvider[selector.Provider] = RuntimeReadinessStatuses.Unknown;
                }
                else
                {
                    authByProvider[selector.Provider] = authStatus;
                }
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string PiAuthStatus(JsonElement entry)
    {
        if (!entry.TryGetProperty("authStatus", out var status)
            || status.ValueKind != JsonValueKind.String)
        {
            return RuntimeReadinessStatuses.Unknown;
        }

        return status.GetString() switch
        {
            RuntimeReadinessStatuses.Ready => RuntimeReadinessStatuses.Ready,
            RuntimeReadinessStatuses.Unavailable => RuntimeReadinessStatuses.Unavailable,
            _ => RuntimeReadinessStatuses.Unknown,
        };
    }

    private static string? FailureStatus(ModelCommandResult result)
    {
        if (result.TimedOut || result.Truncated)
        {
            return RuntimeReadinessStatuses.Unknown;
        }

        return result.ExitCode == 0 ? null : RuntimeReadinessStatuses.Unavailable;
    }

    private static Dictionary<string, string> All(
        IEnumerable<AgentModelSelector> candidates,
        string state)
        => candidates.ToDictionary(candidate => candidate.Value, _ => state, StringComparer.Ordinal);

    private sealed record PiCatalogEvidence(
        IReadOnlySet<string> Available,
        IReadOnlyDictionary<string, string> AuthByProvider);

    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
