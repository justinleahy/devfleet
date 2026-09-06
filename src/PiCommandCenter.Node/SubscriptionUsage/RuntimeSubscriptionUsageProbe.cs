using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Antigravity;

namespace PiCommandCenter.Node.SubscriptionUsage;

public interface IRuntimeSubscriptionUsageProbe
{
    Task<NodeSubscriptionUsageMessage> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Raw outcome of one probe command. <see cref="Missing"/> means the executable could not be
/// found; a null <see cref="ExitCode"/> without <see cref="Missing"/> or <see cref="TimedOut"/>
/// means the process failed to start or a pipe read failed before EOF. Closed outcomes never
/// carry partial output.
/// </summary>
public sealed record SubscriptionUsageCommandResult(
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Truncated,
    bool Missing);

public interface IRuntimeSubscriptionUsageCommandRunner
{
    Task<SubscriptionUsageCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public interface IAntigravitySubscriptionUsageCommandRunner
{
    Task<SubscriptionUsageCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts a process with <see cref="ProcessStartInfo.ArgumentList"/> only (no shell),
/// closed stdin, a combined 256KiB stdout+stderr capture budget, and process-tree kill.
/// Process exit plus EOF on both pipes is one operation under a single 15s deadline: a
/// descendant that inherits a pipe and outlives its parent must not turn a valid-looking
/// prefix into a success, so any part that is cancelled or fails closes the result without
/// its partial output. Caller cancellation is observed before start and propagated.
/// </summary>
public sealed class RuntimeSubscriptionUsageCommandRunner : IRuntimeSubscriptionUsageCommandRunner
{
    /// <summary>
    /// A normalized sidecar report is a few KiB per provider; the budget leaves generous headroom
    /// for Node diagnostics on stderr without becoming unbounded.
    /// </summary>
    public const int MaxOutputBytes = 256 * 1024;
    /// <summary>
    /// Host deadline must exceed sidecar's 8-second deadline to cover SDK startup and serialization.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Upper bound on waiting for a killed tree to leave; the kill itself is best-effort.</summary>
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(2);

    /// <summary>ENOENT on Unix and ERROR_FILE_NOT_FOUND on Windows share this value.</summary>
    private const int FileNotFoundErrorCode = 2;

    private readonly TimeSpan _timeout;

    public RuntimeSubscriptionUsageCommandRunner()
        : this(Timeout)
    {
    }

    internal RuntimeSubscriptionUsageCommandRunner(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public async Task<SubscriptionUsageCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        cancellationToken.ThrowIfCancellationRequested();

        return await ExecutePreparedAsync(
                CreateStartInfo(executable, arguments, Directory.GetCurrentDirectory()),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<SubscriptionUsageCommandResult> ExecutePreparedAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == FileNotFoundErrorCode)
        {
            return NotStarted(missing: true);
        }
        catch (Win32Exception)
        {
            return NotStarted(missing: false);
        }

        if (process is null)
        {
            return NotStarted(missing: false);
        }

        using (process)
        {
            process.StandardInput.Close();

            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);

            var budget = new OutputBudget(MaxOutputBytes);
            var exitTask = WaitForExitAsync(process, deadline.Token);
            var stdoutTask = ReadBoundedAsync(process.StandardOutput.BaseStream, budget, deadline.Token);
            var stderrTask = ReadBoundedAsync(process.StandardError.BaseStream, budget, deadline.Token);
            var operation = Task.WhenAll(exitTask, stdoutTask, stderrTask);
            try
            {
                // No part throws; the outer wait only keeps a pipe read that ignores
                // cancellation from extending the deadline.
                await operation.WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            if (operation.IsCompletedSuccessfully)
            {
                var exited = await exitTask.ConfigureAwait(false);
                var stdout = await stdoutTask.ConfigureAwait(false);
                var stderr = await stderrTask.ConfigureAwait(false);
                if (exited && stdout.Drained && stderr.Drained)
                {
                    return new SubscriptionUsageCommandResult(
                        process.ExitCode,
                        stdout.Text,
                        stderr.Text,
                        TimedOut: false,
                        stdout.Truncated || stderr.Truncated,
                        Missing: false);
                }
            }

            await ContainAsync(process, operation).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new SubscriptionUsageCommandResult(
                null,
                string.Empty,
                string.Empty,
                TimedOut: deadline.IsCancellationRequested,
                Truncated: false,
                Missing: false);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory)
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

        return startInfo;
    }

    private static SubscriptionUsageCommandResult NotStarted(bool missing)
        => new(null, string.Empty, string.Empty, TimedOut: false, Truncated: false, missing);

    private static async Task<bool> WaitForExitAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Best-effort tree kill followed by a bounded wait. .NET cannot enumerate descendants once
    /// their parent has exited, so a survivor is possible; it must not hold the probe open.
    /// </summary>
    private static async Task ContainAsync(Process process, Task drain)
    {
        TryKillTree(process);
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await process.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
            await drain.WaitAsync(cleanup.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void TryKillTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
        catch (AggregateException)
        {
            // Kill(entireProcessTree) reports descendants it could not terminate this way.
        }
    }

    /// <summary>
    /// Reads to EOF under the shared budget. Cancellation, I/O failure, or disposal before EOF
    /// yields an undrained result with no text, so a partial prefix can never be parsed.
    /// </summary>
    internal static async Task<BoundedRead> ReadBoundedAsync(
        Stream stream,
        OutputBudget budget,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var collected = new MemoryStream();
        var truncated = false;
        while (true)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return BoundedRead.Undrained;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                return BoundedRead.Undrained;
            }

            if (read == 0)
            {
                break;
            }

            var take = budget.Reserve(read);
            collected.Write(buffer, 0, take);
            if (take < read)
            {
                truncated = true;
            }
        }

        return new BoundedRead(
            Drained: true,
            Encoding.UTF8.GetString(collected.GetBuffer(), 0, (int)collected.Length),
            truncated);
    }

    /// <summary><see cref="Drained"/> is true only when the pipe reached EOF; otherwise <see cref="Text"/> is empty.</summary>
    internal readonly record struct BoundedRead(bool Drained, string Text, bool Truncated)
    {
        public static readonly BoundedRead Undrained = new(Drained: false, string.Empty, Truncated: false);
    }

    /// <summary>Byte budget shared by concurrent stdout and stderr readers.</summary>
    internal sealed class OutputBudget(int total)
    {
        private int _remaining = total;

        /// <summary>Atomically reserves up to <paramref name="requested"/> bytes; returns the granted count.</summary>
        public int Reserve(int requested)
        {
            while (true)
            {
                var current = Volatile.Read(ref _remaining);
                if (current <= 0)
                {
                    return 0;
                }

                var granted = Math.Min(current, requested);
                if (Interlocked.CompareExchange(ref _remaining, current - granted, current) == current)
                {
                    return granted;
                }
            }
        }
    }
}

/// <summary>
/// Runs Antigravity subscription commands only after establishing the mandatory read-only
/// filesystem boundary. The fixed non-secret working directory keeps usage probes independent
/// of the node process's current repository.
/// </summary>
public sealed class AntigravitySubscriptionUsageCommandRunner
    : IAntigravitySubscriptionUsageCommandRunner
{
    internal const string WorkingDirectory = "/tmp";

    private static readonly SubscriptionUsageCommandResult MissingExecutable =
        new(null, string.Empty, string.Empty, TimedOut: false, Truncated: false, Missing: true);

    private readonly Func<
        ProcessStartInfo,
        CancellationToken,
        Task<SubscriptionUsageCommandResult>> _executeAsync;
    private readonly string? _bwrapPath;
    private readonly IReadOnlyList<string>? _maskedLocations;
    private readonly string? _writableStateLocation;

    public AntigravitySubscriptionUsageCommandRunner()
        : this(
            new RuntimeSubscriptionUsageCommandRunner().ExecutePreparedAsync,
            bwrapPath: null,
            maskedLocations: null,
            writableStateLocation: null)
    {
    }

    internal AntigravitySubscriptionUsageCommandRunner(
        Func<ProcessStartInfo, CancellationToken, Task<SubscriptionUsageCommandResult>> executeAsync,
        string? bwrapPath,
        IReadOnlyList<string>? maskedLocations,
        string? writableStateLocation = null)
    {
        ArgumentNullException.ThrowIfNull(executeAsync);
        _executeAsync = executeAsync;
        _bwrapPath = bwrapPath;
        _maskedLocations = maskedLocations;
        _writableStateLocation = writableStateLocation;
    }

    public Task<SubscriptionUsageCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        cancellationToken.ThrowIfCancellationRequested();
        // Once wrapped, Process.Start can only report a missing bwrap. Resolve the original
        // executable first so an uninstalled agy keeps the source's Missing contract.
        if (!ExecutableExists(executable))
        {
            return Task.FromResult(MissingExecutable);
        }

        var startInfo = RuntimeSubscriptionUsageCommandRunner.CreateStartInfo(
            executable,
            arguments,
            WorkingDirectory);

        AntigravityReadOnlySandbox.Apply(
            startInfo,
            WorkingDirectory,
            _bwrapPath,
            _maskedLocations,
            _writableStateLocation);
        return _executeAsync(startInfo, cancellationToken);
    }

