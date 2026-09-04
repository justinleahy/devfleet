namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// One running Pi worker OS process. The adapter owns the protocol; this abstraction owns only
/// the process boundary so tests can substitute a fake executable.
/// </summary>
public interface IPiWorkerProcess : IAsyncDisposable
{
    /// <summary>Protocol stdin; write strict NDJSON frames only.</summary>
    Stream Stdin { get; }

    /// <summary>Protocol stdout; the only trusted source of protocol frames.</summary>
    Stream Stdout { get; }

    /// <summary>Diagnostics stream; never protocol output.</summary>
    Stream Stderr { get; }

    /// <summary>Completes when the process exits; yields the exit code.</summary>
    Task<int> Exited { get; }

    /// <summary>Terminates the whole process tree (children included) and completes <see cref="Exited"/>.</summary>
    Task KillTreeAsync(CancellationToken cancellationToken);
}

/// <summary>Launches worker processes. The executable path is injected for testability.</summary>
public interface IPiWorkerProcessFactory
{
    IPiWorkerProcess Start(string nodeExecutable, string workerPath, string workingDirectory);
}
