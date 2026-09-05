namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Owner-only file and directory helpers (0600 files, 0700 directories on Unix).
/// </summary>
public static class PrivateFileAccess
{
    public static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return path == "~" ? home : Path.Combine(home, path[2..]);
        }

        return path;
    }

    public static void CreatePrivateDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);
        RestrictDirectory(directory);
    }

    public static void WritePrivateFile(string path, string contents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contents);

        var fullPath = Path.GetFullPath(ExpandPath(path));
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            CreatePrivateDirectory(directory);
        }

        File.WriteAllText(fullPath, contents);
        RestrictFile(fullPath);
    }

    public static string ReadPrivateFile(string path, string missingMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(ExpandPath(path));
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException(missingMessage);
        }

        EnsureOwnerOnlyFile(fullPath);
        return File.ReadAllText(fullPath).Trim();
    }

    public static void RestrictFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RestrictDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static void EnsureOwnerOnlyFile(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var mode = File.GetUnixFileMode(path);
        if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                     | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
        {
            throw new InvalidOperationException(
                $"Credential file '{path}' must be owner-only (mode 0600). Group or other permissions are set.");
        }
    }
}
