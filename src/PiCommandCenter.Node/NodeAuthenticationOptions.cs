namespace PiCommandCenter.Node;

/// <summary>
/// Node credential loading. Bound from the <c>NodeAuthentication</c> configuration section.
/// </summary>
public sealed class NodeAuthenticationOptions
{
    public const string SectionName = "NodeAuthentication";

    public string CredentialFile { get; set; } = "~/.config/pi-command-center/node.token";

    public string Header { get; set; } = "Authorization";

    public string Scheme { get; set; } = "Bearer";
}
