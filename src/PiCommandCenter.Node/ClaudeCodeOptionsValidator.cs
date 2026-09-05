using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>Validates <see cref="ClaudeCodeOptions"/> at startup.</summary>
public sealed class ClaudeCodeOptionsValidator : IValidateOptions<ClaudeCodeOptions>
{
    public ValidateOptionsResult Validate(string? name, ClaudeCodeOptions options)
    {
        if (!string.Equals(name, ClaudeCodeOptions.SectionName, StringComparison.Ordinal)
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

        if (options.CancelGraceMilliseconds <= 0)
        {
            failures.Add($"'{nameof(options.CancelGraceMilliseconds)}' must be positive.");
        }

        if (options.MaxLineBytes <= 0)
        {
            failures.Add($"'{nameof(options.MaxLineBytes)}' must be positive.");
        }

        if (options.MaxMalformedEvents <= 0)
        {
            failures.Add($"'{nameof(options.MaxMalformedEvents)}' must be positive.");
        }

        if (options.MaxStderrLines <= 0)
        {
            failures.Add($"'{nameof(options.MaxStderrLines)}' must be positive.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
