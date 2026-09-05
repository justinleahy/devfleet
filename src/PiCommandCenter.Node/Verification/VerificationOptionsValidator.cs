using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node.Verification;

/// <summary>Validates <see cref="VerificationOptions"/> at startup so untrusted or malformed profiles fail fast.</summary>
public sealed class VerificationOptionsValidator : IValidateOptions<VerificationOptions>
{
    public ValidateOptionsResult Validate(string? name, VerificationOptions options)
    {
        if (!string.Equals(name, VerificationOptions.SectionName, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(name))
        {
            return ValidateOptionsResult.Skip;
        }

        var failures = new List<string>();

        if (options.MaxOutputBytes <= 0)
        {
            failures.Add($"'{nameof(options.MaxOutputBytes)}' must be positive.");
        }

        var seenProfileIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, profile) in options.Profiles)
        {
            if (profile is null)
            {
                failures.Add($"Profile '{key}' must not be null.");
                continue;
            }

            var id = string.IsNullOrWhiteSpace(profile.Id) ? key : profile.Id.Trim();
            if (string.IsNullOrWhiteSpace(id))
            {
                failures.Add("Each verification profile must have a non-empty id.");
                continue;
            }

            if (!seenProfileIds.Add(id))
            {
                failures.Add($"Duplicate verification profile id '{id}'.");
            }

            if (profile.Commands.Count == 0)
            {
                failures.Add($"Profile '{id}' must contain at least one command.");
            }

            var seenCommandIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var command in profile.Commands)
            {
                ValidateCommand(id, command, seenCommandIds, failures);
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateCommand(
        string profileId,
        VerificationCommandOptions command,
        HashSet<string> seenCommandIds,
        List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(command.Id))
        {
            failures.Add($"Profile '{profileId}' has a command with an empty id.");
            return;
        }

        if (!seenCommandIds.Add(command.Id))
        {
            failures.Add($"Profile '{profileId}' has duplicate command id '{command.Id}'.");
        }

        if (string.IsNullOrWhiteSpace(command.Executable)
            || command.Executable.Contains('"')
            || command.Executable.Contains('\'')
            || command.Executable.Contains('|')
            || command.Executable.Contains(';')
            || command.Executable.Contains('&')
            || command.Executable.Contains('`'))
        {
            failures.Add(
                $"Profile '{profileId}' command '{command.Id}' executable must be a trusted path without shell metacharacters.");
        }

        if (command.TimeoutSeconds <= 0)
        {
            failures.Add($"Profile '{profileId}' command '{command.Id}' timeout must be positive.");
        }

        if (string.IsNullOrWhiteSpace(command.WorkingDirectory)
            || command.WorkingDirectory.Contains('\\')
            || command.WorkingDirectory.StartsWith('/')
            || command.WorkingDirectory.Contains(".."))
        {
            failures.Add(
                $"Profile '{profileId}' command '{command.Id}' working directory must be a repository-relative POSIX path.");
        }
    }
}
