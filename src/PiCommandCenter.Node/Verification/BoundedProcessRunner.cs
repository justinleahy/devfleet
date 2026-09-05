using System.Diagnostics;
using System.Text;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Starts a process with <see cref="ProcessStartInfo.ArgumentList"/> only (no shell),
/// captures bounded stdout/stderr, and kills the process tree on timeout or cancel.
/// </summary>
public static class BoundedProcessRunner
{
    public static async Task<BoundedProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        int maxOutputBytes,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var started = DateTime.UtcNow;
        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Failed to start '{executable}'.");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            return new BoundedProcessResult(
                ExitCode: null,
                Duration: DateTime.UtcNow - started,
                StandardOutput: string.Empty,
                StandardError: ex.Message,
                TimedOut: false,
                Cancelled: false,
                Crashed: true,
                OutputTruncated: false);
        }

        using (process)
        {
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, maxOutputBytes, timeoutCts.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, maxOutputBytes, timeoutCts.Token);

            var timedOut = false;
            var cancelled = false;
            var crashed = false;
            int? exitCode;

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
                exitCode = process.HasExited ? process.ExitCode : null;
                if (exitCode is null)
                {
                    crashed = true;
                }
            }
            catch (OperationCanceledException)
            {
                cancelled = cancellationToken.IsCancellationRequested;
                timedOut = !cancelled;
                TryKillTree(process);
                try
                {
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // ignored: capture whatever exit we can
                }

                exitCode = process.HasExited ? process.ExitCode : null;
                crashed = !timedOut && !cancelled;
            }

            BoundedRead stdout;
            BoundedRead stderr;
            try
            {
                stdout = await stdoutTask.ConfigureAwait(false);
                stderr = await stderrTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stdout = new BoundedRead(string.Empty, false);
                stderr = new BoundedRead(string.Empty, false);
            }

            return new BoundedProcessResult(
                exitCode,
                DateTime.UtcNow - started,
                stdout.Text,
                stderr.Text,
                timedOut,
                cancelled,
                crashed,
                stdout.Truncated || stderr.Truncated);
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // already exiting
        }
    }

    private static async Task<BoundedRead> ReadBoundedAsync(
        Stream stream,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var collected = new MemoryStream();
        var truncated = false;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (read == 0)
            {
                break;
            }

            var remaining = maxBytes - (int)collected.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            var take = Math.Min(remaining, read);
            collected.Write(buffer, 0, take);
            if (take < read)
            {
                truncated = true;
            }
        }

        return new BoundedRead(Encoding.UTF8.GetString(collected.ToArray()), truncated);
    }

    private readonly record struct BoundedRead(string Text, bool Truncated);
}

/// <summary>Outcome of <see cref="BoundedProcessRunner.RunAsync"/>.</summary>
public sealed record BoundedProcessResult(
    int? ExitCode,
    TimeSpan Duration,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    bool Crashed,
    bool OutputTruncated);