    private static bool ExecutableExists(string executable)
    {
        if (executable.Contains(Path.DirectorySeparatorChar)
            || executable.Contains(Path.AltDirectorySeparatorChar))
        {
            return File.Exists(executable);
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, executable)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Runs the bundled Pi usage sidecar and the ordered provider-native supplemental sources
/// concurrently, then merges their normalized reports deterministically. Only the fields named
/// in <see cref="TryParseReport"/> are read from sidecar JSON. Sidecar drift closes its contract
/// providers; an individual supplemental failure is isolated. Raw command and provider output
/// never leaves its source boundary.
/// </summary>
public sealed class RuntimeSubscriptionUsageProbe : IRuntimeSubscriptionUsageProbe
{
    public const string ProcessTimeout = "process_timeout";
    public const string ProcessMissing = "process_missing";
    public const string ProcessTruncated = "process_truncated";
    public const string ProcessFailed = "process_failed";

    /// <summary>The document as a whole could not be read: not JSON, wrong root shape, or a report that cannot be attributed to a contract provider.</summary>
    public const string ProcessMalformed = "process_malformed";

    /// <summary>One provider's report did not hold together; sibling reports are unaffected.</summary>
    public const string ReportMalformed = "report_malformed";

    /// <summary>The provider reported no limit carrying a percentage, so there is nothing to show.</summary>
    public const string QuotaNotReported = "quota_not_reported";

    /// <summary>The sidecar closed the provider as <c>unavailable</c> without naming a reason.</summary>
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>The sidecar closed the provider as <c>error</c> without naming a reason.</summary>
    public const string ProviderError = "provider_error";

    /// <summary>Stable, secret-free label naming where every row came from.</summary>
    public const string Source = "pi ModelRuntime provider usage";

    /// <summary>
    /// Provider ids the sidecar emits. A report for any other id is drift, not a new provider:
    /// the DTO must never carry an identifier the page has not agreed to render.
    /// </summary>
    public static readonly IReadOnlyList<string> Providers =
    [
        "openai-codex",
        "anthropic",
        "kimi-code",
        "zai",
        "xai-oauth",
        "opencode-go",
        "qwen-token-plan",
        "qwen-token-plan-individual",
        "qwen-token-plan-cn",
    ];

    /// <summary>
    /// More windows than any real plan has is a malformed response, not a long list. Matches the
    /// usage page's own limit so a node never emits a snapshot the page would discard.
    /// </summary>
    public const int MaxWindows = 8;

    /// <summary>Bounds the limit label, the window label that may disambiguate it, and a sidecar diagnostic.</summary>
    public const int MaxLabelLength = 40;

    /// <summary>
    /// Slack allowed between a reported used and remaining fraction. Each may be rounded
    /// independently upstream; anything wider is two claims about the same window. Stays under
    /// the page's 0.25-point tolerance even after this class rounds both to two decimals.
    /// </summary>
    private const double FractionSumTolerance = 0.002;

    /// <summary>9999-12-31T23:59:59.999Z, the last instant <see cref="DateTimeOffset"/> can hold.</summary>
    private const long MaxEpochMilliseconds = 253_402_300_799_999;

    private readonly IOptions<NodeOptions> _node;
    private readonly IOptions<SubscriptionUsageOptions> _options;
    private readonly IRuntimeSubscriptionUsageCommandRunner _runner;
    private readonly IReadOnlyList<ISupplementalSubscriptionUsageSource> _supplementalSources;
    private readonly TimeProvider _time;

    public RuntimeSubscriptionUsageProbe(
        IOptions<NodeOptions> node,
        IOptions<SubscriptionUsageOptions> options,
        TimeProvider time,
        IEnumerable<ISupplementalSubscriptionUsageSource> supplementalSources,
        IRuntimeSubscriptionUsageCommandRunner? runner = null)
    {
        _node = node;
        _options = options;
        _time = time;
        _supplementalSources = [.. supplementalSources];
        _runner = runner ?? new RuntimeSubscriptionUsageCommandRunner();
    }

    public async Task<NodeSubscriptionUsageMessage> GetAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = _time.GetUtcNow();
        var sidecarTask = ReadSidecarAsync(observedAt, cancellationToken);
        var supplementalTasks =
            new Task<ProviderSubscriptionUsageMessage?>[_supplementalSources.Count];
        var operations = new Task[supplementalTasks.Length + 1];
        operations[0] = sidecarTask;
        for (var i = 0; i < supplementalTasks.Length; i++)
        {
            var task = ReadSupplementalAsync(
                _supplementalSources[i],
                observedAt,
                cancellationToken);
            supplementalTasks[i] = task;
            operations[i + 1] = task;
        }

        await Task.WhenAll(operations).ConfigureAwait(false);

        var sidecarProviders = await sidecarTask.ConfigureAwait(false);
        var providers = new List<ProviderSubscriptionUsageMessage>(sidecarProviders);
        foreach (var task in supplementalTasks)
        {
            if (await task.ConfigureAwait(false) is not { } supplemental)
            {
                continue;
            }

            var existing = providers.FindIndex(
                provider => string.Equals(
                    provider.Provider,
                    supplemental.Provider,
                    StringComparison.Ordinal));
            if (existing >= 0)
            {
                providers[existing] = supplemental;
            }
            else
            {
                providers.Add(supplemental);
            }
        }

        return new NodeSubscriptionUsageMessage(_node.Value.Id, providers);
    }

    private async Task<IReadOnlyList<ProviderSubscriptionUsageMessage>> ReadSidecarAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var options = _options.Value;
        if (string.IsNullOrWhiteSpace(options.NodeExecutable)
            || string.IsNullOrWhiteSpace(options.ScriptPath))
        {
            return ClosedAll(observedAt, SubscriptionUsageStatuses.Unavailable, ProcessMissing);
        }

        var result = await _runner.RunAsync(
                options.NodeExecutable,
                [options.ScriptPath],
                cancellationToken)
            .ConfigureAwait(false);
        if (Classify(result) is { } failure)
        {
            return ClosedAll(observedAt, failure.Status, failure.Diagnostic);
        }

        if (!TryParseDocument(result.StandardOutput, observedAt, out var providers))
        {
            return ClosedAll(observedAt, SubscriptionUsageStatuses.Error, ProcessMalformed);
        }

        return providers;
    }

