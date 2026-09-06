using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Projects;

/// <summary>Applies node-local path and Git policy to revisioned workspace bindings.</summary>
public sealed class WorkspaceBindingValidator : IWorkspaceBindingValidator
{
    private const int MaxGitOutputBytes = 4096;
    private const int MaxDetailCharacters = 512;
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(10);

    private readonly WorkspaceValidationOptions _options;
    private readonly string _gitExecutable;

    public WorkspaceBindingValidator(IOptions<WorkspaceValidationOptions> options)
        : this(options, "git")
    {
    }

    internal WorkspaceBindingValidator(
        IOptions<WorkspaceValidationOptions> options,
        string gitExecutable)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(gitExecutable);

        _options = options.Value;
        _gitExecutable = gitExecutable;
    }

    public async Task<WorkspaceBindingValidationResultMessage> ValidateAsync(
        WorkspaceBindingValidationRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var invalidRequestDetail = GetInvalidRequestDetail(request);
        if (invalidRequestDetail is not null)
        {
            return Invalid(request, WorkspaceValidationCodes.InvalidRequest, invalidRequestDetail);
        }

        string repositoryPath;
        try
        {
            repositoryPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.RepositoryPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Invalid(
                request,
                WorkspaceValidationCodes.InvalidRequest,
                "Repository path is not a valid absolute filesystem path.");
        }

        var finalLinkResult = InspectFinalLink(request, repositoryPath);
        if (finalLinkResult is not null)
        {
            return finalLinkResult;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(repositoryPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Invalid(request, WorkspaceValidationCodes.PathMissing, "Repository path does not exist.");
        }
        catch (PathTooLongException)
        {
            return Invalid(request, WorkspaceValidationCodes.InvalidRequest, "Repository path is too long.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Invalid(request, WorkspaceValidationCodes.Unreadable, "Repository path is not readable.");
        }

        if (!attributes.HasFlag(FileAttributes.Directory))
        {
            return Invalid(
                request,
                WorkspaceValidationCodes.PathNotDirectory,
                "Repository path is not a directory.");
        }

        if (!IsUnderApprovedRoot(repositoryPath))
        {
            return Invalid(
                request,
                WorkspaceValidationCodes.PathOutsideApprovedRoot,
                "Repository path is outside every approved root on this node.");
        }

        try
        {
            if (ContainsSymbolicLink(repositoryPath))
            {
                return Invalid(
                    request,
                    WorkspaceValidationCodes.PathSymlink,
                    "Repository path contains a symbolic link; use its canonical path.");
            }

            _ = Directory.EnumerateFileSystemEntries(repositoryPath).Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Invalid(request, WorkspaceValidationCodes.Unreadable, "Repository path is not readable.");
        }

        var gitDirectoryResult = InspectGitDirectory(request, repositoryPath);
        if (gitDirectoryResult is not null)
        {
            return gitDirectoryResult;
        }

        var gitVersion = await RunGitAsync(repositoryPath, ["--version"], useRepository: false, cancellationToken)
            .ConfigureAwait(false);
        if (!gitVersion.Available || !gitVersion.Succeeded)
        {
            return Invalid(request, WorkspaceValidationCodes.GitUnavailable, "Git is not available on this node.");
        }

        var topLevel = await RunGitAsync(
            repositoryPath,
            ["rev-parse", "--show-toplevel"],
            useRepository: true,
            cancellationToken).ConfigureAwait(false);
        if (!topLevel.Available)
        {
            return Invalid(request, WorkspaceValidationCodes.GitUnavailable, "Git is not available on this node.");
        }

        if (!topLevel.Succeeded || !IsSameCanonicalPath(repositoryPath, topLevel.StandardOutput))
        {
            return Invalid(
                request,
                WorkspaceValidationCodes.NotGitRepository,
                "Repository path is not the root of a readable Git working tree.");
        }

        var branchFormat = await RunGitAsync(
            repositoryPath,
            ["check-ref-format", "--branch", request.DefaultBranch],
            useRepository: true,
            cancellationToken).ConfigureAwait(false);
        if (!branchFormat.Available)
        {
            return Invalid(request, WorkspaceValidationCodes.GitUnavailable, "Git is not available on this node.");
        }

        if (!branchFormat.Succeeded)
        {
            return Invalid(
                request,
                WorkspaceValidationCodes.DefaultBranchMissing,
                "Configured default branch is not a valid Git branch name.");
        }

        var branchExists = await BranchExistsAsync(
            repositoryPath,
            request.DefaultBranch,
            cancellationToken).ConfigureAwait(false);
        if (branchExists is null)
        {
            return Invalid(request, WorkspaceValidationCodes.GitUnavailable, "Git is not available on this node.");
        }

        if (branchExists.Value)
        {
            return Valid(request, repositoryPath);
        }

        return Invalid(
            request,
            WorkspaceValidationCodes.DefaultBranchMissing,
            "Configured default branch does not exist locally or in a remote-tracking ref.");
    }

    private async Task<bool?> BranchExistsAsync(
        string repositoryPath,
        string branch,
        CancellationToken cancellationToken)
    {
        var local = await RunGitAsync(
            repositoryPath,
            ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"],
            useRepository: true,
            cancellationToken).ConfigureAwait(false);
        if (!local.Available)
        {
            return null;
        }

        if (local.Succeeded)
        {
            return true;
        }

        var remoteRefs = await RunGitAsync(
            repositoryPath,
            ["for-each-ref", "--format=%(refname)", "refs/remotes"],
            useRepository: true,
            cancellationToken).ConfigureAwait(false);
        if (!remoteRefs.Available)
        {
            return null;
        }

        if (remoteRefs.Succeeded)
        {
            var suffix = "/" + branch;
            if (remoteRefs.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(reference => reference.StartsWith("refs/remotes/", StringComparison.Ordinal)
                    && reference.EndsWith(suffix, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        var symbolicHead = await RunGitAsync(
            repositoryPath,
            ["symbolic-ref", "--quiet", "--short", "HEAD"],
            useRepository: true,
            cancellationToken).ConfigureAwait(false);
        if (!symbolicHead.Available)
        {
            return null;
        }

        return symbolicHead.Succeeded
            && string.Equals(symbolicHead.StandardOutput.Trim(), branch, StringComparison.Ordinal);
    }


    private WorkspaceBindingValidationResultMessage? InspectFinalLink(
        WorkspaceBindingValidationRequestMessage request,
        string repositoryPath)
    {
        try
        {
            if (new DirectoryInfo(repositoryPath).LinkTarget is not null
                || new FileInfo(repositoryPath).LinkTarget is not null)
            {
                return Invalid(
                    request,
                    WorkspaceValidationCodes.PathSymlink,
                    "Repository path is a symbolic link; use its canonical path.");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Invalid(request, WorkspaceValidationCodes.Unreadable, "Repository path is not readable.");
        }

        return null;
    }

    private WorkspaceBindingValidationResultMessage? InspectGitDirectory(
        WorkspaceBindingValidationRequestMessage request,
        string repositoryPath)
    {
        var gitPath = Path.Combine(repositoryPath, ".git");
        try
        {
            if (new DirectoryInfo(gitPath).LinkTarget is not null
                || new FileInfo(gitPath).LinkTarget is not null)
            {
                return Invalid(
                    request,
                    WorkspaceValidationCodes.PathSymlink,
                    "The repository metadata path must not be a symbolic link.");
            }

            var attributes = File.GetAttributes(gitPath);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                return Invalid(
                    request,
                    WorkspaceValidationCodes.NotGitRepository,
                    "Git worktree checkouts are not supported as canonical workspaces.");
            }

            _ = Directory.EnumerateFileSystemEntries(gitPath).Any();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Invalid(
                request,
                WorkspaceValidationCodes.NotGitRepository,
                "Repository path does not contain Git metadata.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Invalid(request, WorkspaceValidationCodes.Unreadable, "Git metadata is not readable.");
        }

        return null;
    }

    private bool IsUnderApprovedRoot(string repositoryPath)
    {
        foreach (var configuredRoot in _options.ApprovedRoots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                continue;
            }

            string approvedRoot;
            try
            {
                approvedRoot = ExpandApprovedRoot(configuredRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }

            var relative = Path.GetRelativePath(approvedRoot, repositoryPath);
            if (relative == "."
                || (!Path.IsPathRooted(relative)
                    && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExpandApprovedRoot(string configuredRoot)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = configuredRoot switch
        {
            "~" => home,
            _ when configuredRoot.StartsWith("~/", StringComparison.Ordinal)
                || configuredRoot.StartsWith("~\\", StringComparison.Ordinal) => Path.Combine(home, configuredRoot[2..]),
            _ => configuredRoot,
        };

        if (!Path.IsPathRooted(expanded))
        {
            throw new ArgumentException("Approved roots must be absolute paths.", nameof(configuredRoot));
        }

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }

    private static bool ContainsSymbolicLink(string path)
    {
        var root = Path.GetPathRoot(path)
            ?? throw new IOException("Path has no filesystem root.");
        var current = root;
        var relative = Path.GetRelativePath(root, path);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsSameCanonicalPath(string expected, string gitPath)
    {
        if (string.IsNullOrWhiteSpace(gitPath))
        {
            return false;
        }

        try
        {
            var actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gitPath.Trim()));
            return string.Equals(expected, actual, PathComparison);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private async Task<GitCommandResult> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        bool useRepository,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _gitExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        startInfo.Environment["GCM_INTERACTIVE"] = "Never";
        startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        if (useRepository)
        {
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(repositoryPath);
        }
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return GitCommandResult.Unavailable;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return GitCommandResult.Unavailable;
        }

        process.StandardInput.Close();
        var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, MaxGitOutputBytes);
        var stderrTask = process.StandardError.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(GitTimeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return GitCommandResult.Unavailable;
        }

        var standardOutput = await stdoutTask.ConfigureAwait(false);
        await stderrTask.ConfigureAwait(false);
        return new GitCommandResult(Available: true, process.ExitCode, standardOutput);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int maxBytes)
    {
        var buffer = new byte[4096];
        using var collected = new MemoryStream(maxBytes);
        while (true)
        {
            var read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            var remaining = maxBytes - (int)collected.Length;
            if (remaining > 0)
            {
                collected.Write(buffer, 0, Math.Min(remaining, read));
            }
        }

        return Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
        }
    }

    private static string? GetInvalidRequestDetail(WorkspaceBindingValidationRequestMessage request)
    {
        if (request.BindingId == Guid.Empty)
        {
            return "Binding id must be a non-empty GUID.";
        }

        if (request.ProjectId == Guid.Empty)
        {
            return "Project id must be a non-empty GUID.";
        }

        if (request.Revision <= 0)
        {
            return "Validation revision must be positive.";
        }

        if (string.IsNullOrWhiteSpace(request.RepositoryPath)
            || !Path.IsPathRooted(request.RepositoryPath)
            || request.RepositoryPath.Any(char.IsControl))
        {
            return "Repository path must be a safe absolute path.";
        }

        if (string.IsNullOrWhiteSpace(request.DefaultBranch)
            || request.DefaultBranch != request.DefaultBranch.Trim()
            || request.DefaultBranch.StartsWith('-')
            || request.DefaultBranch.Any(char.IsWhiteSpace)
            || request.DefaultBranch.Any(char.IsControl))
        {
            return "Default branch must be a safe non-empty Git branch name.";
        }

        return null;
    }

    private static WorkspaceBindingValidationResultMessage Valid(
        WorkspaceBindingValidationRequestMessage request,
        string canonicalRepositoryPath) => new(
        request.BindingId,
        request.ProjectId,
        request.Revision,
        WorkspaceValidationStatuses.Valid,
        WorkspaceValidationCodes.Valid,
        BoundDetail("Workspace validation succeeded."),
        canonicalRepositoryPath);

    private static WorkspaceBindingValidationResultMessage Invalid(
        WorkspaceBindingValidationRequestMessage request,
        string validationCode,
        string detail) => new(
        request.BindingId,
        request.ProjectId,
        request.Revision,
        WorkspaceValidationStatuses.Invalid,
        validationCode,
        BoundDetail(detail),
        CanonicalRepositoryPath: null);

    private static string BoundDetail(string detail) => detail.Length <= MaxDetailCharacters
        ? detail
        : detail[..MaxDetailCharacters];

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly record struct GitCommandResult(bool Available, int? ExitCode, string StandardOutput)
    {
        public static GitCommandResult Unavailable { get; } = new(false, null, string.Empty);
        public bool Succeeded => Available && ExitCode == 0;
    }

}
