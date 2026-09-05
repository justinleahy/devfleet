using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PiCommandCenter.Node.Runtime.Claude;

/// <summary>
/// Spawns an official CLI with redirected stdio and no shell. Signals use POSIX <c>kill</c>
/// so cancel can escalate SIGINT then SIGTERM.
/// </summary>
public sealed class OfficialAgentProcessFactory : IOfficialAgentProcessFactory
{
    public const int SigInt = 2;
    public const int SigTerm = 15;

    public IOfficialAgentProcess Start(OfficialProcessStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
                ? null
                : request.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.ExtraEnvironment is not null)
        {
            foreach (var pair in request.ExtraEnvironment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start official process '{request.FileName}'.");
        return new OfficialAgentProcess(process);
    }

    private sealed class OfficialAgentProcess : IOfficialAgentProcess
    {
        private readonly Process _process;
        private readonly TaskCompletionSource<int> _exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _killed;

        public OfficialAgentProcess(Process process)
        {
            _process = process;
            Exited = WaitForExitAsync(_process, _exited);
        }

        public int Id => _process.Id;

        public Stream Stdout => _process.StandardOutput.BaseStream;

        public Stream Stderr => _process.StandardError.BaseStream;

        public Task<int> Exited { get; }

        public Task SignalAsync(int signal, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                return Task.CompletedTask;
            }

            if (OperatingSystem.IsWindows())
            {
                if (signal == SigTerm || signal == SigInt)
                {
                    try
                    {
                        _process.Kill(entireProcessTree: false);
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }

                return Task.CompletedTask;
            }

            _ = NativeKill(_process.Id, signal);
            return Task.CompletedTask;
        }

        public async Task KillTreeAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _killed, 1) == 1)
            {
                await Exited.ConfigureAwait(false);
                return;
            }

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

            await Exited.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _killed, 1) == 0 && !_process.HasExited)
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

        [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
        private static extern int NativeKill(int pid, int sig);
    }
}
