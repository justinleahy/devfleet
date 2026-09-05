using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Projects;

/// <summary>
/// EF Core backed project catalog. Registration validates the repository on disk — path
/// existence, containment under an approved root, git presence, readability, executable
/// availability, and default branch — without ever shell-concatenating untrusted input
/// (all git invocations go through <see cref="ProcessStartInfo.ArgumentList"/>).
/// </summary>
public sealed class ProjectCatalog(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IOptions<ProjectCatalogOptions> options,
    IProjectionNotifier notifier) : IProjectCatalog
{
    public async Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var projects = await db.Projects
            .AsNoTracking()
            .OrderBy(p => p.CreatedAt)
            .ThenBy(p => p.DisplayName)
            .ToListAsync(cancellationToken);

        return projects.Select(ToDto).ToList();
    }

    public async Task<ProjectDto> GetAsync(ProjectId id, CancellationToken cancellationToken = default)
    {
        var project = await db.Projects
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

        return project is null ? throw new ProjectNotFoundException(id.Value) : ToDto(project);
    }

    public async Task<ProjectValidationReport> ValidateAsync(
        RegisterProjectCommand command,
        CancellationToken cancellationToken = default)
    {
        var errors = await CollectValidationErrorsAsync(command, cancellationToken);
        return errors.Count == 0
            ? ProjectValidationReport.Success
            : ProjectValidationReport.Failure(errors);
    }

    public async Task<ProjectDto> RegisterAsync(
        RegisterProjectCommand command,
        CancellationToken cancellationToken = default)
    {
        var errors = await CollectValidationErrorsAsync(command, cancellationToken);
        if (errors.Count > 0)
        {
            throw new ProjectValidationException(errors);
        }

        var repositoryPath = Project.CanonicalizePath(command.RepositoryPath);
        var duplicate = await db.Projects
            .AnyAsync(p => p.RepositoryPath == repositoryPath, cancellationToken);
        if (duplicate)
        {
            throw new DuplicateProjectException(repositoryPath);
        }

        var project = Project.Register(
            ResolveNodeId(),
            command.DisplayName,
            command.RepositoryPath,
            command.DefaultBranch,
            command.Enabled,
            command.MaxActiveWriteRequests,
            command.MaxReadOnlyRequests,
            command.MaxChildAgentsPerRequest,
            command.RequireCleanStart,
            command.CreateRequestBranch,
            command.CreateRequestCommit,
            command.AutoMerge,
            clock.GetUtcNow());

        db.Projects.Add(project);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        // Losing a race against a concurrent registration falls back to the unique index.
        catch (DbUpdateException)
        {
            throw new DuplicateProjectException(repositoryPath);
        }

        notifier.Publish(ProjectionChange.Fleet());

        return ToDto(project);
    }

    private async Task<List<string>> CollectValidationErrorsAsync(
        RegisterProjectCommand command,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(command.DisplayName))
        {
            errors.Add("Display name must not be empty.");
        }

        var limits = new (int Value, string Name)[]
        {
            (command.MaxActiveWriteRequests, nameof(RegisterProjectCommand.MaxActiveWriteRequests)),
            (command.MaxReadOnlyRequests, nameof(RegisterProjectCommand.MaxReadOnlyRequests)),
            (command.MaxChildAgentsPerRequest, nameof(RegisterProjectCommand.MaxChildAgentsPerRequest)),
        };
        foreach (var (value, name) in limits)
        {
            if (value < 1)
            {
                errors.Add($"{name} must be a positive integer.");
            }
        }

        var repositoryPath = await ValidateRepositoryPathAsync(command.RepositoryPath, errors, cancellationToken);
        if (repositoryPath is not null)
        {
            await ValidateDefaultBranchAsync(repositoryPath, command.DefaultBranch, errors, cancellationToken);
        }

        return errors;
    }

    /// <returns>The canonical repository path when basic path checks pass; otherwise null.</returns>
    private async Task<string?> ValidateRepositoryPathAsync(
        string? repositoryPath,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            errors.Add("Repository path must not be empty.");
            return null;
        }

        string fullPath;
        try
        {
            if (!Path.IsPathRooted(repositoryPath))
            {
                errors.Add("Repository path must be an absolute path.");
                return null;
            }

            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            errors.Add("Repository path is not a valid filesystem path.");
            return null;
        }

        if (!Directory.Exists(fullPath))
        {
            errors.Add($"Repository path '{fullPath}' does not exist or is not a directory.");
            return null;
        }

        // A symlinked repository directory would register an alias identity: duplicate
        // detection and lease path checks key on the stored path, so only the canonical
        // (dereferenced) directory may ever be registered.
        try
        {
            var link = Directory.ResolveLinkTarget(fullPath, returnFinalTarget: true);
            if (link is not null)
            {
                errors.Add(
                    $"Repository path '{fullPath}' is a symlink alias; register '{link.FullName}' instead.");
                return null;
            }
        }
        catch (IOException)
        {
            errors.Add($"Repository path '{fullPath}' is a broken symlink.");
            return null;
        }

        if (!IsUnderApprovedRoot(fullPath))
        {
            errors.Add($"Repository path '{fullPath}' is not under an approved root.");
            return null;
        }

        if (!Directory.Exists(Path.Combine(fullPath, ".git")) && !File.Exists(Path.Combine(fullPath, ".git")))
        {
            errors.Add($"Repository path '{fullPath}' does not contain a .git repository.");
            return null;
        }

        try
        {
            await Task.Run(
                () => _ = Directory.EnumerateFileSystemEntries(fullPath).Any(),
                cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            errors.Add($"Repository path '{fullPath}' is not readable.");
        }

        return fullPath;
    }

    private bool IsUnderApprovedRoot(string fullPath)
    {
        foreach (var root in options.Value.ApprovedRoots)
        {
            string expandedRoot;
            try
            {
                expandedRoot = ExpandRoot(root);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException)
            {
                continue;
            }

            if (fullPath.Equals(expandedRoot, StringComparison.Ordinal)
                || fullPath.StartsWith(expandedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExpandRoot(string root)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expanded = root switch
        {
            "~" => home,
            _ when root.StartsWith("~/", StringComparison.Ordinal) => Path.Combine(home, root[2..]),
            _ when root.StartsWith("~\\", StringComparison.Ordinal) => Path.Combine(home, root[2..]),
            _ => root,
        };

        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded));
    }


    private async Task ValidateDefaultBranchAsync(
        string repositoryPath,
        string? defaultBranch,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(defaultBranch))
        {
            errors.Add("Default branch must not be empty.");
            return;
        }

        var branch = defaultBranch.Trim();
        if (branch.Any(char.IsWhiteSpace) || branch.StartsWith('-'))
        {
            errors.Add($"Default branch '{branch}' is not a valid branch name.");
            return;
        }

        if (await RunGitAsync(repositoryPath, ["show-ref", "--verify", "--quiet", $"refs/heads/{branch}"], cancellationToken))
        {
            return;
        }

        if (!await RunGitAsync(repositoryPath, ["rev-parse", "--git-dir"], cancellationToken))
        {
            errors.Add("The git executable is not available or the repository is not a valid git repository.");
            return;
        }

        // An unborn repository has no refs at all; accept the requested branch only if
        // symbolic HEAD (the branch a first commit would land on) names it exactly.
        var (symbolicRefSucceeded, symbolicRefOutput) = await RunGitWithOutputAsync(
            repositoryPath, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        if (symbolicRefSucceeded && symbolicRefOutput.Trim() == branch)
        {
            return;
        }

        errors.Add($"Default branch '{branch}' does not exist in the repository.");
    }

    /// <summary>
    /// Runs git with strictly separated arguments. Every dynamic value is passed through
    /// <see cref="ProcessStartInfo.ArgumentList"/>; nothing is ever concatenated into a shell string.
    /// </summary>
    private static async Task<bool> RunGitAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildGitStartInfo(repositoryPath, arguments);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return false;
        }

        return process.ExitCode == 0;
    }

    /// <summary>
    /// Runs git as in <see cref="RunGitAsync"/> and additionally returns bounded standard
    /// output for callers that need to inspect it (e.g. symbolic HEAD resolution).
    /// </summary>
    private static async Task<(bool Succeeded, string StdOut)> RunGitWithOutputAsync(
        string repositoryPath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = BuildGitStartInfo(repositoryPath, arguments);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return (false, string.Empty);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return (false, string.Empty);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await stdoutTask;
            return (process.ExitCode == 0, stdout.Length <= 4096 ? stdout : stdout[..4096]);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            return (false, string.Empty);
        }
    }

    private static ProcessStartInfo BuildGitStartInfo(string repositoryPath, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(repositoryPath);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private NodeId ResolveNodeId()
    {
        if (options.Value.NodeId is { } configured)
        {
            return new NodeId(configured);
        }

        // Stable per-machine fallback so restarts do not fork node identities.
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("PiCommandCenter:" + Environment.MachineName));
        return new NodeId(new Guid(digest.AsSpan(0, 16)));
    }

    private static ProjectDto ToDto(Project project) => new(
        project.Id.Value,
        project.NodeId.Value,
        project.DisplayName,
        project.RepositoryPath,
        project.DefaultBranch,
        project.Enabled,
        project.MaxActiveWriteRequests,
        project.MaxReadOnlyRequests,
        project.MaxChildAgentsPerRequest,
        project.RequireCleanStart,
        project.CreateRequestBranch,
        project.CreateRequestCommit,
        project.AutoMerge,
        project.CreatedAt,
        project.UpdatedAt,
        project.Version);
}
