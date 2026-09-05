using System.Diagnostics;
using PiCommandCenter.Node.Runtime.Antigravity;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// OS boundary for repository-controlled verification commands. The host root is read-only,
/// user homes and runtime sockets are hidden, networking and the host process table are
/// unavailable, and only the canonical repository plus an isolated temporary home are writable.
/// </summary>
internal static class VerificationProcessSandbox
{
    private static readonly string[] HiddenRoots = ["/home", "/root", "/run/user", "/tmp"];

    public const string UnavailableMessage =
        "BLOCKED — verification sandbox unavailable. Install bubblewrap (`bwrap`); "
        + "repository-controlled verification never runs directly as the node user.";

    public static void Apply(
        ProcessStartInfo startInfo,
        string repositoryRoot,
        string sandboxHome,
        string? bwrapPath = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        var repository = Path.GetFullPath(repositoryRoot);
        var workingDirectory = Path.GetFullPath(startInfo.WorkingDirectory);
        if (!Directory.Exists(repository)
            || (!string.Equals(workingDirectory, repository, StringComparison.Ordinal)
                && !workingDirectory.StartsWith(repository + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Verification working directory must be inside the canonical repository.");
        }

        var bwrap = !string.IsNullOrWhiteSpace(bwrapPath)
            ? (File.Exists(bwrapPath) ? Path.GetFullPath(bwrapPath) : null)
            : AntigravityReadOnlySandbox.FindBwrap();
        if (string.IsNullOrEmpty(bwrap))
        {
            throw new InvalidOperationException(UnavailableMessage);
        }

        var executable = startInfo.FileName;
        var arguments = startInfo.ArgumentList.ToArray();
        startInfo.FileName = bwrap;
        startInfo.ArgumentList.Clear();

        Add(startInfo, "--die-with-parent", "--new-session", "--unshare-net", "--unshare-pid");
        Add(startInfo, "--ro-bind", "/", "/");
        Add(startInfo, "--proc", "/proc", "--dev", "/dev");
        Add(startInfo, "--tmpfs", "/home", "--tmpfs", "/root", "--tmpfs", "/run/user", "--tmpfs", "/tmp");
        CreateHiddenMountParents(startInfo, repository);
        CreateHiddenMountParents(startInfo, sandboxHome);
        Add(startInfo, "--bind", repository, repository);
        Add(startInfo, "--bind", sandboxHome, sandboxHome);
        Add(startInfo, "--chdir", workingDirectory, "--", executable);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }

    private static void CreateHiddenMountParents(ProcessStartInfo startInfo, string path)
    {
        var fullPath = Path.GetFullPath(path);
        var hiddenRoot = HiddenRoots.FirstOrDefault(root =>
            string.Equals(fullPath, root, StringComparison.Ordinal)
                || fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (hiddenRoot is null)
        {
            return;
        }

        var current = hiddenRoot;
        foreach (var segment in fullPath[(hiddenRoot.Length + 1)..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Add(startInfo, "--dir", current);
        }
    }

    private static void Add(ProcessStartInfo startInfo, params string[] arguments)
    {
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
    }
}
