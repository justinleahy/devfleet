using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;

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

        ValidateRootModel(options.Model, failures);

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
                var selector = ValidateSelector(candidate?.Model, $"Every candidate in 'Pi:RoleRoutes:{role}'", failures);
                if (selector is not null && !seen.Add(selector.Value))
                {
                    failures.Add($"'Pi:RoleRoutes:{role}' contains duplicate model candidate '{selector.Value}'.");
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

    private static void ValidateRootModel(string? value, List<string> failures)
    {
        var selector = ValidateSelector(value, $"'Pi:{nameof(PiWorkerOptions.Model)}'", failures);
        if (selector is not null && !selector.UsesPiRuntime)
        {
            failures.Add(
                $"'Pi:{nameof(PiWorkerOptions.Model)}' must name a Pi-backed provider "
                + $"(not an official harness provider: {string.Join(", ", AgentModelSelector.OfficialHarnessProviders)}); "
                + $"got '{selector.Value}'.");
        }
    }

    private static AgentModelSelector? ValidateSelector(string? value, string subject, List<string> failures)
    {
        if (AgentModelSelector.TryParse(value, out var selector))
        {
            return selector;
        }

        failures.Add(
            $"{subject} must be a canonical '<provider>/<model>' selector; got '{value}'.");
        return null;
    }
}
