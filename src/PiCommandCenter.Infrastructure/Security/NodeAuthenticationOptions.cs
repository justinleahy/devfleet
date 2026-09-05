namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Node-to-control-plane token authentication. Bound from <c>NodeAuthentication</c>.
/// </summary>
public sealed class NodeAuthenticationOptions
{
    public const string SectionName = "NodeAuthentication";

    public const string DefaultHeader = "Authorization";

    public const string DefaultScheme = "Bearer";

    /// <summary>Path to a 0600 file containing the 256-bit node credential (hex).</summary>
    public string CredentialFile { get; set; } = "~/.config/pi-command-center/node.token";

    /// <summary>HTTP header that carries the node credential.</summary>
    public string Header { get; set; } = DefaultHeader;

    /// <summary>Authentication scheme token prefix (e.g. Bearer).</summary>
    public string Scheme { get; set; } = DefaultScheme;
}
