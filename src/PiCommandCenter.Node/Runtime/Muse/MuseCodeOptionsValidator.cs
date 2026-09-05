using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>Fails fast on empty or non-positive Muse options.</summary>
public sealed class MuseCodeOptionsValidator : IValidateOptions<MuseCodeOptions>
{
    public ValidateOptionsResult Validate(string? name, MuseCodeOptions options)
    {
        if (!string.Equals(name, MuseCodeOptions.SectionName, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(name))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.Executable))
        {
            failures.Add($"'{nameof(options.Executable)}' must not be empty.");
        }

        if (options.StartTimeoutSeconds <= 0)
        {
            failures.Add($"'{nameof(options.StartTimeoutSeconds)}' must be positive.");
        }

        if (options.RequestTimeoutSeconds <= 0)
        {
            failures.Add($"'{nameof(options.RequestTimeoutSeconds)}' must be positive.");
        }

        if (options.CancelGraceSeconds <= 0)
        {
            failures.Add($"'{nameof(options.CancelGraceSeconds)}' must be positive.");
        }

        if (options.MaxStderrLines <= 0)
        {
            failures.Add($"'{nameof(options.MaxStderrLines)}' must be positive.");
        }

        if (options.MaxLineBytes <= 0)
        {
            failures.Add($"'{nameof(options.MaxLineBytes)}' must be positive.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
