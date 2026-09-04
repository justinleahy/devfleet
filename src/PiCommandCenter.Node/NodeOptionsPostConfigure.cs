using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Fills in machine-derived defaults for <see cref="NodeOptions"/> after binding:
/// expands '~' in the spool path, creates a private data directory, and persists
/// a stable node identifier when one is not configured.
/// </summary>
public sealed class NodeOptionsPostConfigure : IPostConfigureOptions<NodeOptions>
{
    public void PostConfigure(string? name, NodeOptions options)
    {
        options.EventSpoolPath = ExpandPath(options.EventSpoolPath);

        var directory = Path.GetDirectoryName(options.EventSpoolPath);
        if (!string.IsNullOrEmpty(directory))
        {
            CreatePrivateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(options.DisplayName))
        {
            options.DisplayName = Environment.MachineName;
        }

        if (string.IsNullOrWhiteSpace(options.AgentVersion))
        {
            options.AgentVersion = typeof(NodeWorker).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";
        }

        if (options.Id == Guid.Empty)
        {
            options.Id = LoadOrCreateNodeId(directory, options.EventSpoolPath);
        }
    }

    internal static string ExpandPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home))
            {
                home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
            }

            return Path.Combine(home, path.Length == 1 ? string.Empty : path[2..]);
        }

        return path;
    }

    private static void CreatePrivateDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                var root = Path.GetPathRoot(Path.GetFullPath(directory));
                var current = Path.GetFullPath(directory);
                var created = new Stack<string>();
                while (!string.IsNullOrEmpty(current) && current != root && !Directory.Exists(current))
                {
                    created.Push(current);
                    current = Path.GetDirectoryName(current);
                }

                while (created.Count > 0)
                {
                    var dir = created.Pop();
                    Directory.CreateDirectory(dir);

                    // Best-effort: restrict to the owner on Unix.
                    if (!OperatingSystem.IsWindows())
                    {
                        try
                        {
                            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                        }
                        catch (IOException)
                        {
                        }
                    }
                }
            }
        }
        catch (IOException)
        {
            // Best-effort; the spool will surface a concrete failure when opened.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Guid LoadOrCreateNodeId(string? directory, string spoolPath)
    {
        try
        {
            if (string.IsNullOrEmpty(directory))
            {
                return Guid.NewGuid();
            }

            var idFile = Path.Combine(directory, "node-id");
            if (File.Exists(idFile)
                && Guid.TryParse(File.ReadAllText(idFile).Trim(), out var existing))
            {
                return existing;
            }

            var id = Guid.NewGuid();
            File.WriteAllText(idFile, id.ToString("D"));

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(idFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
                catch (IOException)
                {
                }
            }

            return id;
        }
        catch (IOException)
        {
            // Cannot persist; use an ephemeral identity rather than crash at configure time.
            return Guid.NewGuid();
        }
        catch (UnauthorizedAccessException)
        {
            return Guid.NewGuid();
        }
    }
}
