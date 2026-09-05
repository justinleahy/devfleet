namespace PiCommandCenter.Node.Runtime.Claude;

/// <summary>
/// One running official CLI process. The adapter owns stream parsing; this type owns
/// the OS process boundary so tests can substitute a fake executable.
/// </summary>
public interface IOfficialAgentProcess : IAsyncDisposable
{
    int Id { get; }

    Stream Stdout { get; }

    Stream Stderr { get; }

    Task<int> Exited { get; }

    /// <summary>Sends a POSIX signal (e.g. 2 = SIGINT, 15 = SIGTERM) to the process.</summary>
    Task SignalAsync(int signal, CancellationToken cancellationToken);

    /// <summary>Last-resort SIGKILL of the process tree.</summary>
    Task KillTreeAsync(CancellationToken cancellationToken);
}

/// <summary>Launch request for an official CLI (no shell, redirected stdio).</summary>
public sealed record OfficialProcessStartRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string>? ExtraEnvironment);

/// <summary>Starts official CLI processes without a shell.</summary>
public interface IOfficialAgentProcessFactory
{
    IOfficialAgentProcess Start(OfficialProcessStartRequest request);
}
