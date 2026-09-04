using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PiCommandCenter.Contracts;

namespace PiCommandCenter.Node.Tests;

public class AddPiNodeTests
{
    [Fact]
    public void AddPiNode_registers_the_node_worker_as_a_hosted_service()
    {
        var services = new ServiceCollection().AddPiNode();

        using var provider = services.BuildServiceProvider();

        Assert.IsType<NodeWorker>(provider.GetRequiredService<NodeWorker>());
        Assert.Contains(provider.GetServices<IHostedService>(), s => s is NodeWorker);
    }

    [Fact]
    public void NodeWorker_survives_cancellation_and_reports_success()
    {
        using var cts = new CancellationTokenSource();
        var worker = new NodeWorker();
        var execution = worker.StartAsync(cts.Token);

        Assert.Equal(TaskStatus.RanToCompletion, execution.Status);
        cts.Cancel();
    }

    [Fact]
    public void Protocol_version_is_current()
    {
        Assert.Equal(1, ProtocolVersion.Current);
    }
}
