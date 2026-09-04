using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Fills in machine-derived defaults for <see cref="PiWorkerOptions"/> after binding:
/// resolves the worker path from the repository/content root when not configured and
/// expands '~' in the agent data directory.
/// </summary>
public sealed class PiWorkerOptionsPostConfigure : IPostConfigureOptions<PiWorkerOptions>
{
    public void PostConfigure(string? name, PiWorkerOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.WorkerPath))
        {
            options.WorkerPath = ResolveDefaultWorkerPath();
        }

        options.AgentDataDirectory = NodeOptionsPostConfigure.ExpandPath(options.AgentDataDirectory);
        if (!string.IsNullOrEmpty(options.AgentDataDirectory))
        {
            NodeOptionsPostConfigure.CreatePrivateDirectory(options.AgentDataDirectory);
        }
    }

    /// <summary>
    /// Walks up from the content root (and the entry assembly directory) looking for
    /// <c>runtime/pi-worker/src/index.ts</c>, so the node works both from the repository
    /// root and from a published <c>bin</c> layout inside the repository.
    /// </summary>
    internal static string ResolveDefaultWorkerPath()
    {
        var candidateName = Path.Combine("runtime", "pi-worker", "src", "index.ts");
        var roots = new List<string?> { Directory.GetCurrentDirectory(), AppContext.BaseDirectory };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            var current = Path.GetFullPath(root);
            while (!string.IsNullOrEmpty(current))
            {
                var candidate = Path.Combine(current, candidateName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                var parent = Path.GetDirectoryName(current);
                if (parent == current)
                {
                    break;
                }

                current = parent;
            }
        }

        // Leave empty; the options validator fails fast with a clear message.
        return string.Empty;
    }
}
