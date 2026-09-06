using System.Diagnostics;
using System.Text.RegularExpressions;
using PiCommandCenter.Application.Git;

namespace PiCommandCenter.Node.Git;

/// <summary>
/// Process-based trusted git seam. The only code in the node that may execute git, and even here
/// only supervisor-owned workspace initialization, request-branch creation, and explicit
/// checkpoint commits run, always as argument vectors (never a shell). Push, merge, rebase,
/// reset, clean, stash, persistent config, hooks, credential helpers, and remotes are unreachable
/// by construction. Failures surface as <see cref="InvalidOperationException"/> with the git
/// stderr tail; nothing is parsed beyond commit ids, refs, root paths, and worktree metadata.
/// </summary>
public sealed partial class RestrictedGitService(string executable, TimeSpan timeout) : ITrustedGitService
{
    private const int MaxErrorTail = 512;

    /// <summary>The exact baseline commit message used for workspace initialization.</summary>
    public const string BaselineCommitMessage = "Initialize workspace for DevFleet";
    private const string BaselineAuthorName = "DevFleet Supervisor";
    private const string BaselineAuthorEmail = "devfleet@localhost";

    private static readonly string DevNull = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";

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

        // Idempotent for the same request's retry: an existing branch that still sits exactly
        // at the default branch tip is the branch this call would have created. A divergent
        // preexisting branch is never adopted — it belongs to something else.
        var head = await RunAsync(
            request.RepositoryPath,
            ["rev-parse", "--verify", "--quiet", $"refs/heads/{request.BranchName}^{{commit}}"],
            GitExpectation.MayFail,
            cancellationToken).ConfigureAwait(false);

        var baseCommit = await RunAsync(
            request.RepositoryPath,
            ["rev-parse", "--verify", $"refs/heads/{request.DefaultBranch}^{{commit}}"],
            GitExpectation.SuccessWithOutput,
            cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(head))
        {
            if (string.Equals(head.Trim(), baseCommit.Trim(), StringComparison.Ordinal))
            {
                await RunAsync(
                    request.RepositoryPath,
                    ["checkout", request.BranchName],
                    GitExpectation.Success,
                    cancellationToken).ConfigureAwait(false);
                return new RequestBranchCreated(request.BranchName, baseCommit.Trim());
            }

            throw new InvalidOperationException(
                $"Request branch '{request.BranchName}' already exists in '{request.RepositoryPath}' "
                + "and does not match the default branch tip; refusing to adopt a divergent branch.");
        }

        await RunAsync(
            request.RepositoryPath,
            ["checkout", "-b", request.BranchName, request.DefaultBranch],
            GitExpectation.Success,
            cancellationToken).ConfigureAwait(false);

