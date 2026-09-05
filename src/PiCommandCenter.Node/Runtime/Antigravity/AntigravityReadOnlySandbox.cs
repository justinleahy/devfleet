using System.Diagnostics;

namespace PiCommandCenter.Node.Runtime.Antigravity;

/// <summary>
/// Trusted OS boundary that makes the complete host filesystem read-only for Antigravity.
/// The repository overlay is explicit for auditability; escaping workspace symlinks still land
/// on the read-only root mount. Cross-provider credential stores (the Pi OAuth mount and the
/// Claude home) are masked with private empty tmpfs mounts so a model-driven process can neither
/// read nor exfiltrate them. Antigravity's own <c>~/.gemini</c> file store stays readable.
/// </summary>
public static class AntigravityReadOnlySandbox
{
    public const string UnavailableMessage =
        "BLOCKED — Antigravity read-only filesystem boundary unavailable. "
        + "Install bubblewrap (`bwrap`) so the repository can be bind-mounted read-only. "
        + "agy defaults to auto-allowing workspace writes.";

    /// <summary>
    /// Host locations that hold other providers' OAuth stores. Each existing directory is
    /// replaced by an empty private tmpfs inside every Antigravity process. Order-sensitive:
    /// the masks are the last mounts in the bwrap argv so no earlier bind is re-exposed.
    /// </summary>
    public static readonly IReadOnlyList<string> MaskedSecretLocations =
        ["/provider-auth", "/home/node/.claude"];

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
    /// Rewrites <paramref name="psi"/> to execute through <c>bwrap</c> with the read-only
    /// host-root and repository mounts followed by the credential masks. Throws an actionable
    /// <see cref="InvalidOperationException"/> when the boundary or a mask cannot be established.
    /// </summary>
    /// <param name="maskedLocations">
    /// Overrides <see cref="MaskedSecretLocations"/>; intended for tests that cannot create the
    /// production paths on the host.
    /// </param>
    public static void Apply(
        ProcessStartInfo psi,
        string workingDirectory,
        string? bwrapPath = null,
        IReadOnlyList<string>? maskedLocations = null)
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

        var masks = ResolveMasks(repo, maskedLocations ?? MaskedSecretLocations);

        var originalArgs = psi.ArgumentList.ToArray();
        psi.FileName = bwrap;
        psi.ArgumentList.Clear();
        foreach (var argument in Prefix(repo, masks, originalFile))
        {
            psi.ArgumentList.Add(argument);
        }

        foreach (var argument in originalArgs)
        {
            psi.ArgumentList.Add(argument);
        }
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
    private static IEnumerable<string> Prefix(string repo, IReadOnlyList<string> masks, string executable)
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
