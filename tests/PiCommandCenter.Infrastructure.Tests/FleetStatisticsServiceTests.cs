using System.Text.Json;
using PiCommandCenter.Application.Statistics;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Statistics;

namespace PiCommandCenter.Infrastructure.Tests;

/// <summary>
/// Aggregation contract for <see cref="FleetStatisticsService"/>: per-runtime normalization
/// (Pi sums finals; Claude/Antigravity/Muse replace), null-vs-zero discipline, malformed-event
/// isolation with ignored counting, active-agent rules, and ordinal runtime grouping.
/// </summary>
public class FleetStatisticsServiceTests : IDisposable
{
    private static readonly DateTimeOffset Base = new(2026, 9, 5, 10, 0, 0, TimeSpan.Zero);

    private readonly string _sqlitePath = TestRepositories.CreateSqliteFile();
    private readonly Guid _projectId = Guid.NewGuid();
    private readonly Guid _requestId = Guid.NewGuid();

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

    private ControlPlaneDbContext CreateContext() => TestRepositories.CreateContext(_sqlitePath);

    private FleetStatisticsService CreateService(ControlPlaneDbContext db) => new(db);

    private AgentSessionRow Session(
        string id,
        string runtime,
        string liveness = "Online",
        string workState = "Executing",
        bool ended = false) => new()
    {
        Id = id,
        ProjectId = _projectId,
        RequestId = _requestId,
        AgentName = id,
        Role = "task",
        Runtime = runtime,
        Model = "model",
        Liveness = liveness,
        Activity = "Responding",
        Attention = "None",
        WorkState = workState,
        StatusReason = string.Empty,
        StartedAtUtcTicks = Base.UtcTicks,
        EndedAtUtcTicks = ended ? Base.UtcTicks : null,
    };

    private SessionEvent Telemetry(
        string sessionId,
        string type,
        string payloadJson,
        long sequence,
        long occurredAtOffsetSeconds = 0) => new()
    {
        EventId = $"evt-{sessionId}-{sequence}",
        NodeId = Guid.NewGuid(),
        ProjectId = _projectId,
        RequestId = _requestId,
        SessionId = sessionId,
        Sequence = sequence,
        Type = type,
        OccurredAtUtcTicks = Base.AddSeconds(occurredAtOffsetSeconds).UtcTicks,
        ReceivedAtUtcTicks = Base.AddSeconds(occurredAtOffsetSeconds).UtcTicks,
        PayloadJson = payloadJson,
    };

