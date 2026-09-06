namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// Per-node control-plane authentication. Bound from <c>NodeAuthentication</c>.
/// </summary>
public sealed class NodeAuthenticationOptions
{
    public const string SectionName = "NodeAuthentication";

    public const string DefaultHeader = "Authorization";

    public const string DefaultScheme = "Bearer";

    /// <summary>Owner-only directory containing one credential file per node.</summary>
    public string CredentialDirectory { get; set; } = "~/.config/pi-command-center/node-credentials";

    /// <summary>HTTP header that carries the node credential.</summary>
    public string Header { get; set; } = DefaultHeader;

    /// <summary>Authentication scheme token prefix (e.g. Bearer).</summary>
    public string Scheme { get; set; } = DefaultScheme;
}
