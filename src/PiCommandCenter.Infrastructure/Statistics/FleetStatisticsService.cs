using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Application.Statistics;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Infrastructure.Persistence;

namespace PiCommandCenter.Infrastructure.Statistics;

/// <summary>
/// Aggregates fleet statistics from the <c>AgentSessions</c> projection plus the append-only
/// <c>SessionEvents</c> log, normalized per runtime (docs/research/agent-token-cost-statistics.md):
/// Pi sums final usage on <c>message.completed</c>/<c>compaction.completed</c>; Claude replaces from
/// each <c>result.completed</c> (preferring summed <c>modelUsage</c>, cost from <c>total_cost_usd</c>);
/// Antigravity replaces from the usage on each <c>turn.completed</c>/<c>turn.failed</c>/
/// <c>session.cancelled</c>; Muse replaces cumulative input/output and accumulates each reported
/// cache-read delta from <c>session.usage</c>. Known telemetry events with malformed, negative,
/// fractional, overflowing, or non-finite values are skipped whole and counted as ignored; unknown
/// event types are ignored silently. Cost is only the pre-existing runtime-reported client/catalog
/// estimate.
/// </summary>
public sealed class FleetStatisticsService(ControlPlaneDbContext db) : IFleetStatisticsService
{
    private static readonly string[] KnownTelemetryEventTypes =
    [
        "message.completed",
        "compaction.completed",
        "result.completed",
        "turn.completed",
        "turn.failed",
        "session.cancelled",
        "session.usage",
    ];

    public async Task<FleetStatisticsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var sessions = await db.AgentSessions
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var sessionsById = new Dictionary<string, AgentSessionRow>(StringComparer.Ordinal);
        foreach (var session in sessions)
        {
            sessionsById[session.Id] = session;
        }

        var accumulators = new Dictionary<string, SessionAccumulator>(StringComparer.Ordinal);
        var ignored = 0;
        DateTimeOffset? latestTelemetryAt = null;

        // Filter and project in SQL: only known telemetry events and the fields needed for
        // association, parsing, and the latest-observation timestamp are streamed.
        var eventRows = db.SessionEvents
            .AsNoTracking()
            .Where(e => e.SessionId != null && KnownTelemetryEventTypes.Contains(e.Type))
            .OrderBy(e => e.OccurredAtUtcTicks)
            .ThenBy(e => e.Sequence)
            .ThenBy(e => e.EventId)
            .Select(e => new
            {
                e.SessionId,
                e.Type,
                e.OccurredAtUtcTicks,
                e.PayloadJson,
            })
            .AsAsyncEnumerable();

        await foreach (var row in eventRows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var sessionId = row.SessionId;
            if (sessionId is null || !sessionsById.TryGetValue(sessionId, out var session))
            {
                continue;
            }

            if (!TryApply(session.Runtime, row.Type, row.PayloadJson, out var update))
            {
                continue;
            }

            if (update is null)
            {
                // Known telemetry event for this runtime with malformed values: skip whole.
                ignored++;
                continue;
            }

            var accumulator = GetAccumulator(accumulators, sessionId);
            if (!accumulator.TryCommit(update))
            {
                ignored++;
                continue;
            }

            var occurredAt = new DateTimeOffset(row.OccurredAtUtcTicks, TimeSpan.Zero);
            if (latestTelemetryAt is null || occurredAt > latestTelemetryAt)
            {
                latestTelemetryAt = occurredAt;
            }
        }

        var runtimeGroups = sessions
            .GroupBy(s => s.Runtime, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => BuildRuntimeRow(g.Key, g.ToList(), accumulators))
            .ToList();

