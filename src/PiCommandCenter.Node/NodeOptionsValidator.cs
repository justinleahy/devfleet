using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Validates <see cref="NodeOptions"/> at startup so misconfiguration fails fast.
/// </summary>
public sealed class NodeOptionsValidator : IValidateOptions<NodeOptions>
{
    public ValidateOptionsResult Validate(string? name, NodeOptions options)
    {
        if (!string.Equals(name, NodeOptions.SectionName, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(name))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        var controlPlaneUrlFailure = GetControlPlaneUrlFailure(options.ControlPlaneUrl, out _);
        if (controlPlaneUrlFailure is not null)
        {
            failures.Add(controlPlaneUrlFailure);
        }

        if (options.Id == Guid.Empty)
        {
            failures.Add($"'{nameof(options.Id)}' must be a non-empty GUID.");
        }

        if (string.IsNullOrWhiteSpace(options.DisplayName))
        {
            failures.Add($"'{nameof(options.DisplayName)}' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.AgentVersion))
        {
            failures.Add($"'{nameof(options.AgentVersion)}' must not be empty.");
        }

        if (string.IsNullOrWhiteSpace(options.CapabilitiesJson))
        {
            failures.Add($"'{nameof(options.CapabilitiesJson)}' must not be empty.");
        }

        if (options.HeartbeatSeconds <= 0)
        {
            failures.Add($"'{nameof(options.HeartbeatSeconds)}' must be positive.");
        }

        if (options.MaxConcurrentRequests <= 0)
        {
            failures.Add($"'{nameof(options.MaxConcurrentRequests)}' must be positive.");
        }


        if (options.ClaimLeaseSeconds <= 0)
        {
            failures.Add($"'{nameof(options.ClaimLeaseSeconds)}' must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.EventSpoolPath))
        {
            failures.Add($"'{nameof(options.EventSpoolPath)}' must not be empty.");
        }

        if (options.RecoveryCooperativeStopSeconds <= 0)
        {
            failures.Add($"'{nameof(options.RecoveryCooperativeStopSeconds)}' must be positive.");
        }

        if (options.RecoveryTerminationSeconds <= 0)
        {
            failures.Add($"'{nameof(options.RecoveryTerminationSeconds)}' must be positive.");
        }

        if (options.RecoveryAttemptSeconds <= 0)
        {
            failures.Add($"'{nameof(options.RecoveryAttemptSeconds)}' must be positive.");
        }
        else if (options.RecoveryCooperativeStopSeconds > 0
            && options.RecoveryTerminationSeconds > 0
            && options.RecoveryAttemptSeconds
                < options.RecoveryCooperativeStopSeconds + options.RecoveryTerminationSeconds)
        {
            failures.Add(
                $"'{nameof(options.RecoveryAttemptSeconds)}' must be at least "
                + $"'{nameof(options.RecoveryCooperativeStopSeconds)}' + "
                + $"'{nameof(options.RecoveryTerminationSeconds)}'.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    internal static Uri CreateControlPlaneUri(string? configuredUrl)
    {
        var failure = GetControlPlaneUrlFailure(configuredUrl, out var uri);
        return failure is null
            ? uri!
            : throw new InvalidOperationException(failure);
    }

    private static string? GetControlPlaneUrlFailure(string? configuredUrl, out Uri? uri)
    {
        if (!Uri.TryCreate(configuredUrl, UriKind.Absolute, out var candidate)
            || (candidate.Scheme != Uri.UriSchemeHttp && candidate.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(candidate.Host))
        {
            uri = null;
            return $"'{nameof(NodeOptions.ControlPlaneUrl)}' must be an absolute http(s) URL.";
        }

        if (!string.IsNullOrEmpty(candidate.UserInfo))
        {
            uri = null;
            return $"'{nameof(NodeOptions.ControlPlaneUrl)}' must not contain user information.";
        }

        if (candidate.Scheme == Uri.UriSchemeHttp && !candidate.IsLoopback)
        {
            uri = null;
            return $"'{nameof(NodeOptions.ControlPlaneUrl)}' must use HTTPS unless the host is loopback.";
        }

        uri = candidate;
        return null;
    }
}
