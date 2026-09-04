using System.Diagnostics;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Default <see cref="IPiWorkerProcessFactory"/>: spawns <c>&lt;node&gt; &lt;workerPath&gt;</c>
/// with fully redirected stdio, no shell. Cancellation and timeouts terminate the entire
/// process tree.
/// </summary>
public sealed class NodeWorkerProcessFactory : IPiWorkerProcessFactory
{
    public IPiWorkerProcess Start(string nodeExecutable, string workerPath, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);

        var startInfo = new ProcessStartInfo
        {
            FileName = nodeExecutable,
            ArgumentList = { workerPath },
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start Pi worker process '{nodeExecutable} {workerPath}'.");
        return new NodeWorkerProcess(process);
    }

    private sealed class NodeWorkerProcess : IPiWorkerProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killRequested;

        public NodeWorkerProcess(Process process)
        {
            _process = process;
            Exited = WaitForExitAsync(_process, _exited);
        }

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

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _killRequested, 1) == 0 && !_process.HasExited)
            {
                try
                {
                    _process.Kill(entireProcessTree: true);
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