        return new RequestBranchCreated(request.BranchName, baseCommit.Trim());

    }

    /// <summary>
    /// Prepares the assigned workspace before any session starts. An ordinary directory is
    /// initialized on the configured branch with exactly one baseline commit of all nonignored
    /// contents; an unborn repository gets the same baseline; a repository with commits is only
    /// revalidated (root, branch, clean tree) and never re-initialized or re-committed. Linked
    /// worktrees and directories nested inside another repository are refused. Retries after an
    /// interrupted preparation converge to the same single baseline commit.
    /// </summary>
    public async Task<WorkspacePreparation> PrepareWorkspaceAsync(
        WorkspacePreparationRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRef(request.DefaultBranch, nameof(request.DefaultBranch));
        if (!Directory.Exists(request.RepositoryPath))
        {
            throw new InvalidOperationException(
                $"Workspace path '{request.RepositoryPath}' does not exist; refusing to prepare.");
        }

        var expectedRoot = Path.GetFullPath(request.RepositoryPath);

        // Nesting check: a directory inside another repository's work tree is never adopted as
        // its own workspace, and a linked worktree never becomes the session root.
        var inside = await RunAsync(
            expectedRoot,
            ["rev-parse", "--is-inside-work-tree"],
            GitExpectation.MayFail,
            cancellationToken).ConfigureAwait(false);
        var isRepository = string.Equals(inside.Trim(), "true", StringComparison.Ordinal);
        if (isRepository)
        {
            var topLevel = await RunAsync(
                expectedRoot,
                ["rev-parse", "--show-toplevel"],
                GitExpectation.SuccessWithOutput,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    Path.GetFullPath(topLevel.Trim()), expectedRoot, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workspace path '{expectedRoot}' is nested inside repository '{topLevel.Trim()}'; refusing to prepare.");
            }

            var gitDir = await RunAsync(
                expectedRoot,
                ["rev-parse", "--git-dir"],
                GitExpectation.SuccessWithOutput,
                cancellationToken).ConfigureAwait(false);
            var commonDir = await RunAsync(
                expectedRoot,
                ["rev-parse", "--git-common-dir"],
                GitExpectation.SuccessWithOutput,
                cancellationToken).ConfigureAwait(false);
            if (!string.Equals(
                    Path.GetFullPath(Path.Combine(expectedRoot, gitDir.Trim())),
                    Path.GetFullPath(Path.Combine(expectedRoot, commonDir.Trim())),
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Workspace path '{expectedRoot}' is a linked worktree; refusing to prepare.");
            }
        }

        if (!isRepository)
        {
            await RunAsync(
                expectedRoot,
                ["init", "--initial-branch", request.DefaultBranch],
                GitExpectation.Success,
                cancellationToken).ConfigureAwait(false);
        }

        var head = await RunAsync(
            expectedRoot,
            ["rev-parse", "--verify", "--quiet", "HEAD"],
            GitExpectation.MayFail,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(head))
        {
            // Unborn HEAD (fresh init or an interrupted earlier attempt): land the one baseline
            // commit. Point HEAD at the configured branch first — an existing unborn repository
            // may have been initialized with a different default branch name.
            await RunAsync(
                expectedRoot,
                ["symbolic-ref", "HEAD", $"refs/heads/{request.DefaultBranch}"],
                GitExpectation.Success,
                cancellationToken).ConfigureAwait(false);
            // Stage every nonignored path; .gitignore is honored by add itself.
            await RunAsync(
                expectedRoot,
                ["add", "--all", "--", "."],
                GitExpectation.Success,
                cancellationToken).ConfigureAwait(false);
            // Fixed command-local identity and exact message; empty workspaces still get the
            // baseline commit so every request starts from a real commit.
            await RunAsync(
                expectedRoot,
                [
                    "-c", $"user.name={BaselineAuthorName}",
                    "-c", $"user.email={BaselineAuthorEmail}",
                    "commit", "--allow-empty", "-m", BaselineCommitMessage,
                ],
                GitExpectation.Success,
                cancellationToken).ConfigureAwait(false);

            head = await RunAsync(
                expectedRoot,
                ["rev-parse", "HEAD"],
                GitExpectation.SuccessWithOutput,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // Existing history is never touched: revalidate branch and clean tree only.
            var branch = await RunAsync(
                expectedRoot,
                ["symbolic-ref", "--short", "HEAD"],
                GitExpectation.SuccessWithOutput,
                cancellationToken).ConfigureAwait(false);
            var currentBranch = branch.Trim();
            if (!string.Equals(currentBranch, request.DefaultBranch, StringComparison.Ordinal))
            {
                var requestBranch = PiRequestGit.RequestBranchName(request.RequestId);
                var defaultTip = await RunAsync(
                    expectedRoot,
                    ["rev-parse", $"refs/heads/{request.DefaultBranch}"],
                    GitExpectation.SuccessWithOutput,
                    cancellationToken).ConfigureAwait(false);
                if (!string.Equals(currentBranch, requestBranch, StringComparison.Ordinal)
                    || !string.Equals(head.Trim(), defaultTip.Trim(), StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Workspace '{expectedRoot}' is on branch '{currentBranch}' but the assignment expects '{request.DefaultBranch}'.");
                }

                await RunAsync(
                    expectedRoot,
                    ["checkout", request.DefaultBranch],
                    GitExpectation.Success,
                    cancellationToken).ConfigureAwait(false);
            }

            var status = await RunAsync(
                expectedRoot,
                ["status", "--porcelain"],
                GitExpectation.Success,
                cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(status))
            {
                throw new InvalidOperationException(
                    $"Workspace '{expectedRoot}' has uncommitted changes; refusing to prepare.");
            }
        }

        return new WorkspacePreparation(expectedRoot, request.DefaultBranch, head.Trim());
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
        // Isolated from every ambient configuration source: no global/system config, no hooks,
        // no credential helpers, no gpg signing, no interactive prompts. Repository config is
        // overridden per command below where it matters (identity, hooks, signing).
        startInfo.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        startInfo.Environment["GIT_CONFIG_GLOBAL"] = DevNull;
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add($"core.hooksPath={DevNull}");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("credential.helper=");
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("commit.gpgsign=false");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{executable}'.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        string stdout;
        string stderr;
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Git operation timed out after {timeout}.");
        }
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
