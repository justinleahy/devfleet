using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.SubscriptionUsage;

public enum SubscriptionQuotaReadStatus
{
    Available,
    Unavailable,
    Error,
}

/// <summary>
/// One provider quota read. <see cref="SubscriptionQuotaReadStatus.Available"/> carries nonempty
/// windows and no diagnostic; every closed status carries empty windows and a stable diagnostic.
/// </summary>
public sealed record ProviderSubscriptionQuotaReadResult(
    SubscriptionQuotaReadStatus Status,
    IReadOnlyList<SubscriptionUsageWindowMessage> Windows,
    string Source,
    string? Diagnostic,
    bool? Authenticated,
    string? PlanLabel);

public interface IProviderSubscriptionQuotaReader
{
    Task<ProviderSubscriptionQuotaReadResult> ReadPiAsync(CancellationToken cancellationToken = default);

    Task<ProviderSubscriptionQuotaReadResult> ReadClaudeAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads remaining subscription windows through the same first-party OAuth surfaces the Pi and
/// Claude Code CLIs use. Credentials come from the CLI-owned files; access tokens go only to the
/// exact HTTPS usage and token URLs below, redirects are never followed, and every file, request,
/// and body is bounded. Rotated tokens are written back the way the owning CLI writes them (Pi's
/// <c>auth.json.lock</c> directory, Claude's compare-and-swap on the refresh token; both a 0600
/// temp file plus atomic rename) so the CLI's session stays valid and a concurrent CLI write is
/// never overwritten. Credentials, account ids, and provider bodies never leave this class: only
/// the constant diagnostics below do.
/// </summary>
public sealed class ProviderSubscriptionQuotaReader : IProviderSubscriptionQuotaReader, IDisposable
{
    public const string PiSource = "chatgpt.com/backend-api/wham/usage";
    public const string ClaudeSource = "api.anthropic.com/api/oauth/usage";

    /// <summary>Credential file absent, or it holds no OAuth entry for the provider.</summary>
    public const string CredentialMissing = "credential_missing";

    /// <summary>
    /// Credential file is not a private (owner-only, owned by this process) regular file within the
    /// size bound, or reading it failed or exceeded the operation bound.
    /// </summary>
    public const string CredentialUnreadable = "credential_unreadable";
    public const string CredentialMalformed = "credential_malformed";

    /// <summary>Access token expired and no refresh token is stored.</summary>
    public const string CredentialExpired = "credential_expired";

    /// <summary>
    /// Tokens were rotated but could not be written back: the store could not be locked, replaced
    /// atomically, or kept changing underneath. The CLI session may now be stale.
    /// </summary>
    public const string CredentialPersistFailed = "credential_persist_failed";
    public const string RefreshFailed = "refresh_failed";
    public const string HttpUnauthorized = "http_unauthorized";
    public const string HttpRateLimited = "http_rate_limited";
    public const string HttpFailed = "http_failed";
    public const string HttpTimeout = "http_timeout";
    public const string HttpOversized = "http_oversized";
    public const string HttpMalformed = "http_malformed";

    /// <summary>Usage endpoint answered, but reported no rate-limit window for this account.</summary>
    public const string QuotaNotReported = "quota_not_reported";

    public const int MaxCredentialFileBytes = 256 * 1024;
    public const int MaxResponseBytes = 64 * 1024;

    /// <summary>Bound on every external wait: one HTTP exchange, one credential read, or one lock acquisition.</summary>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A token this close to expiry is refreshed before use so the request does not race the clock.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    /// <summary>
    /// A Pi lock directory not touched for this long belongs to a dead process. Pi's own async
    /// writer uses the same <c>stale</c> bound, so a lock Pi still honours is never reclaimed here.
    /// </summary>
    private static readonly TimeSpan LockStaleAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan LockRetryCeiling = TimeSpan.FromMilliseconds(250);

    private static readonly Uri PiUsageUri = new("https://chatgpt.com/backend-api/wham/usage");
    private static readonly Uri PiTokenUri = new("https://auth.openai.com/oauth/token");
    private static readonly Uri ClaudeUsageUri = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly Uri ClaudeTokenUri = new("https://platform.claude.com/v1/oauth/token");
    private const string PiClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string ClaudeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string ClaudeOAuthBeta = "oauth-2025-04-20";
    private const string PiProviderKey = "openai-codex";
    private const string ClaudeProviderKey = "claudeAiOauth";
    private const string PiJwtAuthClaim = "https://api.openai.com/auth";

    /// <summary>Only these canonical labels may reach the DTO; anything else is dropped.</summary>
    private static readonly string[] KnownPlanLabels =
        ["Free", "Plus", "Pro", "Max", "Team", "Business", "Enterprise", "Edu"];

    /// <summary>Claude usage keys in display order and the window name each becomes; unknown keys are ignored.</summary>
    private static readonly (string Key, string Name)[] ClaudeWindowKinds =
    [
        ("five_hour", "five-hour"),
        ("seven_day", "weekly"),
        ("seven_day_opus", "weekly opus"),
        ("seven_day_sonnet", "weekly sonnet"),
        ("seven_day_oauth_apps", "weekly oauth-apps"),
        ("cinder_cove", "cowork credit"),
    ];

    private static readonly string[] Rfc3339Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
    ];

    private static readonly JsonSerializerOptions PersistOptions = new() { WriteIndented = true };

