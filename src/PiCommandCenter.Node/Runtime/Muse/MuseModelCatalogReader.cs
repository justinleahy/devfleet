using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;

namespace PiCommandCenter.Node.Runtime.Muse;

/// <summary>
/// Launches a separate read-only Muse host, handshakes, calls <c>model/list {}</c> only, and
/// preserves the valid native model ids before combining them with DevFleet's curated discovery
/// aliases. All ids are prefixed with <c>muse/</c>, deduplicated, sorted, and the host is
/// terminated. Errors are stable sentences without raw provider output; a login failure yields
/// the local-login guidance.
/// </summary>
public sealed class MuseModelCatalogReader : IMuseModelCatalogReader
{
    private static readonly ImmutableArray<string> CuratedModelIds =
    [
        "muse-spark-1.3",
        "muse-spark-1.3-contributor",
        "muse-spark-1.2",
        "muse-spark-1.2-contributor",
    ];

    private static readonly Action<ILogger, Exception?> LogDiscoveryStartFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(1, nameof(LogDiscoveryStartFailed)),
            "Muse model discovery could not start the host executable.");

    private static readonly Action<ILogger, Exception?> LogDiscoveryTerminationFailed =
        LoggerMessage.Define(
            LogLevel.Debug,
            new EventId(2, nameof(LogDiscoveryTerminationFailed)),
            "Muse discovery host termination failed.");

    private readonly MuseCodeOptions _options;
    private readonly IMuseProcessFactory _processFactory;
    private readonly ILogger<MuseModelCatalogReader> _logger;

    public MuseModelCatalogReader(
        IOptions<MuseCodeOptions> options,
        IMuseProcessFactory processFactory,
        ILogger<MuseModelCatalogReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options.Value;
    }

    public async Task<MuseModelCatalogResult> ReadAsync(CancellationToken cancellationToken)
    {
        IMuseProcess process;
        try
        {
            process = _processFactory.Start(new MuseProcessStartInfo(
                _options.Executable,
                MuseProtocol.LaunchArguments,
                Path.GetTempPath()));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LogDiscoveryStartFailed(_logger, ex);
            return MuseModelCatalogResult.Failure("Muse model discovery could not start the muse executable.");
        }

        var client = new MuseHostClient(
            process,
            _options.MaxLineBytes,
            _options.MaxStderrLines,
            static (_, _) => { },
            _logger);
        await using (client.ConfigureAwait(false))
        {
            client.Start();
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(_options.StartTimeoutSeconds));
            try
            {
                await MuseProtocol.HandshakeAsync(client, "discovery", Timeout.InfiniteTimeSpan, _logger, bound.Token)
                    .ConfigureAwait(false);
                var result = await client.RequestAsync("model/list", new { }, Timeout.InfiniteTimeSpan, bound.Token)
                    .ConfigureAwait(false);
                return Parse(result);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return MuseModelCatalogResult.Failure("Muse model discovery timed out.");
            }
            catch (MuseProtocolException ex)
            {
                return MuseModelCatalogResult.Failure(DescribeFailure(ex.Message, client.StderrTail));
            }
            catch (NotSupportedException ex)
            {
                return MuseModelCatalogResult.Failure(ex.Message);
            }
            finally
            {
                try
                {
                    await client.TerminateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDiscoveryTerminationFailed(_logger, ex);
                }
            }
        }
    }

    internal static MuseModelCatalogResult Parse(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
        {
            return MuseModelCatalogResult.Failure("Muse model discovery returned no model list.");
        }

        var selectors = new SortedSet<string>(
            CuratedModelIds.Select(modelId => AgentModelSelector.Muse + "/" + modelId),
            StringComparer.Ordinal);
        var nativeSelectors = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in models.EnumerateArray())
        {
            var modelId = MuseProtocol.GetString(entry, "modelId")?.Trim();
            if (string.IsNullOrEmpty(modelId))
            {
                continue;
            }

            var selector = AgentModelSelector.Muse + "/" + modelId;
            if (AgentModelSelector.TryParse(selector, out var parsed))
            {
                selectors.Add(parsed.Value);
                nativeSelectors.Add(parsed.Value);
            }
        }

        return new MuseModelCatalogResult(selectors.ToArray(), nativeSelectors.ToArray(), null);
    }

    private static string DescribeFailure(string detail, IReadOnlyList<string> stderrTail)
    {
        var tail = string.Join(" | ", stderrTail.TakeLast(3));
        if (MuseCodeRuntimeAdapter.IsAuthFailure(detail) || MuseCodeRuntimeAdapter.IsAuthFailure(tail))
        {
            return "Muse model discovery requires local login. " + MuseCodeRuntimeAdapter.LoginReason;
        }

        return "Muse model discovery failed: " + detail;
    }
}
