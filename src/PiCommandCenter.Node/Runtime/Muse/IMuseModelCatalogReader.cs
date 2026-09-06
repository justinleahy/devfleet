namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// Muse discovery selectors and the subset returned by the native host, or a stable discovery
/// error. <see cref="Models"/> includes curated discovery aliases. <see cref="NativeModels"/>
/// contains only concrete model ids from the successful native <c>model/list</c> response and can
/// therefore be used as readiness evidence. Both lists are empty when <see cref="Error"/> is set.
/// </summary>
public sealed record MuseModelCatalogResult(
    IReadOnlyList<string> Models,
    IReadOnlyList<string> NativeModels,
    string? Error)
{
    public static MuseModelCatalogResult Failure(string error) => new([], [], error);
}

/// <summary>
/// Discovers Muse models through a fresh read-only host: handshake, <c>model/list</c>, terminate.
/// Never starts a session or spends model quota.
/// </summary>
public interface IMuseModelCatalogReader
{
    Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken);
}
