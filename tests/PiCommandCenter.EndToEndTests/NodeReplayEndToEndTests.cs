using Microsoft.AspNetCore.Hosting;
using PiCommandCenter.ControlPlane.Security;
using PiCommandCenter.Infrastructure.Security;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Node;

namespace PiCommandCenter.EndToEndTests;

/// <summary>
/// The journey behind Milestone 2's hardest guarantee: an event spooled on the node's local
/// SQLite database survives a Control Plane restart, is replayed when the node reconnects, and
/// the server never stores a duplicate. Uses the same mechanics as the node worker (spool,
/// publish, exact-acknowledge deletion) against two sequential Control Plane hosts over one
/// persisted SQLite database — no sleeps beyond bounded connection synchronization.
/// </summary>
public sealed class NodeReplayEndToEndTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "pi-cc-e2e-replay", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Spooled_event_survives_a_control_plane_restart_without_duplicating()
    {
        Directory.CreateDirectory(_tempRoot);
        var sqlitePath = Path.Combine(_tempRoot, "controlplane.db");
        var spoolPath = Path.Combine(_tempRoot, "spool.db");
        var nodeId = Guid.NewGuid();

        NodeEventMessage message = null!;
        using (var first = CreateControlPlane(sqlitePath, nodeId))
        {
            await using var connection = await ConnectAsync(first);
            await connection.InvokeAsync<NodeDto>(
                "Register", new NodeRegistrationMessage(nodeId, "pi-replay", "1.0.0", "{}"));

            message = new NodeEventMessage(
                "evt-replay-1", nodeId, await SeedProjectAsync(first, nodeId), null, "s-1",
                Sequence: 1, "session.log", DateTimeOffset.UtcNow, "{\"line\":\"before restart\"}");

            // Publish to the server and receive an exact acknowledgement... then "crash".
            await using (var spool = CreateSpool(spoolPath))
            {
                await spool.AppendAsync(message, CancellationToken.None);
            }

            // The node durably spools first, publishes, and receives the ack — but crashes
            // before the local delete, leaving the spool row in place.
            await using (var spool = CreateSpool(spoolPath))
            {
                var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
                var spooled = Assert.Single(pending);
                Assert.Equal(message, spooled);
                var ack = await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                    "PublishEvents", new NodeEventBatchMessage(pending));
                Assert.Equal([message.EventId], ack.EventIds);
            }

            Assert.Equal(1, CountServerEvents(sqlitePath, message.EventId));
            Assert.Equal([message.EventId], await PendingIdsAsync(CreateSpool(spoolPath)));
        }

        // Control Plane restart: brand-new host over the same SQLite file, the node reconnects.
        using (var second = CreateControlPlane(sqlitePath, nodeId))
        {
            await using var connection = await ConnectAsync(second);
            var registered = await connection.InvokeAsync<NodeDto>(
                "Register", new NodeRegistrationMessage(nodeId, "pi-replay", "1.0.0", "{}"));
            Assert.Equal(NodeStatus.Online, registered.Status);

            // The crash left the acknowledged event stranded in the spool: replay it.
            await using (var spool = CreateSpool(spoolPath))
            {
                var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
                var spooled = Assert.Single(pending);
                Assert.Equal(message, spooled);

                var ack = await connection.InvokeAsync<NodeEventAcknowledgementMessage>(
                    "PublishEvents", new NodeEventBatchMessage(pending));
                Assert.Equal([message.EventId], ack.EventIds);
                await spool.DeleteAsync([message.EventId], CancellationToken.None);
            }

            Assert.Empty(await PendingIdsAsync(CreateSpool(spoolPath)));
            Assert.Equal(1, CountServerEvents(sqlitePath, message.EventId));
        }
    }

    private static SqliteNodeEventSpool CreateSpool(string path) =>
        new(Options.Create(new NodeOptions { EventSpoolPath = path }));

    private static async Task<IReadOnlyList<string>> PendingIdsAsync(INodeEventSpool spool)
    {
        var pending = await spool.PeekPendingAsync(100, CancellationToken.None);
        return pending.Select(e => e.EventId).ToList();
    }

    private static int CountServerEvents(string sqlitePath, string eventId)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={sqlitePath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM SessionEvents WHERE EventId = $id";
        command.Parameters.AddWithValue("$id", eventId);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static async Task<Guid> SeedProjectAsync(WebApplicationFactory<Program> factory, Guid nodeId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var now = DateTimeOffset.UtcNow;
        var project = Project.Register(
            new NodeId(nodeId), "Replay project", Path.Combine(Path.GetTempPath(), "pi-cc-e2e-replay", "repo"),
            "main", enabled: true, maxActiveWriteRequests: 1, maxReadOnlyRequests: 2,
            maxChildAgentsPerRequest: 1, requireCleanStart: false, createRequestBranch: false,
            createRequestCommit: false, autoMerge: false, now);
        db.Projects.Add(project);
        db.WorkRequests.Add(WorkRequest.Enqueue(project.Id, WorkRequestKind.Analysis, RequestPriority.Normal,
            RiskLevel.Standard, "Replay request", "Prove the replay", now));
        await db.SaveChangesAsync();
        return project.Id.Value;
    }

    private WebApplicationFactory<Program> CreateControlPlane(string sqlitePath, Guid nodeId)
    {
        if (!File.Exists(sqlitePath))
        {
            File.Create(sqlitePath).Dispose();
        }

        var (passwordFile, credentialFile) = AuthTestMaterial.WriteTo(Path.Combine(_tempRoot, "auth"));
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:ControlPlane", $"Data Source={sqlitePath}");
            builder.UseSetting("Projects:NodeId", nodeId.ToString());
            builder.UseTestAuthFiles(passwordFile, credentialFile);
        });
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>().Database.Migrate();
        return factory;
    }

    private static async Task<HubConnection> ConnectAsync(WebApplicationFactory<Program> factory)
    {
        _ = factory.CreateClient();
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "nodeHub"), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(AuthTestMaterial.NodeTokenHex);
            })
            .Build();
        await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        return connection;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
