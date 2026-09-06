namespace PiCommandCenter.Application.Statistics;

/// <summary>
/// Read-only fleet statistics over persisted session projections and append-only session
/// events: agent counts, normalized token counters, and runtime-reported client cost
/// estimates. Subscription quota (<c>/usage</c>) is explicitly out of scope.
/// </summary>
public interface IFleetStatisticsService
{
    /// <summary>
    /// Returns deterministic all-history totals plus a per-runtime breakdown ordered
    /// ordinally by runtime identifier. Missing counters stay null; zero appears only
    /// when a runtime explicitly reported it.
    /// </summary>
    Task<FleetStatisticsDto> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Fleet-wide totals. <see cref="EstimatedCostUsd"/> sums only pre-existing
/// runtime-reported client/catalog estimates; it is never computed from token counts.
/// </summary>
public sealed record FleetStatisticsDto(
    int TrackedAgents,
    int ActiveAgents,
    int AgentsWithReportedTokens,
    int AgentsWithEstimatedCost,
    TokenTotalsDto Tokens,
    decimal? EstimatedCostUsd,
    int IgnoredTelemetryEvents,
    DateTimeOffset? LatestTelemetryAt,
    IReadOnlyList<RuntimeStatisticsDto> Runtimes,
    IReadOnlyList<ProviderStatisticsDto> Providers);

/// <summary>Nullable token series. Null means no runtime ever reported the series.</summary>
public sealed record TokenTotalsDto(
    long? Input,
    long? Output,
    long? CacheRead,
    long? CacheWrite,
    long? Thinking);

/// <summary>Per-runtime breakdown grouped by the session runtime identifier.</summary>
public sealed record RuntimeStatisticsDto(
    string Runtime,
    int TrackedAgents,
    int ActiveAgents,
    int AgentsWithReportedTokens,
    TokenTotalsDto Tokens,
    decimal? EstimatedCostUsd);

/// <summary>
/// Per-provider breakdown grouped by the canonical model selector prefix before
/// <c>/</c>. Unqualified or empty prefixes are omitted, not labeled.
/// </summary>
public sealed record ProviderStatisticsDto(
    string Provider,
    int TrackedAgents,
    int ActiveAgents,
    int AgentsWithReportedTokens,
    TokenTotalsDto Tokens,
    decimal? EstimatedCostUsd);