    private static async Task<ProviderSubscriptionUsageMessage?> ReadSupplementalAsync(
        ISupplementalSubscriptionUsageSource source,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = source.Provider;
            var message = await source.ReadAsync(observedAt, cancellationToken)
                .ConfigureAwait(false);
            return message is not null
                && string.Equals(message.Provider, provider, StringComparison.Ordinal)
                    ? message
                    : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps a raw command result to a closed outcome, or null when the output may be parsed.
    /// A missing executable is <c>unavailable</c>; every other failure is <c>error</c>.
    /// </summary>
    private static Failure? Classify(SubscriptionUsageCommandResult result)
    {
        if (result.Missing)
        {
            return new Failure(SubscriptionUsageStatuses.Unavailable, ProcessMissing);
        }

        if (result.TimedOut)
        {
            return new Failure(SubscriptionUsageStatuses.Error, ProcessTimeout);
        }

        if (result.Truncated)
        {
            return new Failure(SubscriptionUsageStatuses.Error, ProcessTruncated);
        }

        if (result.ExitCode != 0)
        {
            return new Failure(SubscriptionUsageStatuses.Error, ProcessFailed);
        }

        return null;
    }

    /// <summary>
    /// Strict parse of the document: a JSON object whose <c>reports</c> array holds at most one
    /// object per contract provider, each naming a distinct one. Everything else at this level
    /// is ignored. Reports come out in the order the sidecar printed them.
    /// </summary>
    private static bool TryParseDocument(
        string stdout,
        DateTimeOffset observedAt,
        out IReadOnlyList<ProviderSubscriptionUsageMessage> providers)
    {
        providers = [];
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("reports", out var reports)
                || reports.ValueKind != JsonValueKind.Array
                || reports.GetArrayLength() > Providers.Count)
            {
                return false;
            }

            var parsed = new List<ProviderSubscriptionUsageMessage>(reports.GetArrayLength());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var report in reports.EnumerateArray())
            {
                if (report.ValueKind != JsonValueKind.Object
                    || !report.TryGetProperty("provider", out var providerElement)
                    || providerElement.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                var provider = KnownProvider(providerElement);
                if (provider is null || !seen.Add(provider))
                {
                    return false;
                }

                parsed.Add(Describe(provider, report, observedAt));
            }

            providers = parsed;
            return true;
        }
    }

