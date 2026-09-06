using System.Collections.Concurrent;
using System.Diagnostics;

namespace PiCommandCenter.Node.Verification;

/// <summary>
/// Keeps the native thread that created a process alive until that process exits.
/// Linux <c>PR_SET_PDEATHSIG</c> follows the creator thread rather than the whole
/// managed process, so bubblewrap's <c>--die-with-parent</c> requires this ownership.
/// </summary>
internal sealed class OwnedProcess : IDisposable
{
    private static readonly BlockingCollection<StartRequest> StartRequests = new();
    private static readonly Thread CreatorThread = StartCreatorThread();

    private OwnedProcess(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    public static async Task<OwnedProcess> StartAsync(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _ = CreatorThread;

        var started = new TaskCompletionSource<Process>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        StartRequests.Add(new StartRequest(startInfo, started));
        var process = await started.Task.ConfigureAwait(false);
        return new OwnedProcess(process);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        Process.WaitForExitAsync(cancellationToken);

    public void Dispose() => Process.Dispose();

    private static Thread StartCreatorThread()
    {
        var thread = new Thread(ProcessStartRequests)
        {
            IsBackground = true,
            Name = "DevFleet process creator",
        };
        thread.Start();
        return thread;
    }

    private static void ProcessStartRequests()
    {
        foreach (var request in StartRequests.GetConsumingEnumerable())
        {
            try
            {
                request.Started.TrySetResult(
                    Process.Start(request.StartInfo)
                    ?? throw new InvalidOperationException(
                        $"Failed to start '{request.StartInfo.FileName}'."));
            }
            catch (Exception ex)
            {
                request.Started.TrySetException(ex);
            }
        }
    }

    private sealed record StartRequest(
        ProcessStartInfo StartInfo,
        TaskCompletionSource<Process> Started);
}
