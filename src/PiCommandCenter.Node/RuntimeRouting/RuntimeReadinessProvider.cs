using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.RuntimeRouting;

/// <summary>
/// Projects cached provider-native readiness observations and live node capacity into bounded
/// execution-status snapshots. A routing revision is never paired with evidence captured for a
/// different configuration.
/// </summary>
public sealed partial class RuntimeReadinessProvider : BackgroundService, IRuntimeReadinessProvider
{
    private readonly int _maxConcurrentRequests;
    private readonly string _rootModel;
    private readonly TimeSpan _refreshInterval;
    private readonly INodeRuntimeRoutingStore _routing;
    private readonly IRuntimeReadinessProbe _probe;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuntimeReadinessProvider> _logger;
    private readonly Verification.IVerificationPolicyCatalog _verificationPolicies;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private ReadinessSnapshot? _snapshot;

    public RuntimeReadinessProvider(
        IOptions<NodeOptions> options,
        IOptions<PiWorkerOptions> worker,
        INodeRuntimeRoutingStore routing,
        IRuntimeReadinessProbe probe,
        TimeProvider timeProvider,
        ILogger<RuntimeReadinessProvider> logger,
        Verification.IVerificationPolicyCatalog verificationPolicies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(verificationPolicies);

        _maxConcurrentRequests = options.Value.MaxConcurrentRequests;
        _rootModel = AgentModelSelector.Parse(worker.Value.Model).Value;
        _refreshInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.HeartbeatSeconds));
        _routing = routing;
        _probe = probe;
        _timeProvider = timeProvider;
        _logger = logger;
        _verificationPolicies = verificationPolicies;
    }

    public NodeExecutionStatusMessage Capture(IReadOnlyList<Guid> activeAssignmentIds)
    {
        ArgumentNullException.ThrowIfNull(activeAssignmentIds);

        var assignments = activeAssignmentIds.Distinct().ToArray();
        var routes = Normalize(_routing.Current.RoleRoutes, _rootModel);
        var routingRevision = ComputeRoutingRevision(routes);
        var capturedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var snapshot = Volatile.Read(ref _snapshot);
        var hasCurrentEvidence = string.Equals(
            snapshot?.RoutingRevision,
            routingRevision,
            StringComparison.Ordinal);
        var observedAt = hasCurrentEvidence ? snapshot!.ObservedAt : capturedAt;
        var evidenceSource = hasCurrentEvidence
            ? RuntimeReadinessEvidenceSources.RuntimeAdapterProbe
            : RuntimeReadinessEvidenceSources.UnsupportedNativeObservation;
        var observations = new List<RuntimeRouteReadinessMessage>(
            routes.Sum(route => route.Candidates.Count));

        foreach (var route in routes)
        {
            foreach (var candidate in route.Candidates)
            {
                var readiness = hasCurrentEvidence
                    && snapshot!.ReadinessByModel.TryGetValue(candidate, out var observed)
                        ? observed
                        : RuntimeReadinessStatuses.Unknown;
                observations.Add(new RuntimeRouteReadinessMessage(
                    route.Role,
                    candidate,
                    readiness,
                    evidenceSource,
                    observedAt,
                    routingRevision));
            }
        }

        var availableSlots = assignments.Length >= _maxConcurrentRequests
            ? 0
            : _maxConcurrentRequests - assignments.Length;
        return new NodeExecutionStatusMessage(
            capturedAt,
            availableSlots,
            assignments,
            routingRevision,
            observations,
            _verificationPolicies.Capture());
    }

    internal async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var routes = Normalize(_routing.Current.RoleRoutes, _rootModel);
            var selectors = routes
                .SelectMany(route => route.Candidates)
                .Distinct(StringComparer.Ordinal)
                .Select(AgentModelSelector.Parse)
                .ToArray();
            IReadOnlyDictionary<string, string> readiness;
            try
            {
                readiness = await _probe.ObserveAsync(selectors, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogRefreshFailure(_logger, ex.GetType().Name);
                readiness = selectors.ToDictionary(
                    selector => selector.Value,
                    _ => RuntimeReadinessStatuses.Unknown,
                    StringComparer.Ordinal);
            }

            Volatile.Write(ref _snapshot, new ReadinessSnapshot(
                ComputeRoutingRevision(routes),
                _timeProvider.GetUtcNow().ToUniversalTime(),
                readiness));
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_refreshInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    public override void Dispose()
    {
        _refreshGate.Dispose();
        base.Dispose();
    }

    private static IReadOnlyList<NormalizedRoleRoute> Normalize(
        IReadOnlyList<RuntimeRoleRouteMessage> routes,
        string rootModel)
    {
        var normalized = routes
            .Select(route => new NormalizedRoleRoute(
                route.Role.Trim(),
                route.Candidates
                    .Select(candidate => AgentModelSelector.Parse(candidate.Model).Value)
                    .ToArray()))
            .ToList();
        var rootIndex = normalized.FindIndex(route =>
            string.Equals(route.Role, "root", StringComparison.Ordinal));
        if (rootIndex < 0)
        {
            normalized.Add(new NormalizedRoleRoute("root", [rootModel]));
        }
        else if (!normalized[rootIndex].Candidates.Contains(rootModel, StringComparer.Ordinal))
        {
            normalized[rootIndex] = normalized[rootIndex] with
            {
                Candidates = [.. normalized[rootIndex].Candidates, rootModel],
            };
        }

        return normalized
            .OrderBy(route => route.Role, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComputeRoutingRevision(IReadOnlyList<NormalizedRoleRoute> routes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var route in routes)
            {
                writer.WriteStartArray();
                writer.WriteStringValue(route.Role);
                writer.WriteStartArray();
                foreach (var candidate in route.Candidates)
                {
                    writer.WriteStringValue(candidate);
                }
                writer.WriteEndArray();
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan));
    }
    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Runtime readiness refresh failed with {ExceptionType}.")]
    private static partial void LogRefreshFailure(ILogger logger, string exceptionType);


    private sealed record ReadinessSnapshot(
        string RoutingRevision,
        DateTimeOffset ObservedAt,
        IReadOnlyDictionary<string, string> ReadinessByModel);

    private sealed record NormalizedRoleRoute(string Role, IReadOnlyList<string> Candidates);
}
