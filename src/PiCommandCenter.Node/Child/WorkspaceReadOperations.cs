using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace PiCommandCenter.Node.Child;

/// <summary>
/// Read-only workspace queries for Pi root/child custom tools. Every path, including
/// symlink targets, must stay inside the registered repository.
/// </summary>
public static class WorkspaceReadOperations
{
    public const int MaxMatches = 256;
    public const int MaxFileBytes = 256 * 1024;

    public static FileOperationResult Read(string repositoryRoot, string? relativePath)
    {
        try
        {
            var path = RepositoryPathPolicy.Resolve(repositoryRoot, relativePath ?? "");
            if (!File.Exists(path))
            {
                return FileOperationResult.Failure("not_found", "The file does not exist.");
            }

            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                return FileOperationResult.Failure("too_large", "The file exceeds the read bound.");
            }

            return new ReadResult(File.ReadAllText(path));
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }
    }

    public static FileOperationResult List(string repositoryRoot, string? relativePath)
    {
        try
        {
            var path = RepositoryPathPolicy.ResolveOrRoot(repositoryRoot, relativePath);
            if (!Directory.Exists(path))
            {
                return FileOperationResult.Failure("not_found", "The directory does not exist.");
            }

            var root = Path.GetFullPath(repositoryRoot);
            var names = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (!IsInside(root, entry))
                {
                    continue;
                }

                names.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
                if (names.Count >= MaxMatches)
                {
                    break;
                }
            }

            return new ReadResult(string.Join("\n", names));
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }
    }

    public static FileOperationResult Find(string repositoryRoot, string? relativePath, string? pattern)
    {
        try
        {
            var start = RepositoryPathPolicy.ResolveOrRoot(repositoryRoot, relativePath);
            var root = Path.GetFullPath(repositoryRoot);
            var glob = string.IsNullOrWhiteSpace(pattern) ? "*" : pattern.Trim();
            var matches = new List<string>();
            foreach (var file in EnumerateInside(root, start))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var name = Path.GetFileName(file);
                if (FileSystemName.MatchesSimpleExpression(glob, name)
                    || FileSystemName.MatchesSimpleExpression(glob, relative))
                {
                    matches.Add(relative);
                    if (matches.Count >= MaxMatches)
                    {
                        break;
                    }
                }
            }

            return new ReadResult(string.Join("\n", matches));
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }
    }

    public static FileOperationResult Grep(string repositoryRoot, string? relativePath, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return FileOperationResult.Failure("pattern_required", "A grep pattern is required.");
        }

        Regex regex;
        try
        {
            regex = new Regex(pattern, RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));
        }
        catch (ArgumentException ex)
        {
            return FileOperationResult.Failure("invalid_pattern", ex.Message);
        }

        try
        {
            var start = RepositoryPathPolicy.ResolveOrRoot(repositoryRoot, relativePath);
            var root = Path.GetFullPath(repositoryRoot);
            var hits = new List<string>();
            IEnumerable<string> files;
            if (File.Exists(start))
            {
                files = [start];
            }
            else
            {
                files = EnumerateInside(root, start);
            }

            foreach (var file in files)
            {
                string text;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > MaxFileBytes)
                    {
                        continue;
                    }

                    text = File.ReadAllText(file);
                }
                catch (IOException)
                {
                    continue;
                }

                if (!regex.IsMatch(text))
                {
                    continue;
                }

                hits.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
                if (hits.Count >= MaxMatches)
                {
                    break;
                }
            }

            return new ReadResult(string.Join("\n", hits));
        }
        catch (RepositoryPathPolicyException ex)
        {
            return FileOperationResult.Failure(ex.Code, ex.Message);
        }
        catch (RegexMatchTimeoutException)
        {
            return FileOperationResult.Failure("pattern_timeout", "The grep pattern timed out.");
        }
    }

    private static IEnumerable<string> EnumerateInside(string root, string start)
    {
        if (!Directory.Exists(start) || !IsInside(root, start))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (!IsInside(root, entry))
                {
                    continue;
                }

                if (Directory.Exists(entry) && !File.Exists(entry))
                {
                    pending.Push(entry);
                }
                else if (File.Exists(entry))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        try
        {
            var full = Path.GetFullPath(candidate);
            var link = File.Exists(full)
                ? File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName
                : Directory.Exists(full)
                    ? Directory.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName
                    : null;
            var check = link is null ? full : Path.GetFullPath(link);
            return check.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || string.Equals(check, root, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }
}
