namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// Canonical <c>muse/&lt;modelId&gt;</c> selectors the local Muse host will accept, or a stable
/// discovery error. <see cref="Models"/> is empty whenever <see cref="Error"/> is set.
/// </summary>
public sealed record MuseModelCatalogResult(IReadOnlyList<string> Models, string? Error)
{
    public static MuseModelCatalogResult Failure(string error) => new([], error);
}

/// <summary>
/// Discovers Muse models through a fresh read-only host: handshake, <c>model/list</c>, terminate.
/// Never starts a session or spends model quota.
/// </summary>
public interface IMuseModelCatalogReader
{
    Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken);
}
