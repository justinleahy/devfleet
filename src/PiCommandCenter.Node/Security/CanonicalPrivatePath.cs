namespace PiCommandCenter.Node.Security;

/// <summary>
/// Host-owned configuration and hook directories must stay outside the agent-writable
/// repository (SPEC §34.3).
/// </summary>
public static class CanonicalPrivatePath
{
    public static bool IsOutsideRepository(string hostPath, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        var host = Path.GetFullPath(hostPath);
        var root = Path.GetFullPath(repositoryRoot);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
        {
            root += Path.DirectorySeparatorChar;
        }

        return !host.StartsWith(root, StringComparison.Ordinal)
               && !string.Equals(Path.GetFullPath(hostPath), Path.GetFullPath(repositoryRoot), StringComparison.Ordinal);
    }

    public static void EnsureOutsideRepository(string hostPath, string repositoryRoot)
    {
        if (!IsOutsideRepository(hostPath, repositoryRoot))
        {
            throw new InvalidOperationException(
                $"Host-owned path '{hostPath}' must remain outside repository '{repositoryRoot}'.");
        }
    }

    public static bool IsOwnerPrivateDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
#pragma warning disable CA1416
            var mode = File.GetUnixFileMode(directory);
            var others = UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute
                         | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
            return (mode & others) == 0;
#pragma warning restore CA1416
        }

        return true;
    }
}
