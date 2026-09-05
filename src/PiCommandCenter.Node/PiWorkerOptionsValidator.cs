using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Validates <see cref="PiWorkerOptions"/> at startup so misconfiguration fails fast.
/// </summary>
public sealed class PiWorkerOptionsValidator : IValidateOptions<PiWorkerOptions>
{
    public ValidateOptionsResult Validate(string? name, PiWorkerOptions options)
    {
        if (!string.Equals(name, PiWorkerOptions.SectionName, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(name))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.WorkerPath) || !File.Exists(options.WorkerPath))
        {
            failures.Add(
                $"'{nameof(options.WorkerPath)}' must point to the Pi worker entry point "
                + "(configure 'Pi:WorkerPath' or check out runtime/pi-worker).");
        }

        if (string.IsNullOrWhiteSpace(options.NodeExecutable))
        {
            failures.Add($"'{nameof(options.NodeExecutable)}' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.AgentDataDirectory))
        {
            failures.Add($"'{nameof(options.AgentDataDirectory)}' must not be empty.");
        }

        if (options.StartTimeoutSeconds <= 0)
        {
            failures.Add($"'{nameof(options.StartTimeoutSeconds)}' must be positive.");
        }

        if (options.MaxChildAgentsPerRequest <= 0)
        {
            failures.Add($"'{nameof(options.MaxChildAgentsPerRequest)}' must be positive.");
        }

        if (options.LeaseRenewalSeconds <= 0)
        {
            failures.Add($"'{nameof(options.LeaseRenewalSeconds)}' must be positive.");
        }

        if (options.AllowedChildRoles.Length == 0)
        {
            failures.Add($"'{nameof(options.AllowedChildRoles)}' must contain at least one role.");
        }

        if (options.AllowedRuntimeProfiles.Length == 0)
        {
            failures.Add($"'{nameof(options.AllowedRuntimeProfiles)}' must contain at least one profile.");
        }

        foreach (var role in options.AllowedChildRoles)
        {
            if (!options.RoleRoutes.TryGetValue(role, out var candidates) || candidates.Length == 0)
            {
                failures.Add($"'Pi:RoleRoutes:{role}' must contain at least one candidate.");
                continue;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                if (candidate is null || string.IsNullOrWhiteSpace(candidate.RuntimeProfile))
                {
                    failures.Add($"Every candidate in 'Pi:RoleRoutes:{role}' must name a runtime profile.");
                    continue;
                }

                if (!options.AllowedRuntimeProfiles.Contains(candidate.RuntimeProfile, StringComparer.Ordinal))
                {
                    failures.Add(
                        $"Runtime profile '{candidate.RuntimeProfile}' in 'Pi:RoleRoutes:{role}' "
                        + $"is not present in '{nameof(options.AllowedRuntimeProfiles)}'.");
                }

                if (candidate.Model is not null && string.IsNullOrWhiteSpace(candidate.Model))
                {
                    failures.Add($"Models in 'Pi:RoleRoutes:{role}' must be null or non-empty.");
                }

                var key = candidate.RuntimeProfile + "\0" + candidate.Model;
                if (!seen.Add(key))
                {
                    failures.Add(
                        $"'Pi:RoleRoutes:{role}' contains duplicate runtime/model candidate "
                        + $"'{candidate.RuntimeProfile}/{candidate.Model ?? "<default>"}'.");
                }
            }
        }

        if (options.RequestTimeoutSeconds <= 0)
        {
            failures.Add($"'{nameof(options.RequestTimeoutSeconds)}' must be positive.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
