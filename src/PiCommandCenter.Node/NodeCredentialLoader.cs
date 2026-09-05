using Microsoft.Extensions.Options;

namespace PiCommandCenter.Node;

/// <summary>
/// Reads the 0600 node credential file and fails fast when it is missing or world-accessible.
/// </summary>
public sealed class NodeCredentialLoader(IOptions<NodeAuthenticationOptions> options)
{
    public NodeCredential Load()
    {
        var configured = options.Value;
        if (string.IsNullOrWhiteSpace(configured.CredentialFile))
        {
            throw new InvalidOperationException(
                "NodeAuthentication:CredentialFile must be configured. Run control-plane --setup to generate the token file.");
        }

        var path = Path.GetFullPath(NodeOptionsPostConfigure.ExpandPath(configured.CredentialFile));
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"Node credential file is missing at '{path}'. Run the control plane with --setup (or scripts/setup-local.sh). The node does not invent a token.");
        }

        EnsureOwnerOnly(path);
        var hex = File.ReadAllText(path).Trim();
        if (hex.Length != 64)
        {
            throw new InvalidOperationException("Node credential file must contain a 256-bit token as 64 hex characters.");
        }

        try
        {
            _ = Convert.FromHexString(hex);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Node credential file is not valid hexadecimal.", ex);
        }

        return new NodeCredential(hex, configured.Header, configured.Scheme);
    }

    internal static void EnsureOwnerOnly(string path)
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
                $"Node credential file '{path}' must be mode 0600 (owner read/write only).");
        }
    }
}
