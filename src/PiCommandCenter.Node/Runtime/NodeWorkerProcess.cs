using System.Diagnostics;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Default <see cref="IPiWorkerProcessFactory"/>: spawns <c>&lt;node&gt; &lt;workerPath&gt;</c>
/// with fully redirected stdio, no shell. Cancellation and timeouts terminate the entire
/// process tree. On Linux the worker is launched under util-linux <c>setsid</c> so stop
/// proof can target the new session and process group.
/// </summary>
public sealed class NodeWorkerProcessFactory : IPiWorkerProcessFactory
{
    public IPiWorkerProcess Start(string nodeExecutable, string workerPath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);

        var setsid = AssignmentProcessIsolation.SetsidExecutable;
        var useSetsid = OperatingSystem.IsLinux() && setsid is not null;
        var startInfo = new ProcessStartInfo
        {
            FileName = useSetsid ? setsid! : nodeExecutable,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (useSetsid)
        {
            startInfo.ArgumentList.Add("-w");
            startInfo.ArgumentList.Add(nodeExecutable);
            startInfo.ArgumentList.Add(workerPath);
        }
        else
        {
            startInfo.ArgumentList.Add(workerPath);
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start Pi worker process '{nodeExecutable} {workerPath}'.");

        AssignmentProcessIdentity? identity = null;
        if (useSetsid)
        {
            identity = AssignmentProcessIsolation.WaitForSessionChild(
                process.Id,
                scopeName: "pi-worker",
                TimeSpan.FromSeconds(2));
            if (identity is null)
            {
                AssignmentProcessIsolation.TryReadIdentity(process.Id, "pi-worker", out var waiter);
                identity = waiter;
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            AssignmentProcessIsolation.TryReadIdentity(process.Id, "pi-worker", out identity);
        }

        return new NodeWorkerProcess(process, identity, isolationAvailable: useSetsid);
    }

    private sealed class NodeWorkerProcess : IPiWorkerProcess, IAssignmentProcessIsolation
    {
        private readonly Process _process;
        private readonly bool _isolationAvailable;
        private readonly TaskCompletionSource<int> _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killRequested;
        private AssignmentProcessStopResult? _lastStop;

        public NodeWorkerProcess(
            Process process,
            AssignmentProcessIdentity? identity,
            bool isolationAvailable)
        {
            _process = process;
            Identity = identity;
            _isolationAvailable = isolationAvailable;
            Exited = WaitForExitAsync(_process, _exited);
        }

        public AssignmentProcessIdentity? Identity { get; }

        public Stream Stdin => _process.StandardInput.BaseStream;

        public Stream Stdout => _process.StandardOutput.BaseStream;

        public Stream Stderr => _process.StandardError.BaseStream;

        public Task<int> Exited { get; }

        public async Task KillTreeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _killRequested, 1) == 1)
            {
                await Exited.ConfigureAwait(false);
                return;
            }

            if (_isolationAvailable && Identity is not null)
            {
                _lastStop = await AssignmentProcessIsolation
                    .StopSessionAsync(Identity, cancellationToken)
                    .ConfigureAwait(false);
                try
                {
                    if (!_process.HasExited)
                    {
                        _process.Kill(entireProcessTree: false);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }

                await Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                // Kill the full tree: the worker may own SDK child processes.
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            catch (System.ComponentModel.Win32Exception ex) when (!_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"Failed to kill Pi worker process {_process.Id}.", ex);
            }

            await Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async Task<AssignmentProcessStopResult> StopIsolatedAsync(
            CancellationToken cancellationToken)
        {
            if (!OperatingSystem.IsLinux() || !_isolationAvailable)
            {
                await KillTreeAsync(cancellationToken).ConfigureAwait(false);
                return AssignmentProcessStopResult.Unproven(
                    Identity is null ? [] : [Identity]);
            }

            if (Interlocked.Exchange(ref _killRequested, 1) == 1)
            {
                await Exited.ConfigureAwait(false);
                return _lastStop
                    ?? AssignmentProcessStopResult.Stopped(
                        Identity is null ? [] : [Identity]);
            }

            var result = await AssignmentProcessIsolation
                .StopSessionAsync(Identity, cancellationToken)
                .ConfigureAwait(false);
            _lastStop = result;
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: false);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            try
            {
                await Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return AssignmentProcessStopResult.Unproven(
                    result.DiscoveredProcesses);
            }

            return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _killRequested, 1) == 0 && !_process.HasExited)
            {
                try
                {
                    if (_isolationAvailable && Identity is not null)
                    {
                        _lastStop = await AssignmentProcessIsolation
                            .StopSessionAsync(Identity, CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
            }

            await Exited.ConfigureAwait(false);
            _process.Dispose();
        }

        private static async Task<int> WaitForExitAsync(
            Process process,
            TaskCompletionSource<int> completion)
        {
            process.EnableRaisingEvents = true;
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (_, _) => exited.TrySetResult();

            if (process.HasExited)
            {
                completion.TrySetResult(process.ExitCode);
                return process.ExitCode;
            }

            await exited.Task.ConfigureAwait(false);
            var code = process.ExitCode;
            completion.TrySetResult(code);
            return code;
        }
    }
}