    /// <summary>
    /// A readable report takes the status the sidecar declared: <c>available</c> with its
    /// windows (or <c>unavailable</c> when no limit carried a percentage), otherwise closed with
    /// the sidecar's diagnostic. Either way it is stamped with the report's own <c>fetchedAt</c>.
    /// An unreadable report is <c>error</c> stamped with the probe time.
    /// </summary>
    private static ProviderSubscriptionUsageMessage Describe(string provider, JsonElement report, DateTimeOffset observedAt)
    {
        if (!TryParseStatus(report, out var status, out var diagnostic)
            || !TryParseReport(report, out var fetchedAt, out var windows))
        {
            return Closed(provider, observedAt, SubscriptionUsageStatuses.Error, ReportMalformed);
        }

        if (diagnostic is not null)
        {
            return Closed(provider, fetchedAt, status, diagnostic);
        }

        if (windows.Count == 0)
        {
            return Closed(provider, fetchedAt, SubscriptionUsageStatuses.Unavailable, QuotaNotReported);
        }

        return new ProviderSubscriptionUsageMessage(
            provider,
            SubscriptionUsageStatuses.Available,
            Authenticated: null,
            PlanLabel: null,
            Version: null,
            windows,
            fetchedAt,
            Source,
            Diagnostic: null);
    }

