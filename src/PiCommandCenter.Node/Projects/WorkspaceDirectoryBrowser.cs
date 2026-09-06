using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.Projects;

/// <summary>
/// Default <see cref="IWorkspaceDirectoryBrowser"/>. Canonicalizes paths the same way
/// <see cref="WorkspaceBindingValidator"/> does (including <c>~</c> expansion in approved
/// roots), stays inside approved roots, and omits symbolic links.
/// </summary>
public sealed class WorkspaceDirectoryBrowser : IWorkspaceDirectoryBrowser
{
    /// <summary>Maximum number of directory entries returned per browse.</summary>
    public const int MaxEntries = 500;

    private const int MaxDetailCharacters = 512;

    private readonly WorkspaceValidationOptions _options;

    public WorkspaceDirectoryBrowser(IOptions<WorkspaceValidationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    public Task<WorkspaceDirectoryBrowseResponseMessage> BrowseAsync(
        WorkspaceDirectoryBrowseRequestMessage request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Path is null)
        {
            return Task.FromResult(ListApprovedRoots(cancellationToken));
        }

        return Task.FromResult(ListDirectory(request.Path, cancellationToken));
    }

    private WorkspaceDirectoryBrowseResponseMessage ListApprovedRoots(CancellationToken cancellationToken)
    {
        var entries = new List<WorkspaceDirectoryEntryMessage>();
        var seen = new HashSet<string>(PathComparer);
        foreach (var root in ExpandApprovedRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entries.Count >= MaxEntries)
            {
                break;
            }

            if (!seen.Add(root)
                || InspectSymbolicLinks(root) is not SymlinkInspection.Clear
                || !IsExistingReadablePlainDirectory(root))
            {
                continue;
            }

            entries.Add(new WorkspaceDirectoryEntryMessage(Path.GetFileName(root) is { Length: > 0 } name ? name : root, root));
        }

        entries.Sort(EntryComparer);
        return new WorkspaceDirectoryBrowseResponseMessage(
            CurrentPath: null,
            ParentPath: null,
            entries,
            ErrorCode: null,
            ErrorDetail: null);
    }

    private WorkspaceDirectoryBrowseResponseMessage ListDirectory(string requestedPath, CancellationToken cancellationToken)
    {
        if (requestedPath.Length == 0
            || !Path.IsPathRooted(requestedPath)
            || requestedPath.Any(char.IsControl))
        {
            return Error(WorkspaceDirectoryBrowseErrorCodes.InvalidPath, "Path must be a safe absolute path.");
        }

        string canonicalPath;
        try
        {
            canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requestedPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Error(WorkspaceDirectoryBrowseErrorCodes.InvalidPath, "Path is not a valid absolute filesystem path.");
        }

        var approvedRoots = ExpandApprovedRoots();
        var matchingRoot = FindApprovedRoot(approvedRoots, canonicalPath);
        if (matchingRoot is null)
        {
            return Error(
                WorkspaceDirectoryBrowseErrorCodes.OutsideApprovedRoot,
                "Path is outside every approved root on this node.");
        }

        switch (InspectSymbolicLinks(canonicalPath))
        {
            case SymlinkInspection.SymbolicLink:
                return Error(
                    WorkspaceDirectoryBrowseErrorCodes.OutsideApprovedRoot,
                    "Path contains a symbolic link; browse its canonical path instead.");
            case SymlinkInspection.Missing:
                return Error(WorkspaceDirectoryBrowseErrorCodes.PathMissing, "Path is not an existing directory.");
            case SymlinkInspection.Unreadable:
                return Error(WorkspaceDirectoryBrowseErrorCodes.Unreadable, "Directory is not readable.");
        }

        try
        {
            var attributes = File.GetAttributes(canonicalPath);
            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                return Error(WorkspaceDirectoryBrowseErrorCodes.PathMissing, "Path is not an existing directory.");
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Error(WorkspaceDirectoryBrowseErrorCodes.PathMissing, "Path is not an existing directory.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
        {
            return Error(WorkspaceDirectoryBrowseErrorCodes.Unreadable, "Directory is not readable.");
        }

        var entries = new List<WorkspaceDirectoryEntryMessage>();
        try
        {
            foreach (var child in Directory.EnumerateDirectories(canonicalPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entries.Count >= MaxEntries)
                {
                    break;
                }

                if (!IsExistingReadablePlainDirectory(child))
                {
                    continue;
                }

                entries.Add(new WorkspaceDirectoryEntryMessage(
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(child)),
                    Path.TrimEndingDirectorySeparator(child)));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException or PathTooLongException)
        {
            return Error(WorkspaceDirectoryBrowseErrorCodes.Unreadable, "Directory is not readable.");
        }

        entries.Sort(EntryComparer);
        var parent = string.Equals(canonicalPath, matchingRoot, PathComparison)
            ? null
            : Path.GetDirectoryName(canonicalPath);
        return new WorkspaceDirectoryBrowseResponseMessage(
            canonicalPath,
            parent,
            entries,
            ErrorCode: null,
            ErrorDetail: null);
    }

    private IEnumerable<string> ExpandApprovedRoots()
    {
        foreach (var configuredRoot in _options.ApprovedRoots)
        {
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                continue;
            }

            string expanded;
            try
            {
                expanded = ExpandApprovedRoot(configuredRoot);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
            {
                continue;
            }

            yield return expanded;
        }
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

    private static string? FindApprovedRoot(IEnumerable<string> approvedRoots, string canonicalPath)
    {
        foreach (var approvedRoot in approvedRoots)
        {
            var relative = Path.GetRelativePath(approvedRoot, canonicalPath);
            if (relative == "."
                || (!Path.IsPathRooted(relative)
                    && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, PathComparison)))
            {
                return approvedRoot;
            }
        }

        return null;
    }

    private static bool IsExistingReadablePlainDirectory(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return false;
            }

            _ = Directory.EnumerateFileSystemEntries(path).Any();
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
            or IOException
            or FileNotFoundException
            or DirectoryNotFoundException
            or PathTooLongException)
        {
            return false;
        }
    }

    private enum SymlinkInspection
    {
        Clear,
        SymbolicLink,
        Missing,
        Unreadable,
    }

    private static SymlinkInspection InspectSymbolicLinks(string path)
    {
        var root = Path.GetPathRoot(path);
        if (root is null)
        {
            return SymlinkInspection.Clear;
        }

        var current = root;
        var relative = Path.GetRelativePath(root, path);
        foreach (var segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (new DirectoryInfo(current).LinkTarget is not null)
                {
                    return SymlinkInspection.SymbolicLink;
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return SymlinkInspection.Missing;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PathTooLongException)
            {
                return SymlinkInspection.Unreadable;
            }
        }

        return SymlinkInspection.Clear;
    }

    private static WorkspaceDirectoryBrowseResponseMessage Error(string code, string detail) => new(
        CurrentPath: null,
        ParentPath: null,
        [],
        code,
        BoundDetail(detail));

    private static string BoundDetail(string detail) => detail.Length <= MaxDetailCharacters
        ? detail
        : detail[..MaxDetailCharacters];

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static Comparison<WorkspaceDirectoryEntryMessage> EntryComparer { get; } =
        static (left, right) =>
        {
            var byName = string.Compare(left.Name, right.Name, PathComparison);
            return byName != 0 ? byName : string.Compare(left.Path, right.Path, PathComparison);
        };
}
