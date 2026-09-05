using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.RuntimeRouting;

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

/// <summary>
/// Starts a process with <see cref="ProcessStartInfo.ArgumentList"/> only (no shell),
/// closed stdin, a combined 16KiB stdout+stderr capture budget, and process-tree kill.
/// Process exit plus EOF on both pipes is one operation under a single 10s deadline: a
/// descendant that inherits a pipe and outlives its parent must not turn a valid-looking
/// prefix into a success, so any part that is cancelled or fails closes the result without
/// its partial output. Caller cancellation is observed before start and propagated.
/// </summary>
public sealed class RuntimeSubscriptionUsageCommandRunner : IRuntimeSubscriptionUsageCommandRunner
{
    public const int MaxOutputBytes = 16 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

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

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Directory.GetCurrentDirectory(),
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
/// Probes each allowed provider for remaining subscription windows. Pi and Claude quota come from
/// <see cref="IProviderSubscriptionQuotaReader"/>; Claude additionally runs fixed, non-interactive
/// CLI commands for version and sign-in state, and Antigravity runs its print-mode <c>/usage</c>
/// report and parses the strict tab-separated grammar it prints. Anything that drifts from the
/// expected shape closes the provider with a stable diagnostic and no windows. Raw command output
/// never leaves this class.
/// </summary>
public sealed class RuntimeSubscriptionUsageProbe : IRuntimeSubscriptionUsageProbe
{
    public const string UnknownDiagnostic = "unknown_runtime_profile";
    public const string ProcessTimeout = "process_timeout";
    public const string ProcessMissing = "process_missing";
    public const string ProcessTruncated = "process_truncated";
    public const string ProcessMalformed = "process_malformed";
    public const string ProcessFailed = "process_failed";

    /// <summary>
    /// A quota reader's answer did not hold together: <c>available</c> without coherent windows,
    /// or a closed answer without a diagnostic.
    /// </summary>
    public const string QuotaIncoherent = "quota_incoherent";

    /// <summary><c>claude auth status</c> reported no signed-in account, so no credential was read.</summary>
    public const string SignedOut = "signed_out";

    /// <summary>
    /// Only documented non-interactive quota surface: standalone print-mode slash with a bounded
    /// wait. The advertised 8s deadline sits under the runner's 10s one so the CLI can finish its
    /// own timeout path and exit cleanly instead of being killed mid-report.
    /// </summary>
    private static readonly string[] AntigravityUsageArguments = ["-p", "/usage", "--print-timeout", "8s"];

    /// <summary>
    /// More windows than any real plan has is a malformed response, not a long list. Matches the
    /// usage page's own limit so a node never emits a snapshot the page would discard.
    /// </summary>
    public const int MaxWindows = 8;

    /// <summary>
    /// Slack allowed between a reported used and remaining percentage: each may be rounded to one
    /// decimal independently, so the pair can miss 100 by up to 0.1 without contradicting itself.
    /// </summary>
    private const double PercentSumTolerance = 0.25;

    private const string ClaudeCommandSource = "claude --version; claude auth status";
    private const string AntigravitySource = "agy --version; agy -p /usage --print-timeout 8s";

    /// <summary>Documented <c>claude auth status</c> exit code when no account is logged in.</summary>
    private const int ClaudeNotLoggedInExitCode = 1;

    /// <summary>
    /// Only these canonical labels may reach the DTO; anything else in <c>subscriptionType</c>
    /// is dropped. The allowlist also bounds the label length.
    /// </summary>
    private static readonly string[] KnownPlanLabels = ["Pro", "Max", "Team", "Enterprise", "API"];

    /// <summary>
    /// Exact second-column labels <c>agy -p /usage</c> prints, and the window suffix each becomes.
    /// Any other label is drift, not a new window kind.
    /// </summary>
    private static readonly (string Label, string Suffix)[] AntigravityWindowKinds =
    [
        ("Weekly Limit Remaining", "weekly"),
        ("Five Hour Limit Remaining", "five-hour"),
    ];

    /// <summary>Bounds the model-group column so a window name stays short and printable.</summary>
    private const int MaxAntigravityGroupLength = 32;