    /// <summary>
    /// Reads the sidecar's explicit <c>status</c>. For <c>unavailable</c> or <c>error</c> the
    /// <paramref name="diagnostic"/> is the sidecar's own token when it named one and safe, or
    /// <see cref="ProviderUnavailable"/>/<see cref="ProviderError"/> when it named none; a
    /// present but unsafe token fails the report. For <c>available</c> the diagnostic is null
    /// and any the sidecar attached is ignored.
    /// </summary>
    private static bool TryParseStatus(JsonElement report, out string status, out string? diagnostic)
    {
        status = SubscriptionUsageStatuses.Error;
        diagnostic = null;
        if (!report.TryGetProperty("status", out var statusElement) || statusElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        if (statusElement.ValueEquals(SubscriptionUsageStatuses.Available))
        {
            status = SubscriptionUsageStatuses.Available;
            return true;
        }

        if (statusElement.ValueEquals(SubscriptionUsageStatuses.Unavailable))
        {
            status = SubscriptionUsageStatuses.Unavailable;
            diagnostic = ProviderUnavailable;
        }
        else if (statusElement.ValueEquals(SubscriptionUsageStatuses.Error))
        {
            diagnostic = ProviderError;
        }
        else
        {
            return false;
        }

        if (!report.TryGetProperty("diagnostic", out var diagnosticElement))
        {
            return true;
        }

        if (!TrySafeDiagnostic(diagnosticElement, out var named))
        {
            return false;
        }

        diagnostic = named;
        return true;
    }

    /// <summary>Returns the interned contract id the element spells exactly, without materializing any other string.</summary>
    private static string? KnownProvider(JsonElement element)
    {
        foreach (var provider in Providers)
        {
            if (element.ValueEquals(provider))
            {
                return provider;
            }
        }

        return null;
    }

    /// <summary>
    /// Strict parse of one report. Reads <c>fetchedAt</c> (epoch milliseconds) and, per
    /// <c>limits[]</c> entry, <c>label</c>, <c>window.resetsAt</c>, and
    /// <c>amount.usedFraction</c>/<c>remainingFraction</c>. A limit with neither fraction has no
    /// percentage to show and is skipped; a present fraction must be finite in 0..1 and, when
    /// both are present, the pair must sum to one within rounding slack. Window names are the
    /// labels; when a label repeats, <c>window.label</c> is appended to every occurrence so the
    /// names stay distinct and deterministic. Anything else fails the report.
    /// </summary>
    private static bool TryParseReport(
        JsonElement report,
        out DateTimeOffset fetchedAt,
        out IReadOnlyList<SubscriptionUsageWindowMessage> windows)
    {
        windows = [];
        if (!report.TryGetProperty("fetchedAt", out var fetchedAtElement)
            || !TryEpochMilliseconds(fetchedAtElement, out fetchedAt)
            || !report.TryGetProperty("limits", out var limits)
            || limits.ValueKind != JsonValueKind.Array
            || limits.GetArrayLength() > MaxWindows)
        {
            fetchedAt = default;
            return false;
        }

        var parsed = new List<ParsedLimit>(limits.GetArrayLength());
        foreach (var limit in limits.EnumerateArray())
        {
            if (!TryParseLimit(limit, out var window))
            {
                return false;
            }

            if (window is { } kept)
            {
                parsed.Add(kept);
            }
        }

        var labelCounts = new Dictionary<string, int>(parsed.Count, StringComparer.Ordinal);
        foreach (var limit in parsed)
        {
            labelCounts[limit.Label] = labelCounts.GetValueOrDefault(limit.Label) + 1;
        }

        var names = new HashSet<string>(parsed.Count, StringComparer.Ordinal);
        var built = new SubscriptionUsageWindowMessage[parsed.Count];
        for (var i = 0; i < built.Length; i++)
        {
            var limit = parsed[i];
            var name = limit.Label;
            if (labelCounts[limit.Label] > 1)
            {
                if (limit.WindowLabel is null)
                {
                    return false;
                }

                name = $"{limit.Label} \u2014 {limit.WindowLabel}";
            }

            if (!names.Add(name))
            {
                return false;
            }

            built[i] = new SubscriptionUsageWindowMessage(name, limit.PercentUsed, limit.PercentRemaining, limit.ResetsAt);
        }

        windows = built;
        return true;
    }

    /// <summary>
    /// One <c>limits[]</c> entry. Returns false on malformed data; true with a null
    /// <paramref name="window"/> when the entry is well-formed but carries no percentage.
    /// </summary>
    private static bool TryParseLimit(JsonElement limit, out ParsedLimit? window)
    {
        window = null;
        if (limit.ValueKind != JsonValueKind.Object
            || !limit.TryGetProperty("label", out var labelElement)
            || !TrySafeLabel(labelElement, out var label)
            || !limit.TryGetProperty("window", out var windowElement)
            || windowElement.ValueKind != JsonValueKind.Object
            || !limit.TryGetProperty("amount", out var amount)
            || amount.ValueKind != JsonValueKind.Object
            || !TryOptionalFraction(amount, "usedFraction", out var used)
            || !TryOptionalFraction(amount, "remainingFraction", out var remaining))
        {
            return false;
        }

        DateTimeOffset? resetsAt = null;
        if (windowElement.TryGetProperty("resetsAt", out var resetsAtElement))
        {
            if (!TryEpochMilliseconds(resetsAtElement, out var resets))
            {
                return false;
            }

            resetsAt = resets;
        }

        // The window label is only consulted when a limit label repeats; an absent or unsafe one
        // is therefore tolerated here and rejected later only if it turns out to be needed.
        string? windowLabel = null;
        if (windowElement.TryGetProperty("label", out var windowLabelElement)
            && TrySafeLabel(windowLabelElement, out var safeWindowLabel))
        {
            windowLabel = safeWindowLabel;
        }

        if (used is null && remaining is null)
        {
            return true;
        }

        if (used is { } u && remaining is { } r && Math.Abs(u + r - 1) > FractionSumTolerance)
        {
            return false;
        }

        window = new ParsedLimit(label, windowLabel, Percent(used), Percent(remaining), resetsAt);
        return true;
    }

    private static double? Percent(double? fraction)
        => fraction is { } value ? Math.Round(value * 100, 2, MidpointRounding.AwayFromZero) : null;

    /// <summary>Absent is null; present must be a finite JSON number in 0..1.</summary>
    private static bool TryOptionalFraction(JsonElement amount, string name, out double? fraction)
    {
        fraction = null;
        if (!amount.TryGetProperty(name, out var element))
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out var value)
            || !double.IsFinite(value)
            || value is < 0 or > 1)
        {
            return false;
        }

