namespace PiCommandCenter.Node;

/// <summary>Loaded node token. The value is never logged.</summary>
public sealed class NodeCredential
{
    public NodeCredential(string tokenHex, string header, string scheme)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenHex);
        TokenHex = tokenHex;
        Header = string.IsNullOrWhiteSpace(header) ? "Authorization" : header;
        Scheme = string.IsNullOrWhiteSpace(scheme) ? "Bearer" : scheme;
    }

    public string TokenHex { get; }

    public string Header { get; }

    public string Scheme { get; }

    public override string ToString() => nameof(NodeCredential);
}