        var fleet = BuildTotals(sessions, accumulators);
        return new FleetStatisticsDto(
            TrackedAgents: sessions.Count,
            ActiveAgents: sessions.Count(IsActive),
            AgentsWithReportedTokens: fleet.AgentsWithReportedTokens,
            AgentsWithEstimatedCost: fleet.AgentsWithEstimatedCost,
            Tokens: fleet.Tokens,
            EstimatedCostUsd: fleet.EstimatedCostUsd,
            IgnoredTelemetryEvents: ignored,
            LatestTelemetryAt: latestTelemetryAt,
            Runtimes: runtimeGroups,
            Providers: BuildProviderRows(sessions, accumulators));
    }

    private static RuntimeStatisticsDto BuildRuntimeRow(
        string runtime,
        IReadOnlyList<AgentSessionRow> sessions,
        IReadOnlyDictionary<string, SessionAccumulator> accumulators)
    {
        var totals = BuildTotals(sessions, accumulators);
        return new RuntimeStatisticsDto(
            Runtime: runtime,
            TrackedAgents: sessions.Count,
            ActiveAgents: sessions.Count(IsActive),
            AgentsWithReportedTokens: totals.AgentsWithReportedTokens,
            Tokens: totals.Tokens,
            EstimatedCostUsd: totals.EstimatedCostUsd);
    }

    /// <summary>
    /// Groups sessions by the provider from their canonical model selector.
    /// Unqualified or malformed selectors are omitted.
    /// </summary>
    private static IReadOnlyList<ProviderStatisticsDto> BuildProviderRows(
        IReadOnlyList<AgentSessionRow> sessions,
        IReadOnlyDictionary<string, SessionAccumulator> accumulators)
    {
        Dictionary<string, List<AgentSessionRow>>? groups = null;
        foreach (var session in sessions)
        {
            if (!AgentModelSelector.TryParse(session.Model, out var selector))
            {
                continue;
            }

            groups ??= new Dictionary<string, List<AgentSessionRow>>(StringComparer.Ordinal);
            if (!groups.TryGetValue(selector.Provider, out var bucket))
            {
                bucket = new List<AgentSessionRow>();
                groups[selector.Provider] = bucket;
            }

            bucket.Add(session);
        }

        if (groups is null)
        {
            return [];
        }

        return groups
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var totals = BuildTotals(g.Value, accumulators);
                return new ProviderStatisticsDto(
                    Provider: g.Key,
                    TrackedAgents: g.Value.Count,
                    ActiveAgents: g.Value.Count(IsActive),
                    AgentsWithReportedTokens: totals.AgentsWithReportedTokens,
                    Tokens: totals.Tokens,
                    EstimatedCostUsd: totals.EstimatedCostUsd);
            })
            .ToList();
    }


    /// <summary>
    /// Sums per-session accumulators into one totals row. Overflow while combining sessions
    /// fails closed and is sticky: once a series overflows it stays null for the remaining
    /// sessions, while unrelated series still report.
    /// </summary>
    private static (int AgentsWithReportedTokens, int AgentsWithEstimatedCost, TokenTotalsDto Tokens, decimal? EstimatedCostUsd)
        BuildTotals(
            IReadOnlyList<AgentSessionRow> sessions,
            IReadOnlyDictionary<string, SessionAccumulator> accumulators)
    {
        long? input = null, output = null, cacheRead = null, cacheWrite = null, thinking = null;
        var inputOverflowed = false;
        var outputOverflowed = false;
        var cacheReadOverflowed = false;
        var cacheWriteOverflowed = false;
        var thinkingOverflowed = false;
        decimal? cost = null;
        var costOverflowed = false;
        var withTokens = 0;
        var withCost = 0;

        foreach (var session in sessions)
        {
            if (!accumulators.TryGetValue(session.Id, out var accumulator))
            {
                continue;
            }

            var hasTokens = accumulator.HasAnyTokens;
            if (hasTokens)
            {
                withTokens++;
            }

            if (accumulator.CostUsd is { } sessionCost)
            {
                withCost++;
                AccumulateCost(ref cost, ref costOverflowed, sessionCost);
            }

            Accumulate(ref input, ref inputOverflowed, accumulator.Input);
            Accumulate(ref output, ref outputOverflowed, accumulator.Output);
            Accumulate(ref cacheRead, ref cacheReadOverflowed, accumulator.CacheRead);
            Accumulate(ref cacheWrite, ref cacheWriteOverflowed, accumulator.CacheWrite);
            Accumulate(ref thinking, ref thinkingOverflowed, accumulator.Thinking);
        }

        return (withTokens, withCost, new TokenTotalsDto(input, output, cacheRead, cacheWrite, thinking), cost);

        static void Accumulate(ref long? total, ref bool overflowed, long? value)
        {
            if (overflowed || value is null)
            {
                return;
            }

            try
            {
                total = checked((total ?? 0L) + value.Value);
            }
            catch (OverflowException)
            {
                overflowed = true;
                total = null;
            }
        }

        static void AccumulateCost(ref decimal? total, ref bool overflowed, decimal value)
        {
            if (overflowed)
            {
                return;
            }

            try
            {
                total = checked((total ?? 0m) + value);
            }
            catch (OverflowException)
            {
                overflowed = true;
                total = null;
            }
        }
    }

    private static long? Add(long? total, long? value)
        => value is null ? total : checked((total ?? 0L) + value.Value);

    private static bool IsActive(AgentSessionRow row)
        => row.EndedAtUtcTicks is null
           && !string.Equals(row.Liveness, nameof(AgentLiveness.Exited), StringComparison.Ordinal)
           && !string.Equals(row.WorkState, nameof(AgentWorkState.Completed), StringComparison.Ordinal)
           && !string.Equals(row.WorkState, nameof(AgentWorkState.Failed), StringComparison.Ordinal)
           && !string.Equals(row.WorkState, nameof(AgentWorkState.Cancelled), StringComparison.Ordinal);

    private static SessionAccumulator GetAccumulator(
        Dictionary<string, SessionAccumulator> accumulators,
        string sessionId)
    {
        if (!accumulators.TryGetValue(sessionId, out var accumulator))
        {
            accumulator = new SessionAccumulator();
            accumulators[sessionId] = accumulator;
        }

        return accumulator;
    }

    /// <summary>
    /// Parses one event into a pending per-session update. Returns false for unknown runtimes,
    /// unknown event types, and events that carry no telemetry (silently skipped). Returns true
    /// with a null update for known telemetry events whose values are malformed.
    /// </summary>
    private static bool TryApply(
        string runtime,
        string eventType,
        string payloadJson,
        out PendingUpdate? update)
    {
        update = null;
        switch (runtime)
        {
            case AgentRuntimeKinds.Pi when eventType is "message.completed" or "compaction.completed":
                return TryParsePi(eventType, payloadJson, out update);
            case AgentRuntimeKinds.ClaudeCode when eventType is "result.completed":
                return TryParseClaude(payloadJson, out update);
            case AgentRuntimeKinds.Antigravity
                when eventType is "turn.completed" or "turn.failed" or "session.cancelled":
                return TryParseAntigravity(payloadJson, out update);
            case AgentRuntimeKinds.Muse when eventType is "session.usage":
                return TryParseMuse(payloadJson, out update);
            default:
                return false;
        }
    }

    private static bool TryParsePi(string eventType, string payloadJson, out PendingUpdate? update)
    {
        update = null;
        if (!TryGetRoot(payloadJson, out var root))
        {
            // Known telemetry event with an unparseable payload.
            return true;
        }

        var data = UnwrapData(root);
        var isMessage = eventType == "message.completed";
        var containerName = isMessage ? "message" : "result";

        if (!data.TryGetProperty(containerName, out var container)
            || container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("usage", out var usage))
        {
            // Finals without usage (user messages, aborted compactions) carry no telemetry.
            return false;
        }

        if (usage.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (!TryReadToken(usage, "input", out var input)
            || !TryReadToken(usage, "output", out var output)
            || !TryReadToken(usage, "cacheRead", out var cacheRead)
            || !TryReadToken(usage, "cacheWrite", out var cacheWrite)
            || !TryReadOptionalToken(usage, "reasoning", out var reasoning))
        {
            return true;
        }

        decimal? cost = null;
        if (usage.TryGetProperty("cost", out var costElement))
        {
            if (costElement.ValueKind != JsonValueKind.Object
                || !costElement.TryGetProperty("total", out var totalElement)
                || !TryReadCost(totalElement, out var total))
            {
                return true;
            }

            cost = total;
        }

        update = PendingUpdate.Add(input, output, cacheRead, cacheWrite, reasoning, cost);
        return true;
    }

    private static bool TryParseClaude(string payloadJson, out PendingUpdate? update)
    {
        update = null;
        if (!TryGetRoot(payloadJson, out var root))
        {
            return true;
        }

        var data = UnwrapData(root);

        long? input, output, cacheRead, cacheWrite, thinking;
        if (data.TryGetProperty("modelUsage", out var modelUsage))
        {
            // Present modelUsage is authoritative: a malformed or empty block is a malformed
            // event and must never fall back to the stale top-level usage.
            if (modelUsage.ValueKind != JsonValueKind.Object
                || !modelUsage.EnumerateObject().Any())
            {
                return true;
            }

            input = output = cacheRead = cacheWrite = thinking = null;
            try
            {
                foreach (var model in modelUsage.EnumerateObject())
                {
                    if (model.Value.ValueKind != JsonValueKind.Object
                        || !TryReadToken(model.Value, "inputTokens", out var modelInput)
                        || !TryReadToken(model.Value, "outputTokens", out var modelOutput)
                        || !TryReadToken(model.Value, "cacheReadInputTokens", out var modelCacheRead)
                        || !TryReadToken(model.Value, "cacheCreationInputTokens", out var modelCacheWrite)
                        || !TryReadOptionalToken(model.Value, "thinkingTokens", out var modelThinking))
                    {
                        return true;
                    }

                    input = Add(input, modelInput);
                    output = Add(output, modelOutput);
                    cacheRead = Add(cacheRead, modelCacheRead);
                    cacheWrite = Add(cacheWrite, modelCacheWrite);
                    thinking = Add(thinking, modelThinking);
                }
            }
            catch (OverflowException)
            {
                // Cross-model sums exceed Int64: skip the event whole.
                return true;
            }
        }
        else if (data.TryGetProperty("usage", out var usage)
                 && usage.ValueKind == JsonValueKind.Object)
        {
            if (!TryReadToken(usage, "input_tokens", out var usageInput)
                || !TryReadToken(usage, "output_tokens", out var usageOutput)
                || !TryReadOptionalToken(usage, "cache_read_input_tokens", out var usageCacheRead)
                || !TryReadOptionalToken(usage, "cache_creation_input_tokens", out var usageCacheWrite))
            {
                return true;
            }

            input = usageInput;
            output = usageOutput;
            cacheRead = usageCacheRead;
            cacheWrite = usageCacheWrite;
            thinking = null;
        }
        else
        {
            // A result event with no usage at all is a malformed telemetry event.
            return true;
        }

        decimal? cost = null;
        if (data.TryGetProperty("total_cost_usd", out var costElement)
            && costElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadCost(costElement, out var parsedCost))
            {
                return true;
            }

            cost = parsedCost;
        }

        update = PendingUpdate.Replace(input!.Value, output!.Value, cacheRead, cacheWrite, thinking, cost);
        return true;
    }

    private static bool TryParseAntigravity(string payloadJson, out PendingUpdate? update)
    {
        update = null;
        if (!TryGetRoot(payloadJson, out var root))
        {
            return true;
        }

        var data = UnwrapData(root);
        if (!data.TryGetProperty("usage", out var usage))
        {
            // Turns without reported usage carry no telemetry.
            return false;
        }

        if (usage.ValueKind != JsonValueKind.Object
            || !TryReadToken(usage, "input_tokens", out var input)
            || !TryReadToken(usage, "output_tokens", out var output)
            || !TryReadOptionalToken(usage, "cache_read_tokens", out var cacheRead)
            || !TryReadOptionalToken(usage, "thinking_tokens", out var thinking))
        {
            return true;
        }

        update = PendingUpdate.Replace(input, output, cacheRead, null, thinking, null);
        return true;
    }

    private static bool TryParseMuse(string payloadJson, out PendingUpdate? update)
    {
        update = null;
        if (!TryGetRoot(payloadJson, out var root))
        {
            return true;
        }

        var data = UnwrapData(root);
        if (!data.TryGetProperty("cumulative", out var cumulative)
            || cumulative.ValueKind != JsonValueKind.Object
            || !TryReadToken(cumulative, "promptTokens", out var input)
            || !TryReadToken(cumulative, "outputTokens", out var output))
        {
            // The notification's required cumulative block is absent or malformed.
            return true;
        }

        long? cacheRead = null;
        if (data.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty("cacheReadTokens", out var cacheElement)
            && cacheElement.ValueKind != JsonValueKind.Null)
        {
            if (!TryReadToken(usage, "cacheReadTokens", out var parsedCacheRead))
            {
                return true;
            }

            cacheRead = parsedCacheRead;
        }

        update = PendingUpdate.ReplaceWithAdditiveCacheRead(input, output, cacheRead);
        return true;
    }

    private static bool TryGetRoot(string payloadJson, out JsonElement root)
    {
        root = default;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement UnwrapData(JsonElement root)
        => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            ? data
            : root;

    /// <summary>Reads a required token counter: an integral JSON number, zero or positive, fitting Int64.</summary>
    private static bool TryReadToken(JsonElement parent, string name, out long value)
    {
        value = 0;
        return parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value)
            && value >= 0;
    }

    /// <summary>Reads an optional token counter; JSON null or absence yields null.</summary>
    private static bool TryReadOptionalToken(JsonElement parent, string name, out long? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element)
            || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out var parsed)
            && parsed >= 0)
        {
            value = parsed;
            return true;
        }

        return false;
    }

    /// <summary>Reads a finite, non-negative cost estimate.</summary>
    private static bool TryReadCost(JsonElement element, out decimal value)
    {
        value = 0m;
        return element.ValueKind == JsonValueKind.Number
            && element.TryGetDecimal(out value)
            && value >= 0m;
    }

    /// <summary>
    /// How one parsed event affects a single accumulator field: <see cref="Add"/> accumulates
    /// (a null value preserves the prior total), <see cref="Replace"/> overwrites (a null value
    /// erases), <see cref="Preserve"/> leaves the field untouched.
    /// </summary>
    private enum UpdateMode
    {
        Add,
        Replace,
        Preserve,
    }

    private readonly record struct FieldUpdate<T>(UpdateMode Mode, T Value)
    {
        public static FieldUpdate<T> Add(T value) => new(UpdateMode.Add, value);

        public static FieldUpdate<T> Replace(T value) => new(UpdateMode.Replace, value);

        public static FieldUpdate<T> Preserve => new(UpdateMode.Preserve, default!);
    }

    private sealed record PendingUpdate(
        FieldUpdate<long?> Input,
        FieldUpdate<long?> Output,
        FieldUpdate<long?> CacheRead,
        FieldUpdate<long?> CacheWrite,
        FieldUpdate<long?> Thinking,
        FieldUpdate<decimal?> Cost)
    {
        public static PendingUpdate Add(
            long input,
            long output,
            long? cacheRead,
            long? cacheWrite,
            long? thinking,
            decimal? cost)
            => new(
                FieldUpdate<long?>.Add(input),
                FieldUpdate<long?>.Add(output),
                FieldUpdate<long?>.Add(cacheRead),
                FieldUpdate<long?>.Add(cacheWrite),
                FieldUpdate<long?>.Add(thinking),
                FieldUpdate<decimal?>.Add(cost));

        public static PendingUpdate Replace(
            long input,
            long output,
            long? cacheRead,
            long? cacheWrite,
            long? thinking,
            decimal? cost)
            => new(
                FieldUpdate<long?>.Replace(input),
                FieldUpdate<long?>.Replace(output),
                FieldUpdate<long?>.Replace(cacheRead),
                FieldUpdate<long?>.Replace(cacheWrite),
                FieldUpdate<long?>.Replace(thinking),
                FieldUpdate<decimal?>.Replace(cost));

        public static PendingUpdate ReplaceWithAdditiveCacheRead(
            long input,
            long output,
            long? cacheRead)
            => new(
                FieldUpdate<long?>.Replace(input),
                FieldUpdate<long?>.Replace(output),
                cacheRead is null
                    ? FieldUpdate<long?>.Preserve
                    : FieldUpdate<long?>.Add(cacheRead),
                FieldUpdate<long?>.Replace(null),
                FieldUpdate<long?>.Replace(null),
                FieldUpdate<decimal?>.Replace(null));
    }

    private sealed class SessionAccumulator
    {
        public long? Input { get; private set; }
        public long? Output { get; private set; }
        public long? CacheRead { get; private set; }
        public long? CacheWrite { get; private set; }
        public long? Thinking { get; private set; }
        public decimal? CostUsd { get; private set; }

        public bool HasAnyTokens
            => Input is not null || Output is not null || CacheRead is not null
               || CacheWrite is not null || Thinking is not null;

        /// <summary>
        /// Commits one parsed event. Arithmetic is checked; on overflow nothing is applied
        /// and the caller counts the event as ignored.
        /// </summary>
        public bool TryCommit(PendingUpdate update)
        {
            try
            {
                var input = Apply(Input, update.Input);
                var output = Apply(Output, update.Output);
                var cacheRead = Apply(CacheRead, update.CacheRead);
                var cacheWrite = Apply(CacheWrite, update.CacheWrite);
                var thinking = Apply(Thinking, update.Thinking);
                var cost = Apply(CostUsd, update.Cost);

                Input = input;
                Output = output;
                CacheRead = cacheRead;
                CacheWrite = cacheWrite;
                Thinking = thinking;
                CostUsd = cost;
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static long? Apply(long? current, FieldUpdate<long?> update)
            => update.Mode switch
            {
                UpdateMode.Add => Add(current, update.Value),
                UpdateMode.Replace => update.Value,
                UpdateMode.Preserve => current,
                _ => throw new ArgumentOutOfRangeException(nameof(update)),
            };

        private static decimal? Apply(decimal? current, FieldUpdate<decimal?> update)
            => update.Mode switch
            {
                UpdateMode.Add => AddCost(current, update.Value),
                UpdateMode.Replace => update.Value,
                UpdateMode.Preserve => current,
                _ => throw new ArgumentOutOfRangeException(nameof(update)),
            };

        private static long? Add(long? current, long? value)
            => value is null ? current : checked((current ?? 0L) + value.Value);

        private static decimal? AddCost(decimal? current, decimal? value)
            => value is null ? current : checked((current ?? 0m) + value.Value);
    }
}
