using Microsoft.Extensions.Hosting;

namespace PiCommandCenter.Node;

/// <summary>
/// Node background service. Holds the worker host alive; runtime session handling
/// arrives with later milestones over the versioned protocol.
/// </summary>
public sealed class NodeWorker : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown.
        }
    }
}
