using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Nodes;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Nodes;

/// <summary>
/// EF Core backed node registry. Registration is an upsert keyed by the node's stable id, so a
/// node reconnecting after a restart refreshes its identity instead of forking a new row.
/// </summary>
public sealed class NodeRegistry(
    TimeProvider clock,
    ControlPlaneDbContext db,
    IProjectionNotifier notifier) : INodeRegistry
{
    public async Task<IReadOnlyList<NodeDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await db.FleetNodes
            .OrderBy(n => n.DisplayName)
            .ThenBy(n => n.Id)
            .ToListAsync(cancellationToken);

        return nodes.Select(ToDto).ToList();
    }

    public async Task<NodeDto?> GetAsync(NodeId id, CancellationToken cancellationToken = default)
    {
        var node = await db.FleetNodes
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

        return node is null ? null : ToDto(node);
    }

    public async Task<NodeDto> RegisterAsync(
        RegisterNodeCommand command,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.FleetNodes
            .SingleOrDefaultAsync(n => n.Id == command.Id, cancellationToken);

        if (existing is null)
        {
            var node = FleetNode.Register(
                command.Id,
                command.DisplayName,
                command.AgentVersion,
                command.CapabilitiesJson,
                at);

            db.FleetNodes.Add(node);
            await db.SaveChangesAsync(cancellationToken);
            notifier.Publish(ProjectionChange.Fleet());
            return ToDto(node);
        }

        // Re-registration from a live node refreshes identity metadata and brings it online.
        existing.RefreshRegistration(command.DisplayName, command.AgentVersion, command.CapabilitiesJson, at);
        await SaveWithConcurrencyRetryAsync(
            existing,
            now => existing.RefreshRegistration(command.DisplayName, command.AgentVersion, command.CapabilitiesJson, now),
            cancellationToken);
        notifier.Publish(ProjectionChange.Fleet());
        return ToDto(existing);
    }

    public async Task<NodeDto> HeartbeatAsync(
        NodeHeartbeatCommand command,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var node = await db.FleetNodes
            .SingleOrDefaultAsync(n => n.Id == command.Id, cancellationToken)
            ?? throw new NodeNotFoundException(command.Id);

        node.Heartbeat(node.AgentVersion, node.CapabilitiesJson, at);
        await SaveWithConcurrencyRetryAsync(
            node,
            now => node.Heartbeat(node.AgentVersion, node.CapabilitiesJson, now),
            cancellationToken);
        notifier.Publish(ProjectionChange.Fleet());
        return ToDto(node);
    }

    public async Task MarkStaleOfflineAsync(
        NodeId id,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        var node = await db.FleetNodes
            .SingleOrDefaultAsync(n => n.Id == id, cancellationToken);

        if (node is null)
        {
            return;
        }

        node.MarkOffline(at);
        if (db.Entry(node).State == EntityState.Modified)
        {
            await db.SaveChangesAsync(cancellationToken);
            notifier.Publish(ProjectionChange.Fleet());
        }
    }

    /// <summary>
    /// Re-reads the current row on an optimistic-concurrency loss and replays the mutation,
    /// so a racing heartbeat cannot fail a registration or heartbeat call.
    /// </summary>
    private async Task SaveWithConcurrencyRetryAsync(
        FleetNode node,
        Action<DateTimeOffset> replay,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.Entry(node).Reload();
                replay(clock.GetUtcNow());
            }
        }
    }

    private static NodeDto ToDto(FleetNode node) => new(
        node.Id.Value,
        node.DisplayName,
        node.AgentVersion,
        node.LastHeartbeatAt,
        node.Status,
        node.CapabilitiesJson,
        node.Version);
}
