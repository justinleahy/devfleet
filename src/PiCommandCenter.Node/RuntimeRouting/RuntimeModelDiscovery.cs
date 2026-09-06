using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Muse;

namespace PiCommandCenter.Node.RuntimeRouting;

public interface IRuntimeModelDiscovery
{
    Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverAsync(
        CancellationToken cancellationToken = default);
}

public sealed record ModelCommandResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool Truncated);

public interface IRuntimeModelCommandRunner
{
    Task<ModelCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken);
}

public sealed class RuntimeModelCommandRunner : IRuntimeModelCommandRunner
{
    private const int MaxCharacters = 512 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<ModelCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ModelCommandResult(null, string.Empty, "The process did not start.", false, false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ModelCommandResult(null, string.Empty, ex.Message, false, false);
        }
        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderrTask = ReadBoundedAsync(process.StandardError, timeout.Token);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            timedOut = true;
            TryKill(process);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
        if (timedOut)
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        var stdout = await CompleteReadAsync(stdoutTask).ConfigureAwait(false);
        var stderr = await CompleteReadAsync(stderrTask).ConfigureAwait(false);
        return new ModelCommandResult(
            process.HasExited ? process.ExitCode : null,
            stdout.Text,
            stderr.Text,
            timedOut,
            stdout.Truncated || stderr.Truncated);
    }

    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var text = new StringBuilder();
        var truncated = false;
        while (true)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            var remaining = MaxCharacters - text.Length;
            if (remaining > 0)
            {
                text.Append(buffer, 0, Math.Min(read, remaining));
            }
            truncated |= read > remaining;
        }
        return (text.ToString(), truncated);
    }

    private static async Task<(string Text, bool Truncated)> CompleteReadAsync(
        Task<(string Text, bool Truncated)> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return (string.Empty, false);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

/// <summary>
/// Keeps the latest Pi, Antigravity, and Muse discovery results in memory. Collection runs once
/// immediately at node startup and then on a non-overlapping five-minute cadence; readers never
/// invoke providers and wait only for the first completed refresh. Claude is excluded from the
/// snapshot because its catalog embeds live configured route selectors.
/// Every reported <see cref="RuntimeModelMessage.Id"/> is a canonical selector under the
/// catalog's <see cref="RuntimeModelCatalogMessage.Provider"/>, so it can be saved as-is.
/// </summary>
public sealed partial class RuntimeModelDiscovery : BackgroundService, IRuntimeModelDiscovery
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Claude Code cannot export the authenticated /model picker. Keep these stable aliases in
    // sync with https://code.claude.com/docs/en/model-config when Anthropic adds an option.
    private static readonly RuntimeModelMessage[] ClaudeCodeModels =
    [
        new("claude-code/fable-5-1", "Fable 5.1", AgentModelSelector.ClaudeCode),
        new("claude-code/sonnet", "Sonnet (latest)", AgentModelSelector.ClaudeCode),
        new("claude-code/opus", "Opus (latest)", AgentModelSelector.ClaudeCode),
        new("claude-code/haiku", "Haiku (latest)", AgentModelSelector.ClaudeCode),
    ];

    internal static bool IsMaintainedClaudeCodeModel(AgentModelSelector selector)
        => selector.Provider == AgentModelSelector.ClaudeCode
           && ClaudeCodeModels.Any(model =>
               string.Equals(model.Id, selector.Value, StringComparison.Ordinal));

    private readonly PiWorkerOptions _pi;
    private readonly AntigravityOptions _antigravity;
    private readonly INodeRuntimeRoutingStore _routes;
    private readonly IRuntimeModelCommandRunner _runner;
    private readonly IMuseModelCatalogReader _muse;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<RuntimeModelDiscovery> _logger;
    private readonly TaskCompletionSource<ExternalCatalogs> _initial =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ExternalCatalogs? _current;

    public RuntimeModelDiscovery(
        IOptions<PiWorkerOptions> pi,
        IOptions<AntigravityOptions> antigravity,
        INodeRuntimeRoutingStore routes,
        IRuntimeModelCommandRunner runner,
        IMuseModelCatalogReader muse,
        ILogger<RuntimeModelDiscovery> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _pi = pi.Value;
        _antigravity = antigravity.Value;
        _routes = routes;
        _runner = runner;
        _muse = muse;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Cached Pi/Antigravity/Muse discovery results, including provider-level error catalogs.
    /// Claude is excluded because its catalog embeds live configured route selectors.
    /// </summary>
    private sealed record ExternalCatalogs(
        IReadOnlyList<RuntimeModelCatalogMessage> Pi,
        RuntimeModelCatalogMessage Antigravity,
        RuntimeModelCatalogMessage Muse);

    /// <summary>
    /// Serves the latest completed external wave; readers never run discovery processes. Before
    /// the first completed refresh the read waits for it and honors caller cancellation. Claude
    /// aliases and configured selectors are recomputed from live routing on every call.
    /// </summary>
    public async Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var external = Volatile.Read(ref _current)
            ?? await _initial.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return [.. external.Pi, ClaudeCatalog(), external.Antigravity, external.Muse];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, _timeProvider);

        await RefreshAsync(stoppingToken).ConfigureAwait(false);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs one concurrent Pi/Antigravity/Muse discovery wave and atomically publishes the
    /// completed result. A failed refresh preserves the last completed snapshot.
    /// </summary>
    private async Task RefreshAsync(CancellationToken stoppingToken)
    {
        try
        {
            var pi = DiscoverPiAsync(stoppingToken);
            var antigravity = DiscoverAntigravityAsync(stoppingToken);
            var muse = DiscoverMuseAsync(stoppingToken);
            await Task.WhenAll(pi, antigravity, muse).ConfigureAwait(false);
            var snapshot = new ExternalCatalogs(pi.Result, antigravity.Result, muse.Result);
            Volatile.Write(ref _current, snapshot);
            _initial.TrySetResult(snapshot);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogRefreshFailure(_logger, ex.GetType().Name);
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Warning,
        Message = "Runtime model catalog refresh failed with {ExceptionType}.")]
    private static partial void LogRefreshFailure(ILogger logger, string exceptionType);

    /// <summary>
    /// Runs the worker's catalog script, which reports every authenticated Pi model using the
    /// canonical flat <c>&lt;provider&gt;/&lt;model&gt;</c> selector encoding, and returns one
    /// catalog per authenticated Pi provider. A discovery process failure degrades to a single
    /// <c>codex</c> error catalog.
    /// </summary>
    private async Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverPiAsync(CancellationToken cancellationToken)
    {
        var script = Path.Combine(Path.GetDirectoryName(_pi.WorkerPath)!, "modelCatalog.ts");
        var result = await _runner.RunAsync(
            _pi.NodeExecutable, [script], _pi.AgentDataDirectory, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return [Error(AgentModelSelector.Codex, "Pi model discovery timed out.")];
        }
        if (result.ExitCode != 0 || result.Truncated)
        {
            return [Error(AgentModelSelector.Codex, Failure("Pi model discovery failed", result))];
        }
        try
        {
            var models = JsonSerializer.Deserialize<List<RuntimeModelMessage>>(
                result.StandardOutput, JsonOptions) ?? [];
            return models
                .Where(model => AgentModelSelector.TryParse(model.Id, out var selector) && selector.UsesPiRuntime)
                .GroupBy(
                    model => AgentModelSelector.Parse(model.Id).Provider,
                    StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => Catalog(group.Key, group))
                .ToArray();
        }
        catch (JsonException ex)
        {
            return [Error(AgentModelSelector.Codex, $"Pi model discovery returned invalid JSON: {ex.Message}")];
        }
    }

    /// <summary>Lists <c>agy models</c> slugs as <c>antigravity/&lt;slug&gt;</c>.</summary>
    private async Task<RuntimeModelCatalogMessage> DiscoverAntigravityAsync(CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            _antigravity.Executable, ["models"], _pi.AgentDataDirectory, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Error(AgentModelSelector.Antigravity, "Antigravity model discovery timed out.");
        }
        if (result.ExitCode != 0 || result.Truncated)
        {
            return Error(AgentModelSelector.Antigravity, Failure("Antigravity model discovery failed", result));
        }
        var models = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts[0].Length > 0)
            .Select(parts => new RuntimeModelMessage(
                AgentModelSelector.Antigravity + "/" + parts[0],
                parts.Length == 2 ? parts[1] : parts[0],
                AgentModelSelector.Antigravity));
        return Catalog(AgentModelSelector.Antigravity, models);
    }

    /// <summary>
    /// Reads the local Muse host's model list, which the reader already reports as canonical
    /// <c>muse/&lt;id&gt;</c> selectors. Fails closed: a reader error is surfaced as-is, and a
    /// read that yields no usable selector is an error rather than an empty catalog.
    /// </summary>
    private async Task<RuntimeModelCatalogMessage> DiscoverMuseAsync(CancellationToken cancellationToken)
    {
        var result = await _muse.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return Error(AgentModelSelector.Muse, result.Error);
        }
        var models = new List<RuntimeModelMessage>(result.Models.Count);
        foreach (var id in result.Models)
        {
            if (AgentModelSelector.TryParse(id, out var selector))
            {
                models.Add(new RuntimeModelMessage(selector.Value, selector.Value, AgentModelSelector.Muse));
            }
        }
        var catalog = Catalog(AgentModelSelector.Muse, models);
        return catalog.Models.Count == 0
            ? Error(AgentModelSelector.Muse, "Muse model discovery returned no models.")
            : catalog;
    }

    /// <summary>
    /// Returns DevFleet's maintained Claude Code aliases plus any full selectors already present
    /// in the role routes. Claude Code has no supported way to export its authenticated picker.
    /// </summary>
    private RuntimeModelCatalogMessage ClaudeCatalog()
    {
        var configured = _routes.Current.RoleRoutes
            .SelectMany(route => route.Candidates)
            .Where(candidate => AgentModelSelector.TryParse(candidate.Model, out var selector)
                && selector.Provider == AgentModelSelector.ClaudeCode)
            .Select(candidate => new RuntimeModelMessage(
                candidate.Model, candidate.Model, AgentModelSelector.ClaudeCode));
        return Catalog(
            AgentModelSelector.ClaudeCode,
            ClaudeCodeModels.Concat(configured));
    }

    /// <summary>
    /// Keeps only ids that parse as canonical selectors under <paramref name="provider"/>, so a
    /// catalog can never offer a selector the registry would refuse.
    /// </summary>
    private static RuntimeModelCatalogMessage Catalog(
        string provider,
        IEnumerable<RuntimeModelMessage> models,
        string? error = null)
    {
        var canonical = new Dictionary<string, RuntimeModelMessage>(StringComparer.Ordinal);
        foreach (var model in models)
        {
            if (AgentModelSelector.TryParse(model.Id, out var selector) && selector.Provider == provider)
            {
                canonical.TryAdd(selector.Value, model with { Id = selector.Value });
            }
        }
        return new RuntimeModelCatalogMessage(
            provider,
            canonical.Values.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(),
            error);
    }

    private static RuntimeModelCatalogMessage Error(string provider, string message)
        => new(provider, [], message);

    private static string Failure(string prefix, ModelCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {(result.ExitCode is int code ? code.ToString(CultureInfo.InvariantCulture) : "unknown")}"
            : result.StandardError.Trim();
        return $"{prefix}: {detail[..Math.Min(detail.Length, 500)]}";
    }
}
