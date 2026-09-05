using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Live;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Nodes;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Sessions;

namespace PiCommandCenter.Infrastructure.Tests;

/// <summary>
/// Persistence contract for <see cref="SessionEvent.PayloadJson"/>: nested telemetry JSON
/// (token usage, cost breakdowns) must survive the store round-trip so the statistics
/// parser can read it, and duplicate deliveries must leave the stored row untouched.
/// </summary>
public class AgentSessionPersistenceTests : IDisposable
{
    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private static readonly DateTimeOffset Base = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_sqlitePath)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private Guid _nodeId;
    private Guid _projectId;
    private Guid _requestId;

    private NormalizedAgentEvent TelemetryEvent(
        IReadOnlyDictionary<string, object?> payload,
        string eventId = "evt-usage-1") => new(
        ProtocolVersion: 1,
        EventId: eventId,
        NodeId: _nodeId.ToString("D"),
        ProjectId: _projectId.ToString("D"),
        RequestId: _requestId.ToString("D"),
        SessionId: "session-usage",
        ParentSessionId: null,
        Sequence: 1,
        Runtime: "pi",
        Type: "message.completed",
        OccurredAt: Base,
        Payload: payload);

    private async Task<IAgentSessionStore> CreateStoreAsync(ControlPlaneDbContext db)
    {
        _nodeId = TestNodes.NewNodeId().Value;
        var clock = TestNodes.Clock();
        TestNodes.SeedNode(db, new NodeId(_nodeId), clock);
        var project = TestNodes.SeedProject(db, new NodeId(_nodeId), clock);
        _projectId = project.Id.Value;
        var request = TestNodes.SeedRequest(db, project, clock);
        _requestId = request.Id.Value;
        await TestNodes.SaveAsync(db);
        return new AgentSessionStore(TimeProvider.System, db, new ProjectionNotifier());
    }

    private static JsonElement NestedUsage() => JsonDocument.Parse("""
        {
            "usage": {
                "input": 1200,
                "output": 340,
                "cacheRead": 0,
                "thinking": 87
            },
            "cost": { "total": 0.0123 }
        }
        """).RootElement.Clone();

    [Fact]
    public async Task ApplyAsync_persists_nested_usage_and_cost_json_unchanged()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(TelemetryEvent(new Dictionary<string, object?>
        {
            ["agentName"] = "worker",
            ["telemetry"] = NestedUsage(),
            ["note"] = "final",
            ["ok"] = true,
            ["missing"] = null,
        }));

        await using var verify = CreateContext();
        var row = await verify.SessionEvents.SingleAsync();
        using var document = JsonDocument.Parse(row.PayloadJson);
        var telemetry = document.RootElement.GetProperty("telemetry");

        var usage = telemetry.GetProperty("usage");
        Assert.Equal(JsonValueKind.Object, usage.ValueKind);
        Assert.Equal(1200, usage.GetProperty("input").GetInt64());
        Assert.Equal(340, usage.GetProperty("output").GetInt64());
        Assert.Equal(0, usage.GetProperty("cacheRead").GetInt64());
        Assert.Equal(87, usage.GetProperty("thinking").GetInt64());

        var total = telemetry.GetProperty("cost").GetProperty("total");
        Assert.Equal(JsonValueKind.Number, total.ValueKind);
        Assert.Equal(0.0123m, total.GetDecimal());

        // Scalar behavior is unchanged.
        Assert.Equal("worker", document.RootElement.GetProperty("agentName").GetString());
        Assert.Equal("final", document.RootElement.GetProperty("note").GetString());
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("missing").ValueKind);
    }

    [Fact]
    public async Task ApplyAsync_persists_nested_arrays_and_deeply_nested_objects()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(TelemetryEvent(new Dictionary<string, object?>
        {
            ["telemetry"] = JsonDocument.Parse("""
                { "turns": [ { "usage": { "input": 10 } }, { "usage": { "input": 20 } } ] }
                """).RootElement.Clone(),
        }));

        await using var verify = CreateContext();
        var row = await verify.SessionEvents.SingleAsync();
        using var document = JsonDocument.Parse(row.PayloadJson);
        var turns = document.RootElement.GetProperty("telemetry").GetProperty("turns");
        Assert.Equal(JsonValueKind.Array, turns.ValueKind);
        Assert.Equal(2, turns.GetArrayLength());
        Assert.Equal(10, turns[0].GetProperty("usage").GetProperty("input").GetInt64());
        Assert.Equal(20, turns[1].GetProperty("usage").GetProperty("input").GetInt64());
    }

    [Fact]
    public async Task ApplyAsync_duplicate_event_ids_leave_the_stored_payload_untouched()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);
        var original = TelemetryEvent(new Dictionary<string, object?>
        {
            ["telemetry"] = NestedUsage(),
        });

        await store.ApplyAsync(original);
        // Same id, conflicting payload: replay must be fully inert.
        await store.ApplyAsync(TelemetryEvent(
            new Dictionary<string, object?> { ["telemetry"] = JsonDocument.Parse("""{"usage":{"input":999999}}""").RootElement.Clone() },
            eventId: original.EventId));

        await using var verify = CreateContext();
        var row = await verify.SessionEvents.SingleAsync();
        using var document = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(1200, document.RootElement
            .GetProperty("telemetry").GetProperty("usage").GetProperty("input").GetInt64());
    }

    [Fact]
    public async Task ApplyAsync_with_empty_payload_persists_an_empty_json_object()
    {
        await using var db = CreateContext();
        var store = await CreateStoreAsync(db);

        await store.ApplyAsync(TelemetryEvent(new Dictionary<string, object?>()));

        await using var verify = CreateContext();
        var row = await verify.SessionEvents.SingleAsync();
        using var document = JsonDocument.Parse(row.PayloadJson);
        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateObject());
    }
}
