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

        if (options.AllowedChildRoles.Length == 0)
        {
            failures.Add($"'{nameof(options.AllowedChildRoles)}' must contain at least one role.");
        }

        if (options.AllowedRuntimeProfiles.Length == 0)
        {
            failures.Add($"'{nameof(options.AllowedRuntimeProfiles)}' must contain at least one profile.");
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
