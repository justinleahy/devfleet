namespace PiCommandCenter.Node.Runtime.Antigravity;

/// <summary>
/// One running official <c>agy</c> OS process. The adapter owns the stream-json protocol;
/// this type owns only the process boundary so tests can substitute a fake executable.
/// </summary>
public interface IAntigravityProcess : IAsyncDisposable
{
    int Id { get; }

    Stream Stdin { get; }

    Stream Stdout { get; }

    Stream Stderr { get; }

    Task<int> Exited { get; }

    /// <summary>Sends SIGINT to the process (documented cancel surface).</summary>
    Task InterruptAsync(CancellationToken cancellationToken);

    /// <summary>Sends SIGTERM, then kills the process tree if it has not exited.</summary>
    Task TerminateAsync(CancellationToken cancellationToken);
}

/// <summary>Launch request for one Antigravity process. No shell; argv is explicit.</summary>
public sealed record AntigravityProcessStartInfo(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>Starts official-compatible Antigravity processes.</summary>
public interface IAntigravityProcessFactory
{
    IAntigravityProcess Start(AntigravityProcessStartInfo startInfo);
}
