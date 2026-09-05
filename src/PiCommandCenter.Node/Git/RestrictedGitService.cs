using System.Diagnostics;
using System.Text.RegularExpressions;
using PiCommandCenter.Application.Git;

namespace PiCommandCenter.Node.Git;

/// <summary>
/// Process-based trusted git seam. The only code in the node that may execute git, and even here
/// only two whitelisted operations run, always as an argument vector (never a shell): creating a
/// request branch from the project's default branch, and committing explicitly listed paths as a
/// final checkpoint. Everything else — push, merge, rebase, reset, clean, stash, config,
/// credential helpers — is unreachable by construction. Failures surface as
/// <see cref="InvalidOperationException"/> with the git stderr tail; nothing is ever parsed
/// beyond commit ids and refs.
/// </summary>
public sealed partial class RestrictedGitService(string executable, TimeSpan timeout) : ITrustedGitService
{
    private const int MaxErrorTail = 512;

    public RestrictedGitService()
        : this("git", TimeSpan.FromSeconds(30))
    {
    }

    public async Task<RequestBranchCreated> CreateRequestBranchAsync(
        RequestBranchRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRef(request.DefaultBranch, nameof(request.DefaultBranch));
        ValidateRef(request.BranchName, nameof(request.BranchName));

        // The branch must not already exist: request branches are created exactly once.
        var head = await RunAsync(
            request.RepositoryPath,
            ["rev-parse", "--verify", "--quiet", $"refs/heads/{request.BranchName}"],
            GitExpectation.MayFail,
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(head))
        {
            throw new InvalidOperationException(
                $"Request branch '{request.BranchName}' already exists in '{request.RepositoryPath}'.");
        }

        var baseCommit = await RunAsync(
            request.RepositoryPath,
            ["rev-parse", "--verify", $"refs/heads/{request.DefaultBranch}^{{commit}}"],
            GitExpectation.SuccessWithOutput,
            cancellationToken).ConfigureAwait(false);

        await RunAsync(
            request.RepositoryPath,
            ["checkout", "-b", request.BranchName, request.DefaultBranch],
            GitExpectation.Success,
            cancellationToken).ConfigureAwait(false);

        return new RequestBranchCreated(request.BranchName, baseCommit.Trim());
    }

    public async Task<CheckpointCommitted> CreateCheckpointCommitAsync(
        CheckpointCommitRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRef(request.BranchName, nameof(request.BranchName));
        if (request.Paths.Count == 0)
        {
            throw new ArgumentException("A checkpoint commit must list at least one path.", nameof(request));
        }
        if (request.Paths.Any(p => p.StartsWith('-')))
        {
            throw new ArgumentException("Checkpoint paths must not start with '-'.", nameof(request));
        }

        await EnsureOnBranchAsync(request.RepositoryPath, request.BranchName, cancellationToken)
            .ConfigureAwait(false);

        await RunAsync(
            request.RepositoryPath,
            ["add", "--", .. request.Paths],
            GitExpectation.Success,
            cancellationToken).ConfigureAwait(false);

        await RunAsync(
            request.RepositoryPath,
            ["commit", "-m", request.Message],
            GitExpectation.Success,
            cancellationToken).ConfigureAwait(false);

        var commitId = await RunAsync(
            request.RepositoryPath,
            ["rev-parse", "HEAD"],
            GitExpectation.SuccessWithOutput,
            cancellationToken).ConfigureAwait(false);

        return new CheckpointCommitted(commitId.Trim(), request.BranchName);
    }

    // The current branch must equal the request branch: a checkpoint commit landed on any other
    // branch would silently diverge from the request's history.
    private async Task EnsureOnBranchAsync(
        string repositoryPath,
        string branchName,
        CancellationToken cancellationToken)
    {
        var current = await RunAsync(
            repositoryPath,
            ["rev-parse", "--abbrev-ref", "HEAD"],
            GitExpectation.SuccessWithOutput,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current.Trim(), branchName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Checkpoint commit requires branch '{branchName}' but repository is on '{current.Trim()}'.");
        }
    }

    private static void ValidateRef(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value) || !RefName().IsMatch(value) || value.Contains(".."))
        {
            throw new ArgumentException($"Ref name '{value}' is not a safe git ref.", paramName);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/()-]{0,127}$")]
    private static partial Regex RefName();

    private async Task<string> RunAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        GitExpectation expectation,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var started = Stopwatch.GetTimestamp();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (Stopwatch.GetElapsedTime(started) > timeout)
        {
            throw new InvalidOperationException($"Git operation timed out after {timeout}.");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        if (expectation == GitExpectation.Success && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git {arguments[0]} failed with exit code {process.ExitCode}: {Tail(stderr)}");
        }

        if (expectation == GitExpectation.SuccessWithOutput
            && (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout)))
        {
            throw new InvalidOperationException(
                $"Git {arguments[0]} failed with exit code {process.ExitCode}: {Tail(stderr)}");
        }

        return stdout;
    }

    private enum GitExpectation
    {
        MayFail,
        Success,
        SuccessWithOutput,
    }

    private static string Tail(string text) =>
        text.Length <= MaxErrorTail ? text : text[^MaxErrorTail..];
}
