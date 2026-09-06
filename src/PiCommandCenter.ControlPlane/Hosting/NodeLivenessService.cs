using Microsoft.Extensions.Options;
using PiCommandCenter.Domain;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Domain.Nodes;

namespace PiCommandCenter.ControlPlane.Hosting;

/// <summary>
/// Periodically sweeps the fleet and marks nodes whose last heartbeat is older than
/// three heartbeat intervals as offline. Uses the registered <see cref="TimeProvider"/>
/// so time is injectable and testable; never throws out of the background loop.
/// </summary>
public sealed class NodeLivenessService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    IOptions<NodeLivenessOptions> options,
    ILogger<NodeLivenessService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Sweep a few times per heartbeat interval so a stale node flips offline
        // promptly after the third missed heartbeat rather than minutes later.
        var heartbeatSeconds = Math.Max(1, options.Value.HeartbeatSeconds);
        var period = TimeSpan.FromSeconds(Math.Clamp(heartbeatSeconds / 3d, 2, 15));
        var staleAfter = options.Value.StaleAfter;
        using var timer = new PeriodicTimer(period, timeProvider);

        while (!stoppingToken.IsCancellationRequested
            && await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var now = timeProvider.GetUtcNow();
                // The registry (and its DbContext) is scoped; resolve it per sweep
                // so the hosted singleton never captures a scoped service.
                await using var scope = scopeFactory.CreateAsyncScope();
                var registry = scope.ServiceProvider.GetRequiredService<INodeRegistry>();
                var nodes = await registry.ListAsync(stoppingToken).ConfigureAwait(false);
                foreach (var node in nodes)
                {
                    if (node.Status != NodeStatus.Online)
                    {
                        continue;
                    }

                    if (now - node.LastHeartbeatAt <= staleAfter)
                    {
                        continue;
                    }

                    await registry.MarkStaleOfflineAsync(new NodeId(node.Id), now, stoppingToken)
                        .ConfigureAwait(false);
                    logger.LogInformation(
                        "Marked node {NodeId} offline after missed heartbeats",
                        node.Id);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Node liveness sweep failed; retrying next interval");
            }
        }
    }
}