    private static readonly string[] Rfc3339Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    private static readonly Regex SemVer = new(
        @"^(?<v>\d+\.\d+\.\d+)(?:[-+][A-Za-z0-9.-]+)?(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Words of ASCII letters, digits, or <c>.+/-</c> separated by single spaces or that punctuation.</summary>
    private static readonly Regex AntigravityGroup = new(
        @"^[A-Za-z0-9]+(?:[ .+/-][A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly INodeRuntimeRoutingStore _routes;
    private readonly IOptions<NodeOptions> _node;
    private readonly IOptions<ClaudeCodeOptions> _claude;
    private readonly IOptions<AntigravityOptions> _antigravity;
    private readonly IProviderSubscriptionQuotaReader _quota;
    private readonly IRuntimeSubscriptionUsageCommandRunner _runner;
    private readonly TimeProvider _time;

    public RuntimeSubscriptionUsageProbe(
        INodeRuntimeRoutingStore routes,
        IOptions<NodeOptions> node,
        IOptions<ClaudeCodeOptions> claude,
        IOptions<AntigravityOptions> antigravity,
        TimeProvider time,
        IProviderSubscriptionQuotaReader quota,
        IRuntimeSubscriptionUsageCommandRunner? runner = null)
    {
        _routes = routes;
        _node = node;
        _claude = claude;
        _antigravity = antigravity;
        _time = time;
        _quota = quota;
        _runner = runner ?? new RuntimeSubscriptionUsageCommandRunner();
    }

    public async Task<NodeSubscriptionUsageMessage> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var observedAt = _time.GetUtcNow();
        var groups = GroupProfiles(_routes.Current.AllowedRuntimeProfiles);
        var probes = new Task<ProviderSubscriptionUsageMessage>[groups.Count];
        for (var i = 0; i < groups.Count; i++)
        {
            probes[i] = ProbeGroupAsync(groups[i], observedAt, cancellationToken);
        }

        // WhenAll preserves input order, so providers follow first-appearance order in the
        // allowed profiles regardless of which process finishes first.
        var providers = await Task.WhenAll(probes).ConfigureAwait(false);
        return new NodeSubscriptionUsageMessage(_node.Value.Id, providers);
    }

    private async Task<ProviderSubscriptionUsageMessage> ProbeGroupAsync(
        ProfileGroup group,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
        => group.Kind switch
        {
            ProviderKind.Pi => await PiSnapshotAsync(group.Profiles, observedAt, cancellationToken)
                .ConfigureAwait(false),
            ProviderKind.Claude => await ClaudeSnapshotAsync(group.Profiles, observedAt, cancellationToken)
                .ConfigureAwait(false),
            ProviderKind.Antigravity => await AntigravitySnapshotAsync(group.Profiles, observedAt, cancellationToken)
                .ConfigureAwait(false),
            _ => Closed(
                "unknown", group.Profiles, observedAt, "none", SubscriptionUsageStatuses.Unavailable, UnknownDiagnostic),
        };

    /// <summary>Pi has no CLI surface worth spawning; the OAuth reader is the whole reading.</summary>
    private async Task<ProviderSubscriptionUsageMessage> PiSnapshotAsync(
        IReadOnlyList<string> profiles,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var quota = await _quota.ReadPiAsync(cancellationToken).ConfigureAwait(false);
        return Merge("pi", profiles, observedAt, quota.Source, version: null, authenticated: null, planLabel: null, quota);
    }

    /// <summary>
    /// Version and sign-in state come from the CLI; the windows come from the OAuth reader only
    /// when both commands have succeeded and the CLI reports a signed-in account. A signed-out
    /// account closes here without touching the credential file, however stale its contents.
    /// The CLI's plan label wins over the reader's.
    /// </summary>
    private async Task<ProviderSubscriptionUsageMessage> ClaudeSnapshotAsync(
        IReadOnlyList<string> profiles,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string provider = "claude";
        var executable = _claude.Value.Executable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Closed(provider, profiles, observedAt, ClaudeCommandSource, SubscriptionUsageStatuses.Unavailable, ProcessMissing);
        }

        var versionResult = await RunAsync(executable, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        if (Classify(versionResult) is { } versionFailure)
        {
            return Closed(provider, profiles, observedAt, ClaudeCommandSource, versionFailure);
        }

        if (!TryParseSemVer(versionResult.StandardOutput, out var version))
        {
            return Closed(provider, profiles, observedAt, ClaudeCommandSource, SubscriptionUsageStatuses.Error, ProcessMalformed);
        }

        var authResult = await RunAsync(executable, ["auth", "status"], cancellationToken)
            .ConfigureAwait(false);
        if (Classify(authResult, ClaudeNotLoggedInExitCode) is { } authFailure)
        {
            return Closed(provider, profiles, observedAt, ClaudeCommandSource, authFailure);
        }

        if (!TryParseClaudeAuth(authResult.StandardOutput, out var authenticated, out var planLabel))
        {
            return Closed(provider, profiles, observedAt, ClaudeCommandSource, SubscriptionUsageStatuses.Error, ProcessMalformed);
        }

        // Exit 1 is documented only for the not-logged-in case; any other nonzero exit is a failure.
        if (authResult.ExitCode == ClaudeNotLoggedInExitCode && authenticated)
        {
            return Closed(provider, profiles, observedAt, ClaudeCommandSource, SubscriptionUsageStatuses.Error, ProcessFailed);
        }

        if (!authenticated)
        {
            return new ProviderSubscriptionUsageMessage(
                provider,
                profiles,
                SubscriptionUsageStatuses.Unavailable,
                Authenticated: false,
                planLabel,
                version,
                [],
                observedAt,
                ClaudeCommandSource,
                SignedOut);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var quota = await _quota.ReadClaudeAsync(cancellationToken).ConfigureAwait(false);
        return Merge(
            provider,
            profiles,
            observedAt,
            $"{ClaudeCommandSource}; {quota.Source}",
            version,
            authenticated: true,
            planLabel ?? quota.PlanLabel,
            quota);
    }

    private async Task<ProviderSubscriptionUsageMessage> AntigravitySnapshotAsync(
        IReadOnlyList<string> profiles,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        const string provider = "antigravity";
        var executable = _antigravity.Value.Executable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return Closed(provider, profiles, observedAt, AntigravitySource, SubscriptionUsageStatuses.Unavailable, ProcessMissing);
        }

        var versionResult = await RunAsync(executable, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        if (Classify(versionResult) is { } versionFailure)
        {
            return Closed(provider, profiles, observedAt, AntigravitySource, versionFailure);
        }

        if (!TryParseSemVer(versionResult.StandardOutput, out var version))
        {
            return Closed(provider, profiles, observedAt, AntigravitySource, SubscriptionUsageStatuses.Error, ProcessMalformed);
        }

        var usageResult = await RunAsync(executable, AntigravityUsageArguments, cancellationToken)
            .ConfigureAwait(false);
        if (Classify(usageResult) is { } usageFailure)
        {
            return Closed(provider, profiles, observedAt, AntigravitySource, usageFailure);
        }

        if (!TryParseAntigravityUsage(usageResult.StandardOutput, out var windows))
        {
            return Closed(provider, profiles, observedAt, AntigravitySource, SubscriptionUsageStatuses.Error, ProcessMalformed);
        }

        return new ProviderSubscriptionUsageMessage(
            provider,
            profiles,
            SubscriptionUsageStatuses.Available,
            Authenticated: null,
            PlanLabel: null,
            version,
            windows,
            observedAt,
            AntigravitySource,
            Diagnostic: null);
    }

    /// <summary>
    /// Normalizes a quota reader result into the DTO. <c>available</c> is honoured only when the
    /// windows hold together; anything else is closed with the reader's diagnostic and no windows,
    /// while the CLI-observed version, sign-in state, and plan label are kept.
    /// </summary>
    private static ProviderSubscriptionUsageMessage Merge(
        string provider,
        IReadOnlyList<string> profiles,
        DateTimeOffset observedAt,
        string source,
        string? version,
        bool? authenticated,
        string? planLabel,
        ProviderSubscriptionQuotaReadResult quota)
    {
        var status = quota.Status switch
        {
            SubscriptionQuotaReadStatus.Available when AreCoherent(quota.Windows) => SubscriptionUsageStatuses.Available,
            SubscriptionQuotaReadStatus.Unavailable => SubscriptionUsageStatuses.Unavailable,
            _ => SubscriptionUsageStatuses.Error,
        };
        var diagnostic = status == SubscriptionUsageStatuses.Available ? null
            : quota.Status == SubscriptionQuotaReadStatus.Available ? QuotaIncoherent
            : quota.Diagnostic ?? QuotaIncoherent;

        return new ProviderSubscriptionUsageMessage(
            provider,
            profiles,
            status,
            authenticated ?? quota.Authenticated,
            planLabel ?? quota.PlanLabel,
            version,
            status == SubscriptionUsageStatuses.Available ? quota.Windows : [],
            observedAt,
            source,
            diagnostic);
    }

    /// <summary>
    /// Nonempty, bounded, distinctly named windows whose percentages are finite figures on the
    /// 0..100 scale and, when both are present, sum to 100 within rounding slack. This is the
    /// DTO's own validity rule, applied here so a drifting reader fails closed on the node.
    /// </summary>
    private static bool AreCoherent(IReadOnlyList<SubscriptionUsageWindowMessage> windows)
    {
        if (windows.Count is 0 or > MaxWindows)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var window in windows)
        {
            if (string.IsNullOrWhiteSpace(window.Name)
                || !names.Add(window.Name)
                || !IsPercentage(window.PercentUsed)
                || !IsPercentage(window.PercentRemaining)
                || (window.PercentUsed is null && window.PercentRemaining is null)
                || (window.PercentUsed is { } used
                    && window.PercentRemaining is { } remaining
                    && Math.Abs(used + remaining - 100) > PercentSumTolerance))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPercentage(double? value)
        => value is not { } percent || (double.IsFinite(percent) && percent is >= 0 and <= 100);

    /// <summary>
    /// Maps a raw command result to a closed outcome, or null when the output may be parsed.
    /// A missing executable is <c>unavailable</c>; every other failure is <c>error</c>.
    /// <paramref name="acceptedNonZeroExit"/> lets a caller admit one documented exit code
    /// whose output it will validate itself.
    /// </summary>
    private static Failure? Classify(SubscriptionUsageCommandResult result, int? acceptedNonZeroExit = null)
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

        if (result.ExitCode is not int exitCode || (exitCode != 0 && exitCode != acceptedNonZeroExit))
        {
            return new Failure(SubscriptionUsageStatuses.Error, ProcessFailed);
        }

        return null;
    }

    private static bool TryParseSemVer(string stdout, out string version)
    {
        version = string.Empty;
        var line = stdout.AsSpan().Trim();
        var end = line.IndexOfAny('\r', '\n');
        if (end >= 0)
        {
            line = line[..end];
        }

        var match = SemVer.Match(line.ToString());
        if (!match.Success)
        {
            return false;
        }

        version = match.Groups["v"].Value;
        return true;
    }

    /// <summary>
    /// Strict parse of <c>agy -p /usage</c>: every nonempty line is exactly four tab-separated
    /// columns — model group, window label, <c>NN%</c> remaining, RFC 3339 reset — and becomes
    /// the window <c>&lt;group&gt; weekly</c> or <c>&lt;group&gt; five-hour</c> with used derived
    /// as <c>100 - remaining</c>. A single line that fails, a repeated window, or more than
    /// <see cref="MaxWindows"/> rows rejects the whole report; no other text is tolerated, so an
    /// error banner or account line never reaches the DTO.
    /// </summary>
    private static bool TryParseAntigravityUsage(
        string stdout,
        out IReadOnlyList<SubscriptionUsageWindowMessage> windows)
    {
        windows = [];
        var parsed = new List<SubscriptionUsageWindowMessage>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawLine in stdout.AsSpan().EnumerateLines())
        {
            if (rawLine.IsEmpty)
            {
                continue;
            }

            if (parsed.Count == MaxWindows || !TryParseAntigravityRow(rawLine, out var window) || !names.Add(window.Name))
            {
                return false;
            }

            parsed.Add(window);
        }

        if (parsed.Count == 0)
        {
            return false;
        }

        windows = parsed;
        return true;
    }

    private static bool TryParseAntigravityRow(
        ReadOnlySpan<char> line,
        [NotNullWhen(true)] out SubscriptionUsageWindowMessage? window)
    {
        window = null;
        Span<Range> columns = stackalloc Range[5];
        if (line.Split(columns, '\t') != 4)
        {
            return false;
        }

        var group = line[columns[0]];
        if (group.Length > MaxAntigravityGroupLength || !AntigravityGroup.IsMatch(group))
        {
            return false;
        }

        var suffix = WindowSuffix(line[columns[1]]);
        if (suffix is null
            || !TryParsePercent(line[columns[2]], out var remaining)
            || !DateTimeOffset.TryParseExact(
                line[columns[3]],
                Rfc3339Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var resetsAt))
        {
            return false;
        }

        window = new SubscriptionUsageWindowMessage($"{group} {suffix}", 100 - remaining, remaining, resetsAt);
        return true;
    }

    private static string? WindowSuffix(ReadOnlySpan<char> label)
    {
        foreach (var (known, suffix) in AntigravityWindowKinds)
        {
            if (label.SequenceEqual(known))
            {
                return suffix;
            }
        }

        return null;
    }

    /// <summary>Whole percent with a trailing sign: one to three digits, no sign or spaces, at most 100.</summary>
    private static bool TryParsePercent(ReadOnlySpan<char> column, out int percent)
    {
        percent = 0;
        return column.Length is >= 2 and <= 4
            && column[^1] == '%'
            && int.TryParse(column[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out percent)
            && percent <= 100;
    }

    /// <summary>
    /// Strict parse of <c>claude auth status</c> JSON: a single object with a boolean
    /// <c>loggedIn</c>. Only <c>subscriptionType</c> is read beyond that, and only when it
    /// matches a known plan label; email, organization, and other fields are never touched.
    /// </summary>
    private static bool TryParseClaudeAuth(
        string stdout,
        out bool authenticated,
        out string? planLabel)
    {
        authenticated = false;
        planLabel = null;
        try
        {
            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("loggedIn", out var loggedIn)
                || (loggedIn.ValueKind != JsonValueKind.True && loggedIn.ValueKind != JsonValueKind.False))
            {
                return false;
            }

            authenticated = loggedIn.GetBoolean();
            if (root.TryGetProperty("subscriptionType", out var subscription)
                && subscription.ValueKind == JsonValueKind.String)
            {
                planLabel = SanitizePlanLabel(subscription.GetString());
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? SanitizePlanLabel(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        foreach (var known in KnownPlanLabels)
        {
            if (string.Equals(raw, known, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    private static ProviderSubscriptionUsageMessage Closed(
        string provider,
        IReadOnlyList<string> profiles,
        DateTimeOffset observedAt,
        string source,
        Failure failure)
        => Closed(provider, profiles, observedAt, source, failure.Status, failure.Diagnostic);

    private static ProviderSubscriptionUsageMessage Closed(
        string provider,
        IReadOnlyList<string> profiles,
        DateTimeOffset observedAt,
        string source,
        string status,
        string diagnostic)
        => new(
            provider,
            profiles,
            status,
            Authenticated: null,
            PlanLabel: null,
            Version: null,
            [],
            observedAt,
            source,
            diagnostic);

    /// <summary>Observes caller cancellation before every command, including between the two Claude commands.</summary>
    private Task<SubscriptionUsageCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _runner.RunAsync(executable, arguments, cancellationToken);
    }

    /// <summary>
    /// One group per provider in first-appearance order. Both Claude profiles collapse into one
    /// group; every other profile is probed at most once even when the allowed list repeats it.
    /// </summary>
    private static IReadOnlyList<ProfileGroup> GroupProfiles(IReadOnlyList<string> allowed)
    {
        var groups = new List<ProfileGroup>();
        var claude = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in allowed)
        {
            if (!seen.Add(profile))
            {
                continue;
            }

            if (profile is AgentRuntimeProfiles.ClaudeReadOnly or AgentRuntimeProfiles.ClaudeReservedWrite)
            {
                claude.Add(profile);
                if (claude.Count == 1)
                {
                    groups.Add(new ProfileGroup(ProviderKind.Claude, claude));
                }

                continue;
            }

            var kind = profile switch
            {
                AgentRuntimeProfiles.LocalPi => ProviderKind.Pi,
                AgentRuntimeProfiles.AntigravityReadOnly => ProviderKind.Antigravity,
                _ => ProviderKind.Unknown,
            };
            groups.Add(new ProfileGroup(kind, [profile]));
        }

        claude.Sort(StringComparer.Ordinal);
        return groups;
    }

    private enum ProviderKind
    {
        Pi,
        Claude,
        Antigravity,
        Unknown,
    }

    private sealed record ProfileGroup(ProviderKind Kind, IReadOnlyList<string> Profiles);

    private readonly record struct Failure(string Status, string Diagnostic);
}
