using System.Globalization;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;

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

/// <summary>Queries each allowed runtime through its node-owned discovery mechanism.</summary>
public sealed class RuntimeModelDiscovery : IRuntimeModelDiscovery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly PiWorkerOptions _pi;
    private readonly AntigravityOptions _antigravity;
    private readonly INodeRuntimeRoutingStore _routes;
    private readonly IRuntimeModelCommandRunner _runner;

    public RuntimeModelDiscovery(
        IOptions<PiWorkerOptions> pi,
        IOptions<AntigravityOptions> antigravity,
        INodeRuntimeRoutingStore routes,
        IRuntimeModelCommandRunner runner)
    {
        _pi = pi.Value;
        _antigravity = antigravity.Value;
        _routes = routes;
        _runner = runner;
    }

    public async Task<IReadOnlyList<RuntimeModelCatalogMessage>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = _routes.Current.AllowedRuntimeProfiles;
        var tasks = profiles.Select(profile => DiscoverProfileAsync(profile, cancellationToken));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private Task<RuntimeModelCatalogMessage> DiscoverProfileAsync(
        string profile,
        CancellationToken cancellationToken)
        => profile switch
        {
            AgentRuntimeProfiles.LocalPi => DiscoverPiAsync(profile, cancellationToken),
            AgentRuntimeProfiles.AntigravityReadOnly => DiscoverAntigravityAsync(profile, cancellationToken),
            AgentRuntimeProfiles.ClaudeReadOnly or AgentRuntimeProfiles.ClaudeReservedWrite =>
                Task.FromResult(ClaudeCatalog(profile)),
            _ => Task.FromResult(new RuntimeModelCatalogMessage(
                profile, [], "This runtime profile has no model discovery implementation.")),
        };

    private async Task<RuntimeModelCatalogMessage> DiscoverPiAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        var script = Path.Combine(Path.GetDirectoryName(_pi.WorkerPath)!, "modelCatalog.ts");
        var result = await _runner.RunAsync(
            _pi.NodeExecutable, [script], _pi.AgentDataDirectory, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Error(profile, "Pi model discovery timed out.");
        }
        if (result.ExitCode != 0 || result.Truncated)
        {
            return Error(profile, Failure("Pi model discovery failed", result));
        }
        try
        {
            var models = JsonSerializer.Deserialize<List<RuntimeModelMessage>>(
                result.StandardOutput, JsonOptions) ?? [];
            return new RuntimeModelCatalogMessage(profile, Deduplicate(models), null);
        }
        catch (JsonException ex)
        {
            return Error(profile, $"Pi model discovery returned invalid JSON: {ex.Message}");
        }
    }

    private async Task<RuntimeModelCatalogMessage> DiscoverAntigravityAsync(
        string profile,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            _antigravity.Executable, ["models"], _pi.AgentDataDirectory, cancellationToken).ConfigureAwait(false);
        if (result.TimedOut)
        {
            return Error(profile, "Antigravity model discovery timed out.");
        }
        if (result.ExitCode != 0 || result.Truncated)
        {
            return Error(profile, Failure("Antigravity model discovery failed", result));
        }
        var models = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t', 2))
            .Where(parts => parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]))
            .Select(parts => new RuntimeModelMessage(
                parts[0].Trim(),
                parts.Length == 2 ? parts[1].Trim() : parts[0].Trim(),
                "antigravity"));
        return new RuntimeModelCatalogMessage(profile, Deduplicate(models), null);
    }

    private RuntimeModelCatalogMessage ClaudeCatalog(string profile)
    {
        var configured = _routes.Current.RoleRoutes
            .SelectMany(route => route.Candidates)
            .Where(candidate => string.Equals(candidate.RuntimeProfile, profile, StringComparison.Ordinal)
                && candidate.Model is not null)
            .Select(candidate => new RuntimeModelMessage(candidate.Model!, candidate.Model!, "claude"));
        return new RuntimeModelCatalogMessage(
            profile,
            Deduplicate(configured),
            "Claude Code does not expose an authenticated model-list command; enter a documented model ID or use its default.");
    }

    private static IReadOnlyList<RuntimeModelMessage> Deduplicate(IEnumerable<RuntimeModelMessage> models)
        => models
            .Where(model => !string.IsNullOrWhiteSpace(model.Id))
            .DistinctBy(model => model.Id, StringComparer.Ordinal)
            .OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static RuntimeModelCatalogMessage Error(string profile, string message)
        => new(profile, [], message);

    private static string Failure(string prefix, ModelCommandResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.StandardError)
            ? $"exit code {(result.ExitCode is int code ? code.ToString(CultureInfo.InvariantCulture) : "unknown")}"
            : result.StandardError.Trim();
        return $"{prefix}: {detail[..Math.Min(detail.Length, 500)]}";
    }
}
