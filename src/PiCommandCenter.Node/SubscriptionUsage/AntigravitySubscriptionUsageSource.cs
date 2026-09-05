using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.SubscriptionUsage;

/// <summary>
/// Reads subscription windows from the official Antigravity CLI's bounded print-mode usage report.
/// Raw command output is accepted only through the pinned TSV grammar and never crosses the DTO boundary.
/// </summary>
public sealed class AntigravitySubscriptionUsageSource : ISupplementalSubscriptionUsageSource
{
    public const string ProviderId = "google-antigravity";
    public const string Source = "agy --version; agy -p /usage --print-timeout 8s";

    private const int MaxGroupLength = 32;

    private static readonly string[] UsageArguments = ["-p", "/usage", "--print-timeout", "8s"];

    private static readonly (string Label, string Suffix)[] WindowKinds =
    [
        ("Weekly Limit Remaining", "weekly"),
        ("Five Hour Limit Remaining", "five-hour"),
    ];

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

    private static readonly Regex SafeGroup = new(
        @"^[A-Za-z0-9]+(?:[ .+/-][A-Za-z0-9]+)*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly IOptions<AntigravityOptions> _options;
    private readonly IAntigravitySubscriptionUsageCommandRunner _runner;

    public AntigravitySubscriptionUsageSource(
        IOptions<AntigravityOptions> options,
        IAntigravitySubscriptionUsageCommandRunner runner)
    {
        _options = options;
        _runner = runner;
    }

    public string Provider => ProviderId;

    public async Task<ProviderSubscriptionUsageMessage?> ReadAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executable = _options.Value.Executable;
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var versionResult = await RunAsync(executable, ["--version"], cancellationToken)
            .ConfigureAwait(false);
        if (versionResult.Missing)
        {
            return null;
        }

        if (Classify(versionResult) is { } versionFailure)
        {
            return Closed(observedAt, versionFailure);
        }

        if (!TryParseSemVer(versionResult.StandardOutput, out var version))
        {
            return Closed(observedAt, RuntimeSubscriptionUsageProbe.ProcessMalformed);
        }

        var usageResult = await RunAsync(executable, UsageArguments, cancellationToken)
            .ConfigureAwait(false);
        if (usageResult.Missing)
        {
            return null;
        }

        if (Classify(usageResult) is { } usageFailure)
        {
            return Closed(observedAt, usageFailure);
        }

        if (!TryParseUsage(usageResult.StandardOutput, out var windows))
        {
            return Closed(observedAt, RuntimeSubscriptionUsageProbe.ProcessMalformed);
        }

        return new ProviderSubscriptionUsageMessage(
            ProviderId,
            SubscriptionUsageStatuses.Available,
            Authenticated: null,
            PlanLabel: null,
            version,
            windows,
            observedAt,
            Source,
            Diagnostic: null);
    }

    private Task<SubscriptionUsageCommandResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _runner.RunAsync(executable, arguments, cancellationToken);
    }

    private static string? Classify(SubscriptionUsageCommandResult result)
    {
        if (result.TimedOut)
        {
            return RuntimeSubscriptionUsageProbe.ProcessTimeout;
        }

        if (result.Truncated)
        {
            return RuntimeSubscriptionUsageProbe.ProcessTruncated;
        }

        return result.ExitCode == 0 ? null : RuntimeSubscriptionUsageProbe.ProcessFailed;
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

    private static bool TryParseUsage(
        string stdout,
        out IReadOnlyList<SubscriptionUsageWindowMessage> windows)
    {
        windows = [];
        var parsed = new List<SubscriptionUsageWindowMessage>(RuntimeSubscriptionUsageProbe.MaxWindows);
        var names = new HashSet<string>(RuntimeSubscriptionUsageProbe.MaxWindows, StringComparer.Ordinal);
        foreach (var line in stdout.AsSpan().EnumerateLines())
        {
            if (line.IsEmpty)
            {
                continue;
            }

            if (parsed.Count == RuntimeSubscriptionUsageProbe.MaxWindows
                || !TryParseRow(line, out var window)
                || !names.Add(window.Name))
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

    private static bool TryParseRow(
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
        if (group.Length > MaxGroupLength || !SafeGroup.IsMatch(group))
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
        foreach (var (known, suffix) in WindowKinds)
        {
            if (label.SequenceEqual(known))
            {
                return suffix;
            }
        }

        return null;
    }

    private static bool TryParsePercent(ReadOnlySpan<char> column, out int percent)
    {
        percent = 0;
        return column.Length is >= 2 and <= 4
            && column[^1] == '%'
            && int.TryParse(column[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out percent)
            && percent <= 100;
    }

    private static ProviderSubscriptionUsageMessage Closed(DateTimeOffset observedAt, string diagnostic)
        => new(
            ProviderId,
            SubscriptionUsageStatuses.Error,
            Authenticated: null,
            PlanLabel: null,
            Version: null,
            [],
            observedAt,
            Source,
            diagnostic);
}
