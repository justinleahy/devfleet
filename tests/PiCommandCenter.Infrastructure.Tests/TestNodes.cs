using PiCommandCenter.Domain;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Infrastructure.Tests;

/// <summary>Deterministic clock for infrastructure tests; never a real sleep.</summary>
public sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public void Advance(TimeSpan delta) => _now += delta;

    public void Set(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}

/// <summary>Seeds nodes, projects, and queued requests directly through the domain model.</summary>
public static class TestNodes
{
    public static readonly DateTimeOffset Start = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Clock() => new(Start);

    public static NodeId NewNodeId() => new(Guid.NewGuid());

    public static FleetNode SeedNode(ControlPlaneDbContext db, NodeId nodeId, FakeTimeProvider clock)
    {
        var node = FleetNode.Register(nodeId, "node-" + nodeId.Value.ToString("N")[..6], "1.0.0", "{}", clock.GetUtcNow());
        db.FleetNodes.Add(node);
        return node;
    }

    public static Project SeedProject(
        ControlPlaneDbContext db,
        FakeTimeProvider clock,
        bool enabled = true,
        int maxReadOnlyRequests = 4,
        int maxActiveWriteRequests = 2,
        string? displayName = null)
    {
        var project = Project.Register(
            displayName ?? "Project " + Guid.NewGuid().ToString("N")[..6],
            "main",
            enabled,
            maxActiveWriteRequests,
            maxReadOnlyRequests,
            maxChildAgentsPerRequest: 1,
            requireCleanStart: false,
            createRequestBranch: false,
            createRequestCommit: false,
            autoMerge: false,
            clock.GetUtcNow());
        db.Projects.Add(project);
        return project;
    }

    public static WorkRequest SeedRequest(
        ControlPlaneDbContext db,
        Project project,
        FakeTimeProvider clock,
        WorkRequestKind kind = WorkRequestKind.Development,
        RequestPriority priority = RequestPriority.Normal,
        string? title = null)
    {
        var request = WorkRequest.Enqueue(
            project.Id,
            kind,
            priority,
            RiskLevel.Standard,
            title ?? "Request " + Guid.NewGuid().ToString("N")[..6],
            "Do the thing",
            clock.GetUtcNow());
        db.WorkRequests.Add(request);
        return request;
    }

    public static async Task SaveAsync(ControlPlaneDbContext db)
    {
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
