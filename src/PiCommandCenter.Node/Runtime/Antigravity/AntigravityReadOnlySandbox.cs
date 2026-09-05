using System.Diagnostics;

namespace PiCommandCenter.Node.Runtime.Antigravity;

/// <summary>
/// Trusted OS boundary that makes the host filesystem and repository read-only while keeping
/// Antigravity's own state directory writable. Cross-provider credential stores are masked with
/// private empty tmpfs mounts so a model-driven process can neither read nor exfiltrate them.
/// </summary>
public static class AntigravityReadOnlySandbox
{
    public const string UnavailableMessage =
        "BLOCKED — Antigravity read-only filesystem boundary unavailable. "
        + "Install bubblewrap (`bwrap`) so the repository can be bind-mounted read-only. "
        + "agy defaults to auto-allowing workspace writes.";

    /// <summary>Antigravity's host-native credential, cache, and log directory.</summary>
    public static readonly string StateLocation = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".gemini");

    /// <summary>
    /// Host locations that hold other providers' OAuth stores. Each existing directory is
    /// replaced by an empty private tmpfs inside every Antigravity process. Order-sensitive:
    /// the masks are the last mounts in the bwrap argv so no earlier bind is re-exposed.
    /// </summary>
    public static readonly IReadOnlyList<string> MaskedSecretLocations =
    [
        "/provider-auth",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "muse"),
    ];

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
    /// Rewrites <paramref name="psi"/> to execute through <c>bwrap</c> with the read-only host
    /// root and repository, writable Antigravity state, and masked sibling credentials.
    /// </summary>
    /// <param name="maskedLocations">Overrides sibling credential masks for tests.</param>
    /// <param name="writableStateLocation">Overrides Antigravity's writable state path for tests.</param>
    public static void Apply(
        ProcessStartInfo psi,
        string workingDirectory,
        string? bwrapPath = null,
        IReadOnlyList<string>? maskedLocations = null,
        string? writableStateLocation = null)
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

        var state = ResolveWritableState(repo, writableStateLocation ?? StateLocation);
        var masks = ResolveMasks(repo, maskedLocations ?? MaskedSecretLocations);

        var originalArgs = psi.ArgumentList.ToArray();
        psi.FileName = bwrap;
        psi.ArgumentList.Clear();
        foreach (var argument in Prefix(repo, state, masks, originalFile))
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var argument in originalArgs)
        {
            psi.ArgumentList.Add(argument);
        }
    }

    private static string? ResolveWritableState(string repo, string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        var state = Path.GetFullPath(location);
        if (Directory.Exists(state))
        {
            // Bind mounts follow symlinks on the host, so containment must be decided on the
            // real filesystem targets: a symlinked ancestor in either path could smuggle the
            // writable bind inside the read-only repository. Unresolvable aliases fail closed.
            var realRepo = ResolveFilesystemTarget(repo);
            var realState = ResolveFilesystemTarget(state);
            if (realRepo is null || realState is null)
            {
                throw new InvalidOperationException(
                    $"BLOCKED — Antigravity cannot resolve the real filesystem targets of '{state}' and '{repo}' (dangling or ambiguous symlinks); refusing the writable bind.");
            }

            if (IsWithin(realRepo, realState) || IsWithin(realState, realRepo))
            {
                throw new InvalidOperationException(
                    $"BLOCKED — Antigravity state location '{state}' overlaps working directory '{repo}'.");
            }

            return state;
        }

        if (Path.Exists(state))
        {
            throw new InvalidOperationException(
                $"BLOCKED — Antigravity state location '{state}' is not a directory.");
        }

        return null;
    }

    /// <summary>
    /// Returns <paramref name="path"/> with every existing component — including symlinked
    /// ancestors — resolved to its final target, or <c>null</c> when any component is missing
    /// or a link cannot be fully resolved.
    /// </summary>
    private static string? ResolveFilesystemTarget(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var current = root;
        foreach (var component in full.Substring(root.Length).Split(
            Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(current, component);
            FileSystemInfo? info = Directory.Exists(candidate)
                ? new DirectoryInfo(candidate)
                : File.Exists(candidate)
                    ? new FileInfo(candidate)
                    : null;
            if (info is null)
            {
                return null;
            }

            if (info.LinkTarget is not null)
            {
                var resolved = info.ResolveLinkTarget(returnFinalTarget: true);
                if (resolved is null || !resolved.Exists)
                {
                    return null;
                }

                current = resolved.FullName;
            }
            else
            {
                current = candidate;
            }
        }

        return current;
    }

    /// <summary>
    /// Returns the secret locations that must be masked. A location absent from the host holds
    /// nothing to hide, and bwrap cannot create a mount point beneath the read-only root, so it
    /// is skipped. A location that exists but is not a directory cannot take a tmpfs, and a
    /// repository inside a masked location would be hidden by its own mask; both refuse to launch.
    /// </summary>
    private static List<string> ResolveMasks(string repo, IReadOnlyList<string> locations)
    {
        var masks = new List<string>(locations.Count);
        foreach (var location in locations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(location);
            var mask = Path.GetFullPath(location);
            if (Directory.Exists(mask))
            {
                if (IsWithin(repo, mask))
                {
                    throw new InvalidOperationException(
                        $"BLOCKED — Antigravity working directory '{repo}' lies inside the masked credential location '{mask}'.");
                }

                masks.Add(mask);
            }
            else if (Path.Exists(mask))
            {
                throw new InvalidOperationException(
                    $"BLOCKED — Antigravity credential mask '{mask}' is not a directory; refusing to launch with the secret store exposed.");
            }
        }

        return masks;
    }

    private static bool IsWithin(string path, string root) =>
        string.Equals(path, root, StringComparison.Ordinal)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    // bwrap applies mounts in argv order and later mounts shadow earlier ones on the same path,
    // so the masks follow every bind and precede only --chdir and the command.
    private static IEnumerable<string> Prefix(
        string repo,
        string? state,
        IReadOnlyList<string> masks,
        string executable)
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
        if (state is not null)
        {
            yield return "--bind";
            yield return state;
            yield return state;
        }

        foreach (var mask in masks)
        {
            yield return "--tmpfs";
            yield return mask;
        }

        yield return "--chdir";
        yield return repo;
        yield return "--";
        yield return executable;
    }
}
