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

        if (!Uri.TryCreate(options.ControlPlaneUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            failures.Add($"'{nameof(options.ControlPlaneUrl)}' must be an absolute http(s) URL.");
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

        if (options.ClaimLeaseSeconds <= 0)
        {
            failures.Add($"'{nameof(options.ClaimLeaseSeconds)}' must be positive.");
        }

        if (string.IsNullOrWhiteSpace(options.EventSpoolPath))
        {
            failures.Add($"'{nameof(options.EventSpoolPath)}' must not be empty.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
