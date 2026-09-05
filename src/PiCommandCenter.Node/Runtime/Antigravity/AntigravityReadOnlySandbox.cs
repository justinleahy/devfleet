using System.Diagnostics;

namespace PiCommandCenter.Node.Runtime.Antigravity;

/// <summary>
/// Trusted OS boundary that makes the complete host filesystem read-only for Antigravity.
/// The repository overlay is explicit for auditability; escaping workspace symlinks still land
/// on the read-only root mount. Provider-native authentication remains readable but immutable.
public static class AntigravityReadOnlySandbox
{
    public const string UnavailableMessage =
        "BLOCKED — Antigravity read-only filesystem boundary unavailable. "
        + "Install bubblewrap (`bwrap`) so the repository can be bind-mounted read-only. "
        + "agy defaults to auto-allowing workspace writes.";

    public static string? FindBwrap()
    {
        var overridePath = Environment.GetEnvironmentVariable("PI_CC_BWRAP");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return File.Exists(overridePath) ? Path.GetFullPath(overridePath) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, "bwrap");
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return File.Exists("/usr/bin/bwrap") ? "/usr/bin/bwrap" : null;
    }

    /// <summary>
    /// Rewrites <paramref name="psi"/> to execute through <c>bwrap</c> with the repository
    /// read-only repository and host-root mounts. Throws an actionable
    /// <see cref="InvalidOperationException"/> when the boundary cannot be established.
    /// </summary>
    public static void Apply(ProcessStartInfo psi, string workingDirectory, string? bwrapPath = null)
    {
        ArgumentNullException.ThrowIfNull(psi);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var repo = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(repo))
        {
            throw new InvalidOperationException(
                $"BLOCKED — Antigravity working directory '{repo}' does not exist.");
        }

        var bwrap = !string.IsNullOrWhiteSpace(bwrapPath)
            ? (File.Exists(bwrapPath) ? Path.GetFullPath(bwrapPath) : null)
            : FindBwrap();
        if (string.IsNullOrEmpty(bwrap))
        {
            throw new InvalidOperationException(UnavailableMessage);
        }

        var originalFile = psi.FileName;
        if (string.IsNullOrWhiteSpace(originalFile))
        {
            throw new InvalidOperationException(UnavailableMessage);
        }

        var originalArgs = psi.ArgumentList.ToArray();
        psi.FileName = bwrap;
        psi.ArgumentList.Clear();
        foreach (var argument in Prefix(repo, originalFile))
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var argument in originalArgs)
        {
            psi.ArgumentList.Add(argument);
        }
    }

    private static IEnumerable<string> Prefix(string repo, string executable)
    {
        yield return "--die-with-parent";
        yield return "--new-session";
        yield return "--unshare-pid";
        yield return "--ro-bind";
        yield return "/";
        yield return "/";
        yield return "--dev";
        yield return "/dev";
        yield return "--proc";
        yield return "/proc";
        yield return "--ro-bind";
        yield return repo;
        yield return repo;
        yield return "--chdir";
        yield return repo;
        yield return "--";
        yield return executable;
    }
}
