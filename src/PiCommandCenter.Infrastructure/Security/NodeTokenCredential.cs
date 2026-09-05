namespace PiCommandCenter.Infrastructure.Security;

/// <summary>
/// In-memory node token loaded from the private credential file. Never logged or serialized.
/// </summary>
public sealed class NodeTokenCredential
{
    public NodeTokenCredential(byte[] tokenBytes)
    {
        ArgumentNullException.ThrowIfNull(tokenBytes);
        if (tokenBytes.Length != 32)
        {
            throw new InvalidOperationException("Node credential must be a 256-bit (32-byte) token.");
        }

        Bytes = tokenBytes;
    }

    internal byte[] Bytes { get; }

    public bool Matches(ReadOnlySpan<byte> candidate) =>
        candidate.Length == Bytes.Length
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Bytes, candidate);

    public override string ToString() => nameof(NodeTokenCredential);
}
