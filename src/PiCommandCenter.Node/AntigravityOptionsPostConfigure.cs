using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>Expands <c>~</c> in the Antigravity executable path.</summary>
public sealed class AntigravityOptionsPostConfigure : IPostConfigureOptions<AntigravityOptions>
{
    public void PostConfigure(string? name, AntigravityOptions options)
    {
        options.Executable = NodeOptionsPostConfigure.ExpandPath(options.Executable);
    }
}
