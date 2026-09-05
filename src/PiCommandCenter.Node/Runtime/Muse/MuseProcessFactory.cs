using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// Spawns the official <c>muse</c> executable with fully redirected stdio and no shell.
/// Termination is SIGTERM then process-tree kill; never inspects provider credentials.
/// </summary>
public sealed class MuseProcessFactory : IMuseProcessFactory
{
    private const int SigTerm = 15;

    public IMuseProcess Start(MuseProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.Executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(startInfo.WorkingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = startInfo.Executable,
            WorkingDirectory = startInfo.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in startInfo.Arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        if (startInfo.Environment is not null)
        {
            foreach (var pair in startInfo.Environment)
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException(
                $"Failed to start Muse process '{startInfo.Executable}'.");
        return new MuseProcess(process);
    }

    private sealed class MuseProcess : IMuseProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _terminateRequested;

        public MuseProcess(Process process)
        {
            _process = process;
            Exited = WaitForExitAsync(_process, _exited);
        }

        public int Id => _process.Id;

        public Stream Stdin => _process.StandardInput.BaseStream;

        public Stream Stdout => _process.StandardOutput.BaseStream;

        public Stream Stderr => _process.StandardError.BaseStream;

        public Task<int> Exited { get; }

        public async Task TerminateAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _terminateRequested, 1) == 1)
            {
                await Exited.ConfigureAwait(false);
                return;
            }

            TrySignalTerm();
            try
            {
                await Exited.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
            }

            TryKillTree();
            await Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    TryKillTree();
                }

                await Exited.ConfigureAwait(false);
            }
            finally
            {
                _process.Dispose();
            }
        }

        private void TrySignalTerm()
        {
            try
            {
                if (_process.HasExited)
                {
                    return;
                }

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    _process.Kill(entireProcessTree: false);
                    return;
                }

                _ = NativeKill(_process.Id, SigTerm);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private void TryKillTree()
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static async Task<int> WaitForExitAsync(
            Process process,
            TaskCompletionSource<int> completion)
        {
            try
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                completion.TrySetResult(process.ExitCode);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }

            return await completion.Task.ConfigureAwait(false);
        }

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int NativeKill(int pid, int sig);
    }
}
