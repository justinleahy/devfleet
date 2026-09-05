using PiCommandCenter.Node.Verification;

namespace PiCommandCenter.Node.Repository;

/// <summary>Runs read-only git with an argument list inside the canonical repository.</summary>
public static class GitCli
{
    public static async Task<string> RunAsync(
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        GitArgvPolicy.EnsureReadOnly(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var result = await BoundedProcessRunner.RunAsync(
            "git",
            arguments,
            Path.GetFullPath(repositoryRoot),
            maxOutputBytes: 1024 * 1024,
            timeout: TimeSpan.FromSeconds(30),
            cancellationToken).ConfigureAwait(false);

        if (result.TimedOut || result.Cancelled || result.Crashed || result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} failed (exit {result.ExitCode}): {result.StandardError}");
        }

        return result.StandardOutput;
    }
}