        fraction = value;
        return true;
    }

    /// <summary>A JSON integer of non-negative epoch milliseconds that <see cref="DateTimeOffset"/> can hold.</summary>
    private static bool TryEpochMilliseconds(JsonElement element, out DateTimeOffset instant)
    {
        instant = default;
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out var milliseconds)
            || milliseconds is < 0 or > MaxEpochMilliseconds)
        {
            return false;
        }

        instant = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        return true;
    }

    /// <summary>
    /// A JSON string of one to <see cref="MaxLabelLength"/> printable ASCII characters with no
    /// leading or trailing space. Rejected text is dropped here and never reaches the DTO.
    /// </summary>
    private static bool TrySafeLabel(JsonElement element, [NotNullWhen(true)] out string? label)
    {
        label = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString()!;
        if (text.Length is 0 or > MaxLabelLength || text[0] == ' ' || text[^1] == ' ')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        label = text;
        return true;
    }

    /// <summary>
    /// A JSON string of one to <see cref="MaxLabelLength"/> lowercase ASCII letters, digits, or
    /// underscores: a stable token, never free text that could carry an upstream message.
    /// </summary>
    private static bool TrySafeDiagnostic(JsonElement element, [NotNullWhen(true)] out string? diagnostic)
    {
        diagnostic = null;
        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString()!;
        if (text.Length is 0 or > MaxLabelLength)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        diagnostic = text;
        return true;
    }

    /// <summary>One closed row per contract provider: the command answered for none of them.</summary>
    private static IReadOnlyList<ProviderSubscriptionUsageMessage> ClosedAll(
        DateTimeOffset observedAt,
        string status,
        string diagnostic)
    {
        var providers = new ProviderSubscriptionUsageMessage[Providers.Count];
        for (var i = 0; i < providers.Length; i++)
        {
            providers[i] = Closed(Providers[i], observedAt, status, diagnostic);
        }

        return providers;
    }

    private static ProviderSubscriptionUsageMessage Closed(
        string provider,
        DateTimeOffset observedAt,
        string status,
        string diagnostic)
        => new(
            provider,
            status,
            Authenticated: null,
            PlanLabel: null,
            Version: null,
            [],
            observedAt,
            Source,
            diagnostic);

    private readonly record struct Failure(string Status, string Diagnostic);

    private readonly record struct ParsedLimit(
        string Label,
        string? WindowLabel,
        double? PercentUsed,
        double? PercentRemaining,
        DateTimeOffset? ResetsAt);
}