    private async Task SeedAsync(
        IReadOnlyList<AgentSessionRow> sessions,
        IReadOnlyList<SessionEvent> events)
    {
        await using var db = CreateContext();
        db.AgentSessions.AddRange(sessions);
        db.SessionEvents.AddRange(events);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task GetAsync_no_sessions_returns_empty_statistics()
    {
        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(0, result.TrackedAgents);
        Assert.Equal(0, result.ActiveAgents);
        Assert.Equal(0, result.AgentsWithReportedTokens);
        Assert.Equal(0, result.AgentsWithEstimatedCost);
        Assert.Equal(new TokenTotalsDto(null, null, null, null, null), result.Tokens);
        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
        Assert.Null(result.LatestTelemetryAt);
        Assert.Empty(result.Runtimes);
    }

    [Fact]
    public async Task GetAsync_pi_sums_final_message_and_compaction_usage()
    {
        var sessions = new[] { Session("s-pi", "pi") };
        var events = new[]
        {
            Telemetry("s-pi", "message.completed", """
                { "data": { "type": "message_end", "message": { "role": "assistant",
                  "usage": { "input": 100, "output": 40, "cacheRead": 10, "cacheWrite": 5,
                             "reasoning": 7, "totalTokens": 155,
                             "cost": { "total": 0.01 } } } } }
                """, 1),
            Telemetry("s-pi", "message.completed", """
                { "data": { "type": "message_end", "message": { "role": "assistant",
                  "usage": { "input": 200, "output": 60, "cacheRead": 0, "cacheWrite": 3,
                             "totalTokens": 263, "cost": { "total": 0.02 } } } } }
                """, 2),
            Telemetry("s-pi", "compaction.completed", """
                { "data": { "type": "compaction_end",
                  "result": { "usage": { "input": 50, "output": 10, "cacheRead": 2,
                                         "cacheWrite": 1, "totalTokens": 63 } } } }
                """, 3),
            // Streaming updates are never summed.
            Telemetry("s-pi", "sdk.message_update", """
                { "data": { "message": { "usage": { "input": 9999, "output": 9999,
                  "cacheRead": 9999, "cacheWrite": 9999 } } } }
                """, 4),
            // User messages carry no usage and are skipped silently.
            Telemetry("s-pi", "message.completed", """
                { "data": { "type": "message_end", "message": { "role": "user" } } }
                """, 5, occurredAtOffsetSeconds: 30),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(350, 110, 12, 9, 7), result.Tokens);
        Assert.Equal(0.03m, result.EstimatedCostUsd);
        Assert.Equal(1, result.AgentsWithReportedTokens);
        Assert.Equal(1, result.AgentsWithEstimatedCost);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
        Assert.Equal(Base, result.LatestTelemetryAt);
    }

    [Fact]
    public async Task GetAsync_claude_replaces_from_latest_result_and_prefers_model_usage()
    {
        var sessions = new[] { Session("s-claude", "claude-code") };
        var events = new[]
        {
            Telemetry("s-claude", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 1, "output_tokens": 1,
                    "cache_read_input_tokens": null, "cache_creation_input_tokens": null },
                  "modelUsage": {
                    "claude-opus": { "inputTokens": 100, "outputTokens": 40,
                                     "thinkingTokens": 5, "cacheReadInputTokens": 10,
                                     "cacheCreationInputTokens": 8, "costUSD": 0.10 },
                    "claude-haiku": { "inputTokens": 20, "outputTokens": 6,
                                      "cacheReadInputTokens": 2,
                                      "cacheCreationInputTokens": 1, "costUSD": 0.01 } },
                  "total_cost_usd": 0.11 }
                """, 1),
            // Later result replaces the snapshot wholesale (modelUsage absent -> usage).
            Telemetry("s-claude", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 300, "output_tokens": 90,
                    "cache_read_input_tokens": 12, "cache_creation_input_tokens": 4 },
                  "total_cost_usd": 0.50 }
                """, 2, occurredAtOffsetSeconds: 60),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(300, 90, 12, 4, null), result.Tokens);
        Assert.Equal(0.50m, result.EstimatedCostUsd);
        Assert.Equal(Base.AddSeconds(60), result.LatestTelemetryAt);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
    }

    [Fact]
    public async Task GetAsync_claude_sums_model_usage_across_models_within_one_result()
    {
        var sessions = new[] { Session("s-claude", "claude-code") };
        var events = new[]
        {
            Telemetry("s-claude", "result.completed", """
                { "type": "result",
                  "modelUsage": {
                    "a": { "inputTokens": 100, "outputTokens": 40, "thinkingTokens": 5,
                           "cacheReadInputTokens": 10, "cacheCreationInputTokens": 8 },
                    "b": { "inputTokens": 20, "outputTokens": 6,
                           "cacheReadInputTokens": 2, "cacheCreationInputTokens": 1 } },
                  "total_cost_usd": 0.25 }
                """, 1),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(120, 46, 12, 9, 5), result.Tokens);
        Assert.Equal(0.25m, result.EstimatedCostUsd);
    }

    [Fact]
    public async Task GetAsync_antigravity_replaces_from_latest_turn_usage_without_cost()
    {
        var sessions = new[] { Session("s-agy", "antigravity") };
        var events = new[]
        {
            Telemetry("s-agy", "turn.completed", """
                { "status": "SUCCESS", "usage": { "input_tokens": 30384, "output_tokens": 600,
                  "thinking_tokens": 100, "cache_read_tokens": 8000, "total_tokens": 30984 } }
                """, 1),
            Telemetry("s-agy", "turn.completed", """
                { "status": "SUCCESS", "usage": { "input_tokens": 30662, "output_tokens": 657,
                  "thinking_tokens": 616, "cache_read_tokens": 8113, "total_tokens": 11072 } }
                """, 2),
            // Turn without usage is skipped silently.
            Telemetry("s-agy", "turn.completed", """{ "status": "SUCCESS" }""", 3),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(30662, 657, 8113, null, 616), result.Tokens);
        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(1, result.AgentsWithReportedTokens);
        Assert.Equal(0, result.AgentsWithEstimatedCost);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
    }

    [Fact]
    public async Task GetAsync_antigravity_replaces_from_failed_turn_and_cancelled_session()
    {
        var sessions = new[] { Session("s-agy", "antigravity") };
        var events = new[]
        {
            Telemetry("s-agy", "turn.completed", """
                { "status": "SUCCESS", "usage": { "input_tokens": 100, "output_tokens": 10,
                  "thinking_tokens": 1, "cache_read_tokens": 50 } }
                """, 1),
            // A failed turn still reports cumulative usage and replaces the snapshot.
            Telemetry("s-agy", "turn.failed", """
                { "status": "FAILED", "usage": { "input_tokens": 200, "output_tokens": 20,
                  "thinking_tokens": 2, "cache_read_tokens": 60 } }
                """, 2),
            // A cancelled session reports its final cumulative usage and replaces again.
            Telemetry("s-agy", "session.cancelled", """
                { "status": "CANCELLED", "usage": { "input_tokens": 300, "output_tokens": 30,
                  "thinking_tokens": 3, "cache_read_tokens": 70 } }
                """, 3, occurredAtOffsetSeconds: 60),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(300, 30, 70, null, 3), result.Tokens);
        Assert.Equal(1, result.AgentsWithReportedTokens);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
        Assert.Equal(Base.AddSeconds(60), result.LatestTelemetryAt);
    }

    [Theory]
    // Present but empty: no rows to sum, never fall back to top-level usage.
    [InlineData("""{ "type": "result", "usage": { "input_tokens": 999, "output_tokens": 999 }, "modelUsage": {} }""")]
    // Present but not an object.
    [InlineData("""{ "type": "result", "usage": { "input_tokens": 999, "output_tokens": 999 }, "modelUsage": "lots" }""")]
    // Present but JSON null.
    [InlineData("""{ "type": "result", "usage": { "input_tokens": 999, "output_tokens": 999 }, "modelUsage": null }""")]
    public async Task GetAsync_claude_malformed_model_usage_never_falls_back_to_usage(string payload)
    {
        var sessions = new[] { Session("s-claude", "claude-code") };
        var events = new[]
        {
            Telemetry("s-claude", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 100, "output_tokens": 40,
                    "cache_read_input_tokens": null, "cache_creation_input_tokens": null },
                  "total_cost_usd": 0.10 }
                """, 1),
            Telemetry("s-claude", "result.completed", payload, 2),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(1, result.IgnoredTelemetryEvents);
        Assert.Equal(new TokenTotalsDto(100, 40, null, null, null), result.Tokens);
        Assert.Equal(0.10m, result.EstimatedCostUsd);
    }

    [Fact]
    public async Task GetAsync_claude_cross_model_token_overflow_skips_event_whole()
    {
        var sessions = new[] { Session("s-claude", "claude-code") };
        var events = new[]
        {
            Telemetry("s-claude", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 100, "output_tokens": 40,
                    "cache_read_input_tokens": null, "cache_creation_input_tokens": null },
                  "total_cost_usd": 0.10 }
                """, 1),
            // Each model row is individually valid, but their sum overflows Int64.
            Telemetry("s-claude", "result.completed", """
                { "type": "result",
                  "modelUsage": {
                    "a": { "inputTokens": 9223372036854775700, "outputTokens": 1,
                           "cacheReadInputTokens": 0, "cacheCreationInputTokens": 0 },
                    "b": { "inputTokens": 9223372036854775700, "outputTokens": 2,
                           "cacheReadInputTokens": 0, "cacheCreationInputTokens": 0 } },
                  "total_cost_usd": 0.99 }
                """, 2),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(1, result.IgnoredTelemetryEvents);
        Assert.Equal(new TokenTotalsDto(100, 40, null, null, null), result.Tokens);
        Assert.Equal(0.10m, result.EstimatedCostUsd);
    }

    [Fact]
    public async Task GetAsync_cross_session_token_overflow_fails_closed_per_series()
    {
        var sessions = new[]
        {
            Session("s-pi-1", "pi"),
            Session("s-pi-2", "pi"),
            Session("s-pi-3", "pi"),
        };
        var events = new[]
        {
            Telemetry("s-pi-1", "message.completed", """
                { "data": { "message": { "usage": { "input": 9223372036854775700, "output": 5,
                  "cacheRead": 0, "cacheWrite": 0 } } } }
                """, 1),
            Telemetry("s-pi-2", "message.completed", """
                { "data": { "message": { "usage": { "input": 9223372036854775700, "output": 6,
                  "cacheRead": 0, "cacheWrite": 0 } } } }
                """, 1),
            // A later session must not repopulate the overflowed series with a partial total.
            Telemetry("s-pi-3", "message.completed", """
                { "data": { "message": { "usage": { "input": 7, "output": 3,
                  "cacheRead": 0, "cacheWrite": 0 } } } }
                """, 1),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        // Only the overflowing input series is unavailable; other series still report.
        Assert.Equal(new TokenTotalsDto(null, 14, 0, 0, null), result.Tokens);
        Assert.Equal(3, result.AgentsWithReportedTokens);
        Assert.Equal(0, result.IgnoredTelemetryEvents);

        var pi = Assert.Single(result.Runtimes);
        Assert.Equal(new TokenTotalsDto(null, 14, 0, 0, null), pi.Tokens);
    }

    [Fact]
    public async Task GetAsync_cross_session_cost_overflow_fails_closed_without_losing_tokens()
    {
        var sessions = new[]
        {
            Session("s-pi-1", "pi"),
            Session("s-pi-2", "pi"),
            Session("s-pi-3", "pi"),
        };
        var events = new[]
        {
            Telemetry("s-pi-1", "message.completed", """
                { "data": { "message": { "usage": { "input": 10, "output": 5,
                  "cacheRead": 0, "cacheWrite": 0, "cost": { "total": 7e28 } } } } }
                """, 1),
            Telemetry("s-pi-2", "message.completed", """
                { "data": { "message": { "usage": { "input": 20, "output": 6,
                  "cacheRead": 0, "cacheWrite": 0, "cost": { "total": 7e28 } } } } }
                """, 1),
            // A later session must not repopulate the overflowed cost with a partial total.
            Telemetry("s-pi-3", "message.completed", """
                { "data": { "message": { "usage": { "input": 30, "output": 7,
                  "cacheRead": 0, "cacheWrite": 0, "cost": { "total": 0.01 } } } } }
                """, 1),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        // The cost aggregate overflows decimal and fails closed; tokens are unaffected.
        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(new TokenTotalsDto(60, 18, 0, 0, null), result.Tokens);
        Assert.Equal(3, result.AgentsWithEstimatedCost);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
    }

    [Fact]
    public async Task GetAsync_muse_replaces_cumulative_usage_and_accumulates_cache_reads()
    {
        var sessions = new[] { Session("s-muse", "muse") };
        var events = new[]
        {
            Telemetry("s-muse", "session.usage", """
                { "method": "tokenUsage",
                  "cumulative": { "promptTokens": 1000, "outputTokens": 100, "totalTokens": 1100 },
                  "usage": { "inputTokens": 1000, "outputTokens": 100, "reasoningTokens": 5,
                             "cachedTokens": 900, "cacheReadTokens": 900, "cacheWriteTokens": 0 } }
                """, 1),
            Telemetry("s-muse", "session.usage", """
                { "method": "tokenUsage",
                  "cumulative": { "promptTokens": 579968, "outputTokens": 4014,
                                  "totalTokens": 583982 },
                  "usage": { "inputTokens": 53177, "outputTokens": 610, "reasoningTokens": 203,
                             "cachedTokens": 52209, "cacheReadTokens": 52209,
                             "cacheWriteTokens": 0 } }
                """, 2),
            Telemetry("s-muse", "session.usage", """
                { "method": "tokenUsage",
                  "cumulative": { "promptTokens": 600000, "outputTokens": 5000,
                                  "totalTokens": 605000 },
                  "usage": { "inputTokens": 20032, "outputTokens": 986 } }
                """, 3),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(600000, 5000, 53109, null, null), result.Tokens);
        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
    }

    [Fact]
    public async Task GetAsync_muse_cache_overflow_rejects_whole_event()
    {
        var sessions = new[] { Session("s-muse", "muse") };
        var events = new[]
        {
            Telemetry("s-muse", "session.usage", """
                { "method": "tokenUsage",
                  "cumulative": { "promptTokens": 100, "outputTokens": 40 },
                  "usage": { "cacheReadTokens": 9223372036854775807 } }
                """, 1),
            Telemetry("s-muse", "session.usage", """
                { "method": "tokenUsage",
                  "cumulative": { "promptTokens": 200, "outputTokens": 80 },
                  "usage": { "cacheReadTokens": 1 } }
                """, 2),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(
            new TokenTotalsDto(100, 40, long.MaxValue, null, null),
            result.Tokens);
        Assert.Equal(1, result.IgnoredTelemetryEvents);
    }

    [Fact]
    public async Task GetAsync_null_vs_zero_missing_counters_stay_null_explicit_zero_is_zero()
    {
        var sessions = new[]
        {
            Session("s-quiet", "pi"),
            Session("s-zero", "claude-code"),
        };
        var events = new[]
        {
            Telemetry("s-zero", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 0, "output_tokens": 0,
                    "cache_read_input_tokens": null, "cache_creation_input_tokens": null } }
                """, 1),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new TokenTotalsDto(0, 0, null, null, null), result.Tokens);
        Assert.Equal(1, result.AgentsWithReportedTokens);
        Assert.Null(result.EstimatedCostUsd);
        Assert.Equal(0, result.AgentsWithEstimatedCost);

        var quiet = Assert.Single(result.Runtimes, r => r.Runtime == "pi");
        Assert.Equal(new TokenTotalsDto(null, null, null, null, null), quiet.Tokens);
        Assert.Equal(0, quiet.AgentsWithReportedTokens);
    }

    [Theory]
    // Negative token.
    [InlineData("""{ "data": { "message": { "usage": { "input": -1, "output": 40, "cacheRead": 10, "cacheWrite": 5 } } } }""")]
    // Fractional token.
    [InlineData("""{ "data": { "message": { "usage": { "input": 10.5, "output": 40, "cacheRead": 10, "cacheWrite": 5 } } } }""")]
    // Overflow beyond Int64.
    [InlineData("""{ "data": { "message": { "usage": { "input": 99999999999999999999999, "output": 40, "cacheRead": 10, "cacheWrite": 5 } } } }""")]
    // Non-finite / overflowing cost.
    [InlineData("""{ "data": { "message": { "usage": { "input": 10, "output": 40, "cacheRead": 10, "cacheWrite": 5, "cost": { "total": 1e999 } } } } }""")]
    // Usage present but not an object.
    [InlineData("""{ "data": { "message": { "usage": "lots" } } }""")]
    // Unparseable payload.
    [InlineData("""{ not json""")]
    public async Task GetAsync_malformed_pi_event_is_ignored_without_partial_update(string payload)
    {
        var sessions = new[] { Session("s-pi", "pi") };
        var events = new[]
        {
            Telemetry("s-pi", "message.completed", payload, 1),
            Telemetry("s-pi", "message.completed", """
                { "data": { "message": { "usage": { "input": 7, "output": 3,
                  "cacheRead": 1, "cacheWrite": 0 } } } }
                """, 2),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(1, result.IgnoredTelemetryEvents);
        Assert.Equal(new TokenTotalsDto(7, 3, 1, 0, null), result.Tokens);
    }

    [Fact]
    public async Task GetAsync_malformed_replacement_event_does_not_clobber_prior_snapshot()
    {
        var sessions = new[] { Session("s-claude", "claude-code") };
        var events = new[]
        {
            Telemetry("s-claude", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 100, "output_tokens": 40,
                    "cache_read_input_tokens": null, "cache_creation_input_tokens": null },
                  "total_cost_usd": 0.10 }
                """, 1),
            // Negative value inside modelUsage: event skipped whole, snapshot survives.
            Telemetry("s-claude", "result.completed", """
                { "type": "result",
                  "modelUsage": { "m": { "inputTokens": -5, "outputTokens": 1,
                    "cacheReadInputTokens": 0, "cacheCreationInputTokens": 0 } },
                  "total_cost_usd": 0.99 }
                """, 2),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(1, result.IgnoredTelemetryEvents);
        Assert.Equal(new TokenTotalsDto(100, 40, null, null, null), result.Tokens);
        Assert.Equal(0.10m, result.EstimatedCostUsd);
    }

    [Fact]
    public async Task GetAsync_muse_event_missing_cumulative_is_ignored()
    {
        var sessions = new[] { Session("s-muse", "muse") };
        var events = new[]
        {
            Telemetry("s-muse", "session.usage", """{ "method": "tokenUsage" }""", 1),
            Telemetry("s-muse", "session.usage", """
                { "method": "tokenUsage",
                  "cumulative": { "promptTokens": 10, "outputTokens": 2, "totalTokens": 12 } }
                """, 2),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(1, result.IgnoredTelemetryEvents);
        Assert.Equal(new TokenTotalsDto(10, 2, null, null, null), result.Tokens);
    }

    [Fact]
    public async Task GetAsync_active_rule_excludes_ended_exited_and_terminal_work_states()
    {
        var sessions = new[]
        {
            Session("s-active", "pi"),
            Session("s-ended", "pi", ended: true),
            Session("s-exited", "pi", liveness: "Exited"),
            Session("s-completed", "pi", workState: "Completed"),
            Session("s-failed", "pi", workState: "Failed"),
            Session("s-cancelled", "pi", workState: "Cancelled"),
            Session("s-blocked", "pi", workState: "Blocked"),
        };
        await SeedAsync(sessions, Array.Empty<SessionEvent>());

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(7, result.TrackedAgents);
        Assert.Equal(2, result.ActiveAgents);
    }

    [Fact]
    public async Task GetAsync_groups_by_runtime_ordered_ordinally_with_independent_totals()
    {
        var sessions = new[]
        {
            Session("s-pi-1", "pi"),
            Session("s-pi-2", "pi", workState: "Completed"),
            Session("s-claude", "claude-code"),
            Session("s-agy", "antigravity"),
        };
        var events = new[]
        {
            Telemetry("s-pi-1", "message.completed", """
                { "data": { "message": { "usage": { "input": 10, "output": 5,
                  "cacheRead": 0, "cacheWrite": 0, "cost": { "total": 0.01 } } } } }
                """, 1),
            Telemetry("s-pi-2", "message.completed", """
                { "data": { "message": { "usage": { "input": 20, "output": 6,
                  "cacheRead": 0, "cacheWrite": 0, "cost": { "total": 0.02 } } } } }
                """, 1),
            Telemetry("s-claude", "result.completed", """
                { "type": "result", "usage": { "input_tokens": 100, "output_tokens": 40,
                    "cache_read_input_tokens": 3, "cache_creation_input_tokens": 2 },
                  "total_cost_usd": 0.10 }
                """, 1),
            Telemetry("s-agy", "turn.completed", """
                { "usage": { "input_tokens": 50, "output_tokens": 9, "thinking_tokens": 4 } }
                """, 1),
            // Unknown event types are ignored silently.
            Telemetry("s-agy", "message.delta", """{ "textDelta": "hi" }""", 2),
            // Events for unknown sessions are skipped.
            Telemetry("s-ghost", "turn.completed", """
                { "usage": { "input_tokens": 1, "output_tokens": 1 } }
                """, 1),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var result = await CreateService(db).GetAsync();

        Assert.Equal(new[] { "antigravity", "claude-code", "pi" },
            result.Runtimes.Select(r => r.Runtime).ToArray());

        var antigravity = result.Runtimes[0];
        Assert.Equal(1, antigravity.TrackedAgents);
        Assert.Equal(1, antigravity.ActiveAgents);
        Assert.Equal(new TokenTotalsDto(50, 9, null, null, 4), antigravity.Tokens);
        Assert.Null(antigravity.EstimatedCostUsd);

        var claude = result.Runtimes[1];
        Assert.Equal(new TokenTotalsDto(100, 40, 3, 2, null), claude.Tokens);
        Assert.Equal(0.10m, claude.EstimatedCostUsd);

        var pi = result.Runtimes[2];
        Assert.Equal(2, pi.TrackedAgents);
        Assert.Equal(1, pi.ActiveAgents);
        Assert.Equal(new TokenTotalsDto(30, 11, 0, 0, null), pi.Tokens);
        Assert.Equal(0.03m, pi.EstimatedCostUsd);

        Assert.Equal(new TokenTotalsDto(180, 60, 3, 2, 4), result.Tokens);
        Assert.Equal(0.13m, result.EstimatedCostUsd);
        Assert.Equal(4, result.AgentsWithReportedTokens);
        Assert.Equal(3, result.AgentsWithEstimatedCost);
        Assert.Equal(0, result.IgnoredTelemetryEvents);
    }

    [Fact]
    public async Task GetAsync_is_deterministic_across_repeated_calls()
    {
        var sessions = new[] { Session("s-pi", "pi") };
        var events = new[]
        {
            Telemetry("s-pi", "message.completed", """
                { "data": { "message": { "usage": { "input": 1, "output": 2,
                  "cacheRead": 3, "cacheWrite": 4, "cost": { "total": 0.5 } } } } }
                """, 1),
        };
        await SeedAsync(sessions, events);

        await using var db = CreateContext();
        var first = await CreateService(db).GetAsync();
        var second = await CreateService(db).GetAsync();

        Assert.Equal(
            JsonSerializer.Serialize(first),
            JsonSerializer.Serialize(second));
    }
}