    private static readonly Closed MissingFile = new(SubscriptionQuotaReadStatus.Unavailable, CredentialMissing);
    private static readonly Closed UnreadableFile = new(SubscriptionQuotaReadStatus.Error, CredentialUnreadable);
    private static readonly Closed MalformedFile = new(SubscriptionQuotaReadStatus.Error, CredentialMalformed);
    private static readonly Closed PersistFailed = new(SubscriptionQuotaReadStatus.Error, CredentialPersistFailed);

    private readonly IOptions<SubscriptionUsageOptions> _options;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _piGate = new(1, 1);
    private readonly SemaphoreSlim _claudeGate = new(1, 1);

    public ProviderSubscriptionQuotaReader(IOptions<SubscriptionUsageOptions> options, TimeProvider time)
        : this(
            options,
            time,
            new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = DefaultRequestTimeout,
            },
            DefaultRequestTimeout)
    {
    }

    internal ProviderSubscriptionQuotaReader(
        IOptions<SubscriptionUsageOptions> options,
        TimeProvider time,
        HttpMessageHandler handler,
        TimeSpan timeout)
    {
        _options = options;
        _time = time;
        _timeout = timeout;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<ProviderSubscriptionQuotaReadResult> ReadPiAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(_options.Value.PiCredentialPath);
        await _piGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadPiCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _piGate.Release();
        }
    }

    public async Task<ProviderSubscriptionQuotaReadResult> ReadClaudeAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(_options.Value.ClaudeCredentialPath);
        await _claudeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadClaudeCoreAsync(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _claudeGate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _piGate.Dispose();
        _claudeGate.Dispose();
    }

    /// <summary>
    /// A rotation whose commit found the store changed underneath (the CLI rotated first, logged
    /// out, or replaced the file) is discarded and the whole read starts over from the newer
    /// state exactly once; a second conflict fails closed rather than loop.
    /// </summary>
    private async Task<ProviderSubscriptionQuotaReadResult> ReadPiCoreAsync(string path, CancellationToken cancellationToken)
    {
        const string source = PiSource;
        var reloaded = false;
        while (true)
        {
            var file = await LoadCredentialFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (file.Failure is { } fileFailure)
            {
                return Close(source, fileFailure);
            }

            if (!TryObject(file.Root!, PiProviderKey, out var entry) || entry is null
                || !TryString(entry, "type", out var type) || type != "oauth")
            {
                return Close(source, SubscriptionQuotaReadStatus.Unavailable, CredentialMissing, authenticated: false);
            }

            if (!TryString(entry, "access", out var access) || string.IsNullOrEmpty(access)
                || !TryString(entry, "refresh", out var refresh)
                || !TryNumber(entry, "expires", out var expires) || expires is null
                || !TryString(entry, "accountId", out var accountId))
            {
                return Close(source, MalformedFile);
            }

            if (IsExpired(expires.Value))
            {
                if (string.IsNullOrEmpty(refresh))
                {
                    return Close(source, SubscriptionQuotaReadStatus.Error, CredentialExpired, authenticated: false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Post, PiTokenUri)
                {
                    Content = new FormUrlEncodedContent(
                    [
                        new KeyValuePair<string, string>("grant_type", "refresh_token"),
                        new KeyValuePair<string, string>("refresh_token", refresh),
                        new KeyValuePair<string, string>("client_id", PiClientId),
                    ]),
                };
                var rotated = await RefreshAsync(request, priorRefresh: null, cancellationToken).ConfigureAwait(false);
                if (rotated is not { } token)
                {
                    return Close(source, SubscriptionQuotaReadStatus.Error, RefreshFailed);
                }

                var jwtAccountId = AccountIdFromJwt(token.Access);
                var commit = await CommitPiAsync(path, refresh, token, jwtAccountId, cancellationToken).ConfigureAwait(false);
                if (commit == CommitStatus.Conflict && !reloaded)
                {
                    reloaded = true;
                    continue;
                }

                if (commit != CommitStatus.Committed)
                {
                    return Close(source, PersistFailed);
                }

                access = token.Access;
                accountId = jwtAccountId ?? accountId;
            }

            using var usage = new HttpRequestMessage(HttpMethod.Get, PiUsageUri);
            usage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            if (!string.IsNullOrEmpty(accountId))
            {
                usage.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", accountId);
            }

            var outcome = await SendAsync(usage, cancellationToken).ConfigureAwait(false);
            if (ClassifyUsage(outcome) is { } usageFailure)
            {
                return Close(source, usageFailure);
            }

            return ParsePiUsage(outcome.Body!, source);
        }
    }

    /// <summary>Same reload-once conflict policy as <see cref="ReadPiCoreAsync"/>, on both refresh sites.</summary>
    private async Task<ProviderSubscriptionQuotaReadResult> ReadClaudeCoreAsync(string path, CancellationToken cancellationToken)
    {
        const string source = ClaudeSource;
        var reloaded = false;
        while (true)
        {
            var file = await LoadCredentialFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (file.Failure is { } fileFailure)
            {
                return Close(source, fileFailure);
            }

            if (!TryObject(file.Root!, ClaudeProviderKey, out var entry) || entry is null)
            {
                return Close(source, SubscriptionQuotaReadStatus.Unavailable, CredentialMissing, authenticated: false);
            }

            if (!TryString(entry, "accessToken", out var access) || string.IsNullOrEmpty(access)
                || !TryString(entry, "refreshToken", out var refresh)
                || !TryNumber(entry, "expiresAt", out var expiresAt) || expiresAt is null
                || !TryString(entry, "subscriptionType", out var subscription))
            {
                return Close(source, MalformedFile);
            }

            var planLabel = SanitizePlanLabel(subscription);
            var refreshed = false;
            if (IsExpired(expiresAt.Value))
            {
                var rotation = await RotateClaudeAsync(path, refresh, cancellationToken).ConfigureAwait(false);
                if (rotation.IsConflict && !reloaded)
                {
                    reloaded = true;
                    continue;
                }

                if (rotation.Access is null)
                {
                    return Close(source, rotation.Failure ?? PersistFailed, planLabel: planLabel);
                }

                access = rotation.Access;
                refreshed = true;
            }

            var outcome = await SendClaudeUsageAsync(access, cancellationToken).ConfigureAwait(false);
            if (!refreshed && outcome.Failure is null && outcome.Status == HttpStatusCode.Unauthorized)
            {
                var rotation = await RotateClaudeAsync(path, refresh, cancellationToken).ConfigureAwait(false);
                if (rotation.IsConflict && !reloaded)
                {
                    reloaded = true;
                    continue;
                }

                if (rotation.Access is null)
                {
                    return Close(source, rotation.Failure ?? PersistFailed, planLabel: planLabel);
                }

                access = rotation.Access;
                outcome = await SendClaudeUsageAsync(access, cancellationToken).ConfigureAwait(false);
            }

            if (ClassifyUsage(outcome) is { } usageFailure)
            {
                return Close(source, usageFailure, planLabel: planLabel);
            }

            return ParseClaudeUsage(outcome.Body!, source, planLabel);
        }
    }

    /// <summary>
    /// Exchanges the Claude refresh token and commits the rotation. The token endpoint may omit
    /// <c>refresh_token</c>, in which case the prior one stays valid and is kept, as the CLI does.
    /// </summary>
    private async Task<Rotation> RotateClaudeAsync(string path, string? refresh, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refresh))
        {
            return Rotation.Fail(new Closed(SubscriptionQuotaReadStatus.Error, CredentialExpired, Authenticated: false));
        }

        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = ClaudeClientId,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ClaudeTokenUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        var rotated = await RefreshAsync(request, priorRefresh: refresh, cancellationToken).ConfigureAwait(false);
        if (rotated is not { } token)
        {
            return Rotation.Fail(new Closed(SubscriptionQuotaReadStatus.Error, RefreshFailed));
        }

        var commit = await BoundedAsync(
            () => Commit(path, ClaudeProviderKey, "refreshToken", refresh, entry =>
            {
                entry["accessToken"] = token.Access;
                entry["refreshToken"] = token.Refresh;
                entry["expiresAt"] = token.ExpiresAtMs;
            }),
            CommitStatus.Failed,
            cancellationToken).ConfigureAwait(false);
        return commit switch
        {
            CommitStatus.Committed => Rotation.Rotated(token.Access),
            CommitStatus.Conflict => Rotation.Conflict,
            _ => Rotation.Fail(PersistFailed),
        };
    }

    /// <summary>
    /// Commits a Pi rotation under the CLI's <c>auth.json.lock</c> directory so Pi's own
    /// read-merge-write cannot interleave. The account id is written only when the new token
    /// carries one; the stored value is otherwise the CLI's to manage.
    /// </summary>
    private async Task<CommitStatus> CommitPiAsync(
        string path,
        string expectedRefresh,
        RotatedToken token,
        string? jwtAccountId,
        CancellationToken cancellationToken)
    {
        var lockPath = path + ".lock";
        if (!await AcquireLockAsync(lockPath, cancellationToken).ConfigureAwait(false))
        {
            return CommitStatus.Failed;
        }

        try
        {
            return await BoundedAsync(
                () => Commit(path, PiProviderKey, "refresh", expectedRefresh, entry =>
                {
                    entry["access"] = token.Access;
                    entry["refresh"] = token.Refresh;
                    entry["expires"] = token.ExpiresAtMs;
                    if (jwtAccountId is not null)
                    {
                        entry["accountId"] = jwtAccountId;
                    }
                }),
                CommitStatus.Failed,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseLock(lockPath);
        }
    }

    private async Task<HttpOutcome> SendClaudeUsageAsync(string access, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ClaudeUsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.TryAddWithoutValidation("anthropic-beta", ClaudeOAuthBeta);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Exchanges a refresh token. Any non-2xx, transport failure, or body without a usable access
    /// token, refresh token, and lifetime is one closed outcome; the response body itself is never
    /// surfaced. <paramref name="priorRefresh"/>, when given, stands in for an omitted or empty
    /// <c>refresh_token</c>; without it the response must rotate the refresh token.
    /// </summary>
    private async Task<RotatedToken?> RefreshAsync(
        HttpRequestMessage request,
        string? priorRefresh,
        CancellationToken cancellationToken)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var outcome = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (outcome.Failure is not null || outcome.Status != HttpStatusCode.OK)
        {
            return null;
        }

        if (ParseObject(outcome.Body!) is not { } json
            || !TryString(json, "access_token", out var access) || string.IsNullOrEmpty(access)
            || !TryString(json, "refresh_token", out var rotatedRefresh)
            || !TryNumber(json, "expires_in", out var expiresIn) || expiresIn is not { } seconds || seconds <= 0)
        {
            return null;
        }

        var refresh = string.IsNullOrEmpty(rotatedRefresh) ? priorRefresh : rotatedRefresh;
        if (string.IsNullOrEmpty(refresh))
        {
            return null;
        }

        var expiresAt = _time.GetUtcNow().ToUnixTimeMilliseconds() + (long)(seconds * 1000);
        return new RotatedToken(access, refresh, expiresAt);
    }

    private ProviderSubscriptionQuotaReadResult ParsePiUsage(byte[] body, string source)
    {
        if (ParseObject(body) is not { } json
            || !TryString(json, "plan_type", out var planType)
            || !TryObject(json, "rate_limit", out var rateLimit)
            || !TryArray(json, "additional_rate_limits", out var additional))
        {
            return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true);
        }

        var planLabel = SanitizePlanLabel(planType);
        var windows = new List<SubscriptionUsageWindowMessage>(RuntimeSubscriptionUsageProbe.MaxWindows);
        if (rateLimit is not null)
        {
            if (!TryPiWindow(rateLimit, "primary_window", "primary", windows)
                || !TryPiWindow(rateLimit, "secondary_window", "secondary", windows))
            {
                return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
            }
        }

        if (additional is not null && !TryPiAdditionalLimits(additional, windows))
        {
            return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
        }

        if (windows.Count > RuntimeSubscriptionUsageProbe.MaxWindows)
        {
            return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
        }

        return windows.Count == 0
            ? Close(source, SubscriptionQuotaReadStatus.Unavailable, QuotaNotReported, authenticated: true, planLabel)
            : new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, windows, source, null, true, planLabel);
    }

    /// <summary>
    /// Appends the named window when present. Absent or null is fine; present but not an object,
    /// or missing the OpenAPI window fields, is drift. Names come from the window length so
    /// the UI shows the plan's actual windows; a repeated length falls back to the slot name.
    /// When <paramref name="namePrefix"/> is set, the display name is <c>{prefix} {duration}</c>
    /// and a collision or a ninth window fails the whole parse. <c>ResetsAt</c> is the absolute
    /// Unix <c>reset_at</c>; <c>reset_after_seconds</c> is still range-checked.
    /// </summary>
    private bool TryPiWindow(
        JsonObject rateLimit,
        string key,
        string fallbackName,
        List<SubscriptionUsageWindowMessage> windows,
        string? namePrefix = null)
    {
        if (!TryObject(rateLimit, key, out var window))
        {
            return false;
        }

        if (window is null)
        {
            return true;
        }

        if (!TryNumber(window, "used_percent", out var used) || !IsPercentage(used)
            || !TryNumber(window, "limit_window_seconds", out var length)
            || !TryNumber(window, "reset_after_seconds", out var resetAfter)
            || !TryNumber(window, "reset_at", out var resetAt)
            || !IsNonNegativeIntegral(length, out var lengthSeconds)
            || !IsNonNegativeIntegral(resetAfter, out var afterSeconds)
            || !IsNonNegativeIntegral(resetAt, out var resetAtSeconds)
            || lengthSeconds is 0 or > int.MaxValue
            || resetAtSeconds is 0 or > 253_402_300_799)
        {
            return false;
        }

        var now = _time.GetUtcNow();
        if (afterSeconds > (DateTimeOffset.MaxValue - now).TotalSeconds)
        {
            return false;
        }

        var resets = DateTimeOffset.FromUnixTimeSeconds(resetAtSeconds);

        var duration = PiWindowName(lengthSeconds, fallbackName);
        var name = namePrefix is null ? duration : string.Create(CultureInfo.InvariantCulture, $"{namePrefix} {duration}");
        if (namePrefix is null)
        {
            foreach (var existing in windows)
            {
                if (existing.Name == name)
                {
                    name = fallbackName;
                    break;
                }
            }
        }
        else
        {
            foreach (var existing in windows)
            {
                if (existing.Name == name)
                {
                    return false;
                }
            }
        }

        if (windows.Count >= RuntimeSubscriptionUsageProbe.MaxWindows)
        {
            return false;
        }

        windows.Add(new SubscriptionUsageWindowMessage(name, used, 100 - used!.Value, resets));
        return true;
    }

    private bool TryPiAdditionalLimits(JsonArray additional, List<SubscriptionUsageWindowMessage> windows)
    {
        foreach (var node in additional)
        {
            if (node is not JsonObject entry)
            {
                return false;
            }

            if (!TryString(entry, "limit_name", out var rawName)
                || !IsSafeQuotaLabel(rawName, out var limitName)
                || !TryObject(entry, "rate_limit", out var rateLimit))
            {
                return false;
            }

            if (rateLimit is null)
            {
                continue;
            }

            if (!TryPiWindow(rateLimit, "primary_window", "primary", windows, limitName)
                || !TryPiWindow(rateLimit, "secondary_window", "secondary", windows, limitName))
            {
                return false;
            }
        }

        return true;
    }

    private static string PiWindowName(double? lengthSeconds, string fallback)
    {
        if (lengthSeconds is not { } seconds || seconds <= 0 || seconds > int.MaxValue || seconds != Math.Floor(seconds))
        {
            return fallback;
        }

        var total = (long)seconds;
        return total switch
        {
            18_000 => "five-hour",
            604_800 => "weekly",
            _ when total % 86_400 == 0 => string.Create(CultureInfo.InvariantCulture, $"{total / 86_400}-day"),
            _ when total % 3_600 == 0 => string.Create(CultureInfo.InvariantCulture, $"{total / 3_600}-hour"),
            _ => fallback,
        };
    }

    /// <summary>
    /// Claude reports <c>utilization</c> in percentage points (0–100), matching the
    /// <c>used_percentage</c> of its public statusline schema; it is used as-is.
    /// </summary>
    private static ProviderSubscriptionQuotaReadResult ParseClaudeUsage(byte[] body, string source, string? planLabel)
    {
        if (ParseObject(body) is not { } json || !TryArray(json, "limits", out var limits))
        {
            return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
        }

        var windows = new List<SubscriptionUsageWindowMessage>(RuntimeSubscriptionUsageProbe.MaxWindows);
        foreach (var (key, name) in ClaudeWindowKinds)
        {
            if (!TryObject(json, key, out var window))
            {
                return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
            }

            if (window is null)
            {
                continue;
            }

            if (!TryNumber(window, "utilization", out var utilization) || !IsPercentage(utilization)
                || !TryString(window, "resets_at", out var resetsAt)
                || !TryRfc3339Reset(resetsAt, out var resets))
            {
                return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
            }

            if (windows.Count >= RuntimeSubscriptionUsageProbe.MaxWindows)
            {
                return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
            }

            var used = utilization!.Value;
            windows.Add(new SubscriptionUsageWindowMessage(name, used, 100 - used, resets));
        }

        if (limits is not null && !TryClaudeLimits(limits, windows))
        {
            return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
        }

        if (windows.Count > RuntimeSubscriptionUsageProbe.MaxWindows)
        {
            return Close(source, SubscriptionQuotaReadStatus.Error, HttpMalformed, authenticated: true, planLabel);
        }

        return windows.Count == 0
            ? Close(source, SubscriptionQuotaReadStatus.Unavailable, QuotaNotReported, authenticated: true, planLabel)
            : new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, windows, source, null, true, planLabel);
    }

    private static bool TryClaudeLimits(JsonArray limits, List<SubscriptionUsageWindowMessage> windows)
    {
        foreach (var node in limits)
        {
            if (node is not JsonObject row)
            {
                return false;
            }

            if (!TryString(row, "kind", out var kind))
            {
                return false;
            }

            if (kind != "weekly_scoped")
            {
                continue;
            }

            if (!TryObject(row, "scope", out var scope) || scope is null
                || !TryObject(scope, "model", out var model) || model is null
                || !TryString(model, "display_name", out var displayName) || displayName is null)
            {
                return false;
            }


            if (!IsSafeQuotaLabel(displayName, out var label)
                || !TryNumber(row, "percent", out var percent) || !IsPercentage(percent)
                || !TryString(row, "resets_at", out var resetsAt)
                || !TryRfc3339Reset(resetsAt, out var resets))
            {
                return false;
            }

            var name = string.Create(CultureInfo.InvariantCulture, $"weekly {label}");
            foreach (var existing in windows)
            {
                if (existing.Name == name)
                {
                    return false;
                }
            }

            if (windows.Count >= RuntimeSubscriptionUsageProbe.MaxWindows)
            {
                return false;
            }

            var used = percent!.Value;
            windows.Add(new SubscriptionUsageWindowMessage(name, used, 100 - used, resets));
        }

        return true;
    }

    private static bool TryRfc3339Reset(string? resetsAt, out DateTimeOffset? resets)
    {
        resets = null;
        if (resetsAt is null)
        {
            return true;
        }

        if (!DateTimeOffset.TryParseExact(
                resetsAt, Rfc3339Formats, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return false;
        }

        resets = parsed;
        return true;
    }

    private static bool IsPercentage(double? value)
        => value is { } percent && double.IsFinite(percent) && percent is >= 0 and <= 100;

    private static bool IsNonNegativeIntegral(double? value, out long integral)
    {
        integral = 0;
        if (value is not { } number
            || !double.IsFinite(number)
            || number < 0
            || number > long.MaxValue
            || number != Math.Floor(number))
        {
            return false;
        }

        integral = (long)number;
        return true;
    }

    private bool IsExpired(double expiresAtMs)
        => !double.IsFinite(expiresAtMs)
           || expiresAtMs <= _time.GetUtcNow().ToUnixTimeMilliseconds() + ExpirySkew.TotalMilliseconds;

    /// <summary>
    /// Sends to one of the four constant HTTPS URLs with a bounded wait and body. 3xx is a failure
    /// like any other non-2xx: the handler never follows redirects, so a credential cannot be
    /// replayed to another host. Caller cancellation propagates; the request deadline does not.
    /// </summary>
    private async Task<HttpOutcome> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps
            || (uri != PiUsageUri && uri != PiTokenUri && uri != ClaudeUsageUri && uri != ClaudeTokenUri))
        {
            throw new InvalidOperationException("Quota reader may only call its fixed provider URLs.");
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                return new HttpOutcome(HttpOversized, response.StatusCode, null);
            }

            var body = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
            return body is null
                ? new HttpOutcome(HttpOversized, response.StatusCode, null)
                : new HttpOutcome(null, response.StatusCode, body);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new HttpOutcome(HttpTimeout, default, null);
        }
        catch (HttpRequestException)
        {
            return new HttpOutcome(HttpFailed, default, null);
        }
        catch (IOException)
        {
            return new HttpOutcome(HttpFailed, default, null);
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxResponseBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffer[..total];
            }

            total += read;
        }

        return null;
    }

    private static Closed? ClassifyUsage(HttpOutcome outcome)
    {
        if (outcome.Failure is not null)
        {
            return new Closed(SubscriptionQuotaReadStatus.Error, outcome.Failure);
        }

        return outcome.Status switch
        {
            HttpStatusCode.OK => null,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new Closed(SubscriptionQuotaReadStatus.Error, HttpUnauthorized, Authenticated: false),
            HttpStatusCode.TooManyRequests => new Closed(SubscriptionQuotaReadStatus.Error, HttpRateLimited),
            _ => new Closed(SubscriptionQuotaReadStatus.Error, HttpFailed),
        };
    }

    // ----- credential store -----

    /// <summary>
    /// Runs one synchronous store operation off the caller's thread so a hung filesystem or a
    /// special file that ignores non-blocking open can neither outlive caller cancellation nor
    /// exceed the operation bound; on timeout the operation is abandoned and
    /// <paramref name="onTimeout"/> is reported.
    /// </summary>
    private async Task<T> BoundedAsync<T>(Func<T> storeOperation, T onTimeout, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(storeOperation, cancellationToken)
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return onTimeout;
        }
    }

    private Task<CredentialFile> LoadCredentialFileAsync(string path, CancellationToken cancellationToken)
        => path.Length == 0
            ? Task.FromResult(new CredentialFile(null, MissingFile))
            : BoundedAsync(() => LoadCredentialFile(path), new CredentialFile(null, UnreadableFile), cancellationToken);

    /// <summary>
    /// Reads the credential store as a private, bounded regular file. A missing path or a
    /// directory is simply "not signed in"; a symlink, pipe, device, oversized file, file not owned
    /// by this process or readable by anyone else, or IO failure is unreadable; bad JSON is
    /// malformed. On Linux the path is opened without following links and the opened descriptor
    /// must be the same regular inode the lookup saw, so a swap between check and use is refused.
    /// </summary>
    private static CredentialFile LoadCredentialFile(string path)
    {
        byte[] bytes;
        try
        {
            var failure = OperatingSystem.IsLinux()
                ? ReadPrivateFileLinux(path, out bytes)
                : ReadPrivateFilePortable(path, out bytes);
            if (failure is { } closed)
            {
                return new CredentialFile(null, closed);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CredentialFile(null, UnreadableFile);
        }

        return ParseObject(bytes) is { } parsed
            ? new CredentialFile(parsed, null)
            : new CredentialFile(null, MalformedFile);
    }

    private static Closed? ReadPrivateFileLinux(string path, out byte[] bytes)
    {
        bytes = [];
        var stat = new byte[Native.StatxSize];
        if (Native.statx(Native.AtFdCwd, path, Native.AtSymlinkNoFollow, Native.StatxBasicStats, stat) != 0)
        {
            return Marshal.GetLastPInvokeError() is Native.Enoent or Native.Enotdir ? MissingFile : UnreadableFile;
        }

        var looked = FileIdentity.Parse(stat);
        if (looked.IsDirectory)
        {
            return MissingFile;
        }

        if (!IsPrivateRegularFile(looked))
        {
            return UnreadableFile;
        }

        var fd = Native.open(path, Native.OpenPrivateReadFlags, 0);
        if (fd < 0)
        {
            return UnreadableFile;
        }

        using var handle = new SafeFileHandle(fd, ownsHandle: true);
        if (Native.statx(fd, string.Empty, Native.AtEmptyPath, Native.StatxBasicStats, stat) != 0)
        {
            return UnreadableFile;
        }

        var opened = FileIdentity.Parse(stat);
        if (!opened.SameInode(looked) || !IsPrivateRegularFile(opened) || opened.Size > MaxCredentialFileBytes)
        {
            return UnreadableFile;
        }

        return ReadBounded(handle, out bytes);
    }

    private static bool IsPrivateRegularFile(FileIdentity identity)
        => identity.IsRegular && identity.Uid == Native.geteuid() && (identity.Mode & Native.GroupOrOtherBits) == 0;

    private static Closed? ReadPrivateFilePortable(string path, out byte[] bytes)
    {
        bytes = [];
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            return UnreadableFile;
        }

        if (!info.Exists)
        {
            return MissingFile;
        }

        if (!OperatingSystem.IsWindows() && (info.UnixFileMode & GroupOrOtherModes) != 0)
        {
            return UnreadableFile;
        }

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return RandomAccess.GetLength(handle) > MaxCredentialFileBytes ? UnreadableFile : ReadBounded(handle, out bytes);
    }

    private const UnixFileMode GroupOrOtherModes =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    /// <summary>Reads through the verified descriptor; growth past the bound during the read is refused.</summary>
    private static Closed? ReadBounded(SafeFileHandle handle, out byte[] bytes)
    {
        var buffer = new byte[MaxCredentialFileBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = RandomAccess.Read(handle, buffer.AsSpan(total), total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > MaxCredentialFileBytes)
        {
            bytes = [];
            return UnreadableFile;
        }

        bytes = buffer[..total];
        return null;
    }

    /// <summary>
    /// Compare-and-swap commit: rereads the store as it is now, requires the provider entry to
    /// still hold the refresh token the exchange consumed, applies only the rotated fields to that
    /// latest document, and replaces the file atomically. Any other current state (the CLI rotated
    /// first, logged out, or the file is no longer a private regular file) is a conflict and the
    /// rotation is dropped; nothing newer is ever overwritten.
    /// </summary>
    private static CommitStatus Commit(
        string path,
        string providerKey,
        string refreshField,
        string expectedRefresh,
        Action<JsonObject> rotate)
    {
        if (LoadCredentialFile(path).Root is not { } latest
            || !TryObject(latest, providerKey, out var entry) || entry is null
            || !TryString(entry, refreshField, out var current) || current != expectedRefresh)
        {
            return CommitStatus.Conflict;
        }

        rotate(entry);
        return Replace(path, latest) ? CommitStatus.Committed : CommitStatus.Failed;
    }

    /// <summary>
    /// Owner-only temp file in the same directory, flushed to disk, then renamed over the store and
    /// the directory synced. There is no in-place fallback: a store that cannot be replaced
    /// atomically is left exactly as it was.
    /// </summary>
    private static bool Replace(string path, JsonObject root)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, PersistOptions);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.WriteThrough,
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (var stream = new FileStream(temp, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }

        SyncDirectory(Path.GetDirectoryName(path));
        return true;
    }

    /// <summary>Makes the rename itself durable. Best effort: the data is already on disk and the rename done.</summary>
    private static void SyncDirectory(string? directory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(directory))
        {
            return;
        }

        var fd = Native.open(directory, Native.OpenDirectoryFlags, 0);
        if (fd < 0)
        {
            return;
        }

        _ = Native.fsync(fd);
        _ = Native.close(fd);
    }

    // ----- Pi store lock (proper-lockfile convention) -----

    /// <summary>
    /// Takes the lock Pi's own writer takes: an atomically created directory at
    /// <c>&lt;auth.json&gt;.lock</c>, held only while it exists. A lock older than
    /// <see cref="LockStaleAfter"/> is a crashed holder's and is reclaimed; a live one is awaited
    /// with backoff up to the operation bound, after which the rotation is abandoned.
    /// </summary>
    private async Task<bool> AcquireLockAsync(string lockPath, CancellationToken cancellationToken)
    {
        var started = Environment.TickCount64;
        var delay = TimeSpan.FromMilliseconds(10);
        while (true)
        {
            switch (TryCreateLock(lockPath))
            {
                case LockAttempt.Acquired:
                    return true;
                case LockAttempt.Failed:
                    return false;
            }

            if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(lockPath) > LockStaleAfter)
            {
                TryRemoveLock(lockPath);
                continue;
            }

            if (Environment.TickCount64 - started >= _timeout.TotalMilliseconds)
            {
                return false;
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            if (delay < LockRetryCeiling)
            {
                delay *= 2;
            }
        }
    }

    private static LockAttempt TryCreateLock(string lockPath)
    {
        if (OperatingSystem.IsWindows())
        {
            if (Directory.Exists(lockPath))
            {
                return LockAttempt.Held;
            }

            try
            {
                Directory.CreateDirectory(lockPath);
                return LockAttempt.Acquired;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return LockAttempt.Failed;
            }
        }

        if (Native.mkdir(lockPath, Native.LockDirectoryMode) == 0)
        {
            return LockAttempt.Acquired;
        }

        return Marshal.GetLastPInvokeError() == Native.Eexist ? LockAttempt.Held : LockAttempt.Failed;
    }

    private static void ReleaseLock(string lockPath) => TryRemoveLock(lockPath);

    private static void TryRemoveLock(string lockPath)
    {
        try
        {
            Directory.Delete(lockPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ----- parsing helpers -----

    /// <summary>Reads the ChatGPT account id claim from the access token; null on any deviation.</summary>
    private static string? AccountIdFromJwt(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PiJwtAuthClaim, out var auth)
                && auth.ValueKind == JsonValueKind.Object
                && auth.TryGetProperty("chatgpt_account_id", out var id)
                && id.ValueKind == JsonValueKind.String)
            {
                var value = id.GetString();
                return string.IsNullOrEmpty(value) ? null : value;
            }
        }
        catch (FormatException)
        {
        }
        catch (JsonException)
        {
        }

        return null;
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

    private static JsonObject? ParseObject(byte[] bytes)
    {
        var span = bytes.AsSpan();
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        try
        {
            return JsonNode.Parse(
                span,
                documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>False only when the property is present with a non-string, non-null value.</summary>
    private static bool TryString(JsonObject obj, string name, out string? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonValue leaf && leaf.TryGetValue<string>(out var text))
        {
            value = text;
            return true;
        }

        return false;
    }

    /// <summary>False only when the property is present with a non-numeric, non-null value.</summary>
    private static bool TryNumber(JsonObject obj, string name, out double? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonValue leaf && leaf.GetValueKind() == JsonValueKind.Number
            && leaf.TryGetValue<double>(out var number) && double.IsFinite(number))
        {
            value = number;
            return true;
        }

        return false;
    }

    /// <summary>False only when the property is present with a non-object, non-null value.</summary>
    private static bool TryObject(JsonObject obj, string name, out JsonObject? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonObject nested)
        {
            value = nested;
            return true;
        }

        return false;
    }

    /// <summary>False only when the property is present with a non-array, non-null value.</summary>
    private static bool TryArray(JsonObject obj, string name, out JsonArray? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonArray nested)
        {
            value = nested;
            return true;
        }

        return false;
    }

    /// <summary>Provider window labels must be short, printable, and free of control characters.</summary>
    private const int MaxQuotaLabelLength = 32;

    private static bool IsSafeQuotaLabel(string? raw, out string label)
    {
        label = null!;
        if (raw is null)
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length is 0 or > MaxQuotaLabelLength)
        {
            return false;
        }

        var pendingSeparator = false;
        var hasWord = false;
        foreach (var c in trimmed)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                hasWord = true;
                pendingSeparator = false;
                continue;
            }

            if ((c is ' ' or '.' or '+' or '/' or '-') && hasWord && !pendingSeparator)
            {
                pendingSeparator = true;
                continue;
            }

            return false;
        }

        if (!hasWord || pendingSeparator)
        {
            return false;
        }

        label = trimmed;
        return true;
    }

    private static string ResolvePath(string configured)
    {
        var expanded = NodeOptionsPostConfigure.ExpandPath(configured);
        return string.IsNullOrWhiteSpace(expanded) ? string.Empty : Path.GetFullPath(expanded);
    }

    private static ProviderSubscriptionQuotaReadResult Close(string source, Closed closed, string? planLabel = null)
        => Close(source, closed.Status, closed.Diagnostic, closed.Authenticated, planLabel);

    private static ProviderSubscriptionQuotaReadResult Close(
        string source,
        SubscriptionQuotaReadStatus status,
        string diagnostic,
        bool? authenticated = null,
        string? planLabel = null)
        => new(status, [], source, diagnostic, authenticated, planLabel);

    private readonly record struct Closed(SubscriptionQuotaReadStatus Status, string Diagnostic, bool? Authenticated = null);

    private readonly record struct RotatedToken(string Access, string Refresh, long ExpiresAtMs);

    /// <summary><see cref="Failure"/> set means no HTTP answer was usable; otherwise <see cref="Body"/> is bounded.</summary>
    private readonly record struct HttpOutcome(string? Failure, HttpStatusCode Status, byte[]? Body);

    /// <summary>Exactly one of <see cref="Root"/> and <see cref="Failure"/> is set.</summary>
    private readonly record struct CredentialFile(JsonObject? Root, Closed? Failure);

    private enum CommitStatus
    {
        Committed,
        Conflict,
        Failed,
    }

    private enum LockAttempt
    {
        Acquired,
        Held,
        Failed,
    }

    /// <summary>
    /// A Claude refresh plus commit: <see cref="Access"/> on success, <see cref="Failure"/> when
    /// closed, neither when the store changed underneath and the caller must reload.
    /// </summary>
    private readonly record struct Rotation(string? Access, Closed? Failure)
    {
        public static readonly Rotation Conflict = default;

        public bool IsConflict => Access is null && Failure is null;

        public static Rotation Rotated(string access) => new(access, null);

        public static Rotation Fail(Closed failure) => new(null, failure);
    }

    /// <summary>The fields of <c>struct statx</c> this reader checks; the layout is architecture-independent.</summary>
    private readonly record struct FileIdentity(ushort Mode, uint Uid, ulong Inode, uint DevMajor, uint DevMinor, long Size)
    {
        public static FileIdentity Parse(ReadOnlySpan<byte> statx) => new(
            MemoryMarshal.Read<ushort>(statx[28..]),
            MemoryMarshal.Read<uint>(statx[20..]),
            MemoryMarshal.Read<ulong>(statx[32..]),
            MemoryMarshal.Read<uint>(statx[136..]),
            MemoryMarshal.Read<uint>(statx[140..]),
            MemoryMarshal.Read<long>(statx[40..]));

        public bool IsRegular => (Mode & Native.FileTypeMask) == Native.RegularFile;

        public bool IsDirectory => (Mode & Native.FileTypeMask) == Native.DirectoryFile;

        public bool SameInode(FileIdentity other)
            => Inode == other.Inode && DevMajor == other.DevMajor && DevMinor == other.DevMinor;
    }

    /// <summary>
    /// The few libc calls .NET does not expose: no-follow/non-blocking open, descriptor
    /// identity, atomic directory creation, and directory sync. Only <see cref="mkdir"/>,
    /// <see cref="geteuid"/>, and <see cref="close"/> are portable across Unixes; the rest is
    /// called on Linux only, whose flag values and <c>struct statx</c> layout are stable ABI.
    /// </summary>
    private static class Native
    {
        public const int StatxSize = 256;
        public const int AtFdCwd = -100;
        public const int AtSymlinkNoFollow = 0x100;
        public const int AtEmptyPath = 0x1000;
        public const uint StatxBasicStats = 0x7ff;
        public const int Enoent = 2;
        public const int Eexist = 17;
        public const int Enotdir = 20;
        public const ushort FileTypeMask = 0xF000;
        public const ushort RegularFile = 0x8000;
        public const ushort DirectoryFile = 0x4000;
        public const ushort GroupOrOtherBits = 0x077;
        public const uint LockDirectoryMode = 0x1ED; // 0755, as proper-lockfile's mkdir default under umask

        private const int ReadOnly = 0x0;
        private const int NoControllingTty = 0x100;
        private const int NonBlocking = 0x800;
        private const int CloseOnExec = 0x80000;

        /// <summary>Linux keeps arm's historical value for <c>O_NOFOLLOW</c>; every other architecture uses the generic one.</summary>
        private static readonly int NoFollow =
            RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64 ? 0x8000 : 0x20000;

        public static readonly int OpenPrivateReadFlags = ReadOnly | NoControllingTty | NonBlocking | CloseOnExec | NoFollow;
        public const int OpenDirectoryFlags = ReadOnly | CloseOnExec;

        [DllImport("libc", SetLastError = true)]
        public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

        [DllImport("libc", SetLastError = true)]
        public static extern int statx(
            int dirfd,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            byte[] buffer);

        [DllImport("libc", SetLastError = true)]
        public static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

        [DllImport("libc")]
        public static extern int fsync(int fd);

        [DllImport("libc")]
        public static extern int close(int fd);

        [DllImport("libc")]
        public static extern uint geteuid();
    }
}
