namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// One running official <c>muse serve</c> OS process. The adapter owns the MSP JSON-RPC
/// protocol; this type owns only the process boundary so tests can substitute a fake host.
/// </summary>
public interface IMuseProcess : IAsyncDisposable
{
    int Id { get; }

    Stream Stdin { get; }

    Stream Stdout { get; }

    Stream Stderr { get; }

    Task<int> Exited { get; }

    /// <summary>Sends SIGTERM, then kills the process tree if it has not exited.</summary>
    Task TerminateAsync(CancellationToken cancellationToken);
}

/// <summary>Launch request for one Muse host. No shell; argv is explicit.</summary>
public sealed record MuseProcessStartInfo(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Starts official-compatible Muse host processes.</summary>
public interface IMuseProcessFactory
{
    IMuseProcess Start(MuseProcessStartInfo startInfo);
}
