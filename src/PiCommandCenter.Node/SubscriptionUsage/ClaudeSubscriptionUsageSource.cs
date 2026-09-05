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

/// <summary>
/// Reads Claude subscription limits from Claude Code's private OAuth credential store. Access
/// tokens are sent only to Anthropic's fixed usage endpoint; refresh tokens are sent only to its
/// fixed token endpoint. Redirects are disabled and neither credentials nor provider bodies are
/// projected into the result.
/// </summary>
public sealed class ClaudeSubscriptionUsageSource : ISupplementalSubscriptionUsageSource, IDisposable
{
    public const string Source = "api.anthropic.com/api/oauth/usage";
    public const string CredentialUnreadable = "credential_unreadable";
    public const string CredentialMalformed = "credential_malformed";
    public const string CredentialExpired = "credential_expired";
    public const string CredentialPersistFailed = "credential_persist_failed";
    public const string RefreshFailed = "refresh_failed";
    public const string HttpUnauthorized = "http_unauthorized";
    public const string HttpRateLimited = "http_rate_limited";
    public const string HttpFailed = "http_failed";
    public const string HttpTimeout = "http_timeout";
    public const string HttpOversized = "http_oversized";
    public const string HttpMalformed = "http_malformed";
    public const string QuotaNotReported = "quota_not_reported";

    public const int MaxCredentialFileBytes = 256 * 1024;
    public const int MaxResponseBytes = 64 * 1024;

    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);

    private static readonly Uri UsageUri = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly Uri TokenUri = new("https://platform.claude.com/v1/oauth/token");

    private const string ProviderId = "anthropic";
    private const string CredentialKey = "claudeAiOauth";
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string OAuthBeta = "oauth-2025-04-20";
    private const int MaxQuotaLabelLength = 32;

    private static readonly string[] KnownPlanLabels =
        ["Free", "Plus", "Pro", "Max", "Team", "Business", "Enterprise", "Edu"];

    private static readonly (string Key, string Name)[] WindowKinds =
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

    private readonly IOptions<SubscriptionUsageOptions> _options;
    private readonly TimeProvider _time;
    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClaudeSubscriptionUsageSource(
        IOptions<SubscriptionUsageOptions> options,
        TimeProvider time)
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

    internal ClaudeSubscriptionUsageSource(
        IOptions<SubscriptionUsageOptions> options,
        TimeProvider time,
        HttpMessageHandler handler,
        TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(handler);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        _options = options;
        _time = time;
        _timeout = timeout;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Provider => ProviderId;

    public async Task<ProviderSubscriptionUsageMessage?> ReadAsync(
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(_options.Value.ClaudeCredentialPath);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(path, observedAt, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// If Claude Code replaces the credential store during a refresh, discard the stale rotation
    /// and reload once. A second conflict closes instead of overwriting newer credentials.
    /// </summary>
    private async Task<ProviderSubscriptionUsageMessage?> ReadCoreAsync(
        string path,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        var reloaded = false;
        while (true)
        {
            var file = await LoadCredentialFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (file.Status == CredentialFileStatus.Missing)
            {
                return null;
            }

            if (file.Status == CredentialFileStatus.Unreadable)
            {
                return Close(observedAt, CredentialUnreadable);
            }

            if (file.Status == CredentialFileStatus.Malformed)
            {
                return Close(observedAt, CredentialMalformed);
            }

            if (!TryObject(file.Root!, CredentialKey, out var entry))
            {
                return Close(observedAt, CredentialMalformed);
            }

            if (entry is null)
            {
                return null;
            }

            if (!TryString(entry, "accessToken", out var access) || string.IsNullOrEmpty(access)
                || !TryString(entry, "refreshToken", out var refresh)
                || !TryNumber(entry, "expiresAt", out var expiresAt) || expiresAt is null
                || !TryString(entry, "subscriptionType", out var subscription))
            {
                return Close(observedAt, CredentialMalformed);
            }

            var planLabel = SanitizePlanLabel(subscription);
            var refreshed = false;
            if (IsExpired(expiresAt.Value))
            {
                var rotation = await RotateAsync(path, refresh, cancellationToken).ConfigureAwait(false);
                if (rotation.IsConflict && !reloaded)
                {
                    reloaded = true;
                    continue;
                }

                if (rotation.Access is null)
                {
                    return Close(
                        observedAt,
                        rotation.Diagnostic ?? CredentialPersistFailed,
                        rotation.Authenticated,
                        planLabel);
                }

                access = rotation.Access;
                refreshed = true;
            }

            var outcome = await SendUsageAsync(access, cancellationToken).ConfigureAwait(false);
            if (!refreshed && outcome.Failure is null && outcome.Status == HttpStatusCode.Unauthorized)
            {
                var rotation = await RotateAsync(path, refresh, cancellationToken).ConfigureAwait(false);
                if (rotation.IsConflict && !reloaded)
                {
                    reloaded = true;
                    continue;
                }

                if (rotation.Access is null)
                {
                    return Close(
                        observedAt,
                        rotation.Diagnostic ?? CredentialPersistFailed,
                        rotation.Authenticated,
                        planLabel);
                }

                outcome = await SendUsageAsync(rotation.Access, cancellationToken).ConfigureAwait(false);
            }

            if (ClassifyUsage(outcome) is { } failure)
            {
                return Close(observedAt, failure.Diagnostic, failure.Authenticated, planLabel);
            }

            return ParseUsage(outcome.Body!, observedAt, planLabel);
        }
    }

    private async Task<Rotation> RotateAsync(
        string path,
        string? refresh,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(refresh))
        {
            return Rotation.Fail(CredentialExpired, authenticated: false);
        }

        var body = new JsonObject
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refresh,
            ["client_id"] = ClientId,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUri)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var outcome = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (outcome.Failure is not null || outcome.Status != HttpStatusCode.OK
            || ParseObject(outcome.Body!) is not { } json
            || !TryString(json, "access_token", out var access) || string.IsNullOrEmpty(access)
            || !TryString(json, "refresh_token", out var rotatedRefresh)
            || !TryNumber(json, "expires_in", out var expiresIn)
            || expiresIn is not { } seconds || seconds <= 0)
        {
            return Rotation.Fail(RefreshFailed);
        }

        var nextRefresh = string.IsNullOrEmpty(rotatedRefresh) ? refresh : rotatedRefresh;
        var remainingMilliseconds = (DateTimeOffset.MaxValue - _time.GetUtcNow()).TotalMilliseconds;
        if (seconds > remainingMilliseconds / 1000)
        {
            return Rotation.Fail(RefreshFailed);
        }

        var expiresAt = _time.GetUtcNow().ToUnixTimeMilliseconds() + (long)(seconds * 1000);
        var commit = await BoundedAsync(
            () => Commit(path, refresh, entry =>
            {
                entry["accessToken"] = access;
                entry["refreshToken"] = nextRefresh;
                entry["expiresAt"] = expiresAt;
            }),
            CommitStatus.Failed,
            cancellationToken).ConfigureAwait(false);

        return commit switch
        {
            CommitStatus.Committed => Rotation.Rotated(access),
            CommitStatus.Conflict => Rotation.Conflict,
            _ => Rotation.Fail(CredentialPersistFailed),
        };
    }

    private async Task<HttpOutcome> SendUsageAsync(
        string access,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
        return await SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpOutcome> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var uri = request.RequestUri;
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps || (uri != UsageUri && uri != TokenUri))
        {
            throw new InvalidOperationException("Claude usage source may only call its fixed provider URLs.");
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

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
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
            return new Closed(outcome.Failure);
        }

        return outcome.Status switch
        {
            HttpStatusCode.OK => null,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
                new Closed(HttpUnauthorized, Authenticated: false),
            HttpStatusCode.TooManyRequests => new Closed(HttpRateLimited),
            _ => new Closed(HttpFailed),
        };
    }

    private static ProviderSubscriptionUsageMessage ParseUsage(
        byte[] body,
        DateTimeOffset observedAt,
        string? planLabel)
    {
        if (ParseObject(body) is not { } json || !TryArray(json, "limits", out var limits))
        {
            return Close(observedAt, HttpMalformed, authenticated: true, planLabel);
        }

        var windows = new List<SubscriptionUsageWindowMessage>(RuntimeSubscriptionUsageProbe.MaxWindows);
        foreach (var (key, name) in WindowKinds)
        {
            if (!TryObject(json, key, out var window))
            {
                return Close(observedAt, HttpMalformed, authenticated: true, planLabel);
            }

            if (window is null)
            {
                continue;
            }

            if (!TryNumber(window, "utilization", out var utilization) || !IsPercentage(utilization)
                || !TryString(window, "resets_at", out var resetsAt)
                || !TryRfc3339Reset(resetsAt, out var resets)
                || windows.Count >= RuntimeSubscriptionUsageProbe.MaxWindows)
            {
                return Close(observedAt, HttpMalformed, authenticated: true, planLabel);
            }

            var used = utilization!.Value;
            windows.Add(new SubscriptionUsageWindowMessage(name, used, 100 - used, resets));
        }

        if (limits is not null && !TryScopedLimits(limits, windows))
        {
            return Close(observedAt, HttpMalformed, authenticated: true, planLabel);
        }

        return windows.Count == 0
            ? new ProviderSubscriptionUsageMessage(
                ProviderId,
                SubscriptionUsageStatuses.Unavailable,
                Authenticated: true,
                planLabel,
                Version: null,
                [],
                observedAt,
                Source,
                QuotaNotReported)
            : new ProviderSubscriptionUsageMessage(
                ProviderId,
                SubscriptionUsageStatuses.Available,
                Authenticated: true,
                planLabel,
                Version: null,
                windows,
                observedAt,
                Source,
                Diagnostic: null);
    }

    private static bool TryScopedLimits(
        JsonArray limits,
        List<SubscriptionUsageWindowMessage> windows)
    {
        foreach (var node in limits)
        {
            if (node is not JsonObject row || !TryString(row, "kind", out var kind))
            {
                return false;
            }

            if (kind != "weekly_scoped")
            {
                continue;
            }

            if (!TryObject(row, "scope", out var scope) || scope is null
                || !TryObject(scope, "model", out var model) || model is null
                || !TryString(model, "display_name", out var displayName)
                || !IsSafeQuotaLabel(displayName, out var label)
                || !TryNumber(row, "percent", out var percent) || !IsPercentage(percent)
                || !TryString(row, "resets_at", out var resetsAt)
                || !TryRfc3339Reset(resetsAt, out var resets)
                || windows.Count >= RuntimeSubscriptionUsageProbe.MaxWindows)
            {
                return false;
            }

            var name = string.Create(CultureInfo.InvariantCulture, $"weekly {label}");
            if (windows.Any(existing => existing.Name == name))
            {
                return false;
            }

            var used = percent!.Value;
            windows.Add(new SubscriptionUsageWindowMessage(name, used, 100 - used, resets));
        }

        return true;
    }

    private static bool TryRfc3339Reset(string? value, out DateTimeOffset? resetsAt)
    {
        resetsAt = null;
        if (value is null)
        {
            return true;
        }

        if (!DateTimeOffset.TryParseExact(
                value,
                Rfc3339Formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return false;
        }

        resetsAt = parsed;
        return true;
    }

    private static bool IsPercentage(double? value)
        => value is { } percent && double.IsFinite(percent) && percent is >= 0 and <= 100;

    private bool IsExpired(double expiresAtMilliseconds)
        => !double.IsFinite(expiresAtMilliseconds)
           || expiresAtMilliseconds <= _time.GetUtcNow().ToUnixTimeMilliseconds() + ExpirySkew.TotalMilliseconds;

    private async Task<T> BoundedAsync<T>(
        Func<T> operation,
        T timeoutResult,
        CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(operation, cancellationToken)
                .WaitAsync(_timeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return timeoutResult;
        }
    }

    private Task<CredentialFile> LoadCredentialFileAsync(
        string path,
        CancellationToken cancellationToken)
        => path.Length == 0
            ? Task.FromResult(CredentialFile.Missing)
            : BoundedAsync(
                () => LoadCredentialFile(path),
                CredentialFile.Unreadable,
                cancellationToken);

    private static CredentialFile LoadCredentialFile(string path)
    {
        byte[] bytes;
        try
        {
            var status = OperatingSystem.IsLinux()
                ? ReadPrivateFileLinux(path, out bytes)
                : ReadPrivateFilePortable(path, out bytes);
            if (status != CredentialFileStatus.Valid)
            {
                return new CredentialFile(null, status);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CredentialFile.Unreadable;
        }

        return ParseObject(bytes) is { } root
            ? new CredentialFile(root, CredentialFileStatus.Valid)
            : CredentialFile.Malformed;
    }

    private static CredentialFileStatus ReadPrivateFileLinux(string path, out byte[] bytes)
    {
        bytes = [];
        var stat = new byte[Native.StatxSize];
        if (Native.statx(Native.AtFdCwd, path, Native.AtSymlinkNoFollow, Native.StatxBasicStats, stat) != 0)
        {
            return Marshal.GetLastPInvokeError() is Native.Enoent or Native.Enotdir
                ? CredentialFileStatus.Missing
                : CredentialFileStatus.Unreadable;
        }

        var lookedUp = FileIdentity.Parse(stat);
        if (lookedUp.IsDirectory)
        {
            return CredentialFileStatus.Missing;
        }

        if (!IsPrivateRegularFile(lookedUp))
        {
            return CredentialFileStatus.Unreadable;
        }

        var descriptor = Native.open(path, Native.OpenPrivateReadFlags, 0);
        if (descriptor < 0)
        {
            return CredentialFileStatus.Unreadable;
        }

        using var handle = new SafeFileHandle(descriptor, ownsHandle: true);
        if (Native.statx(descriptor, string.Empty, Native.AtEmptyPath, Native.StatxBasicStats, stat) != 0)
        {
            return CredentialFileStatus.Unreadable;
        }

        var opened = FileIdentity.Parse(stat);
        if (!opened.SameInode(lookedUp)
            || !IsPrivateRegularFile(opened)
            || opened.Size > MaxCredentialFileBytes)
        {
            return CredentialFileStatus.Unreadable;
        }

        return ReadBounded(handle, out bytes);
    }

    private static bool IsPrivateRegularFile(FileIdentity identity)
        => identity.IsRegular
           && identity.Uid == Native.geteuid()
           && (identity.Mode & Native.GroupOrOtherBits) == 0;

    private static CredentialFileStatus ReadPrivateFilePortable(string path, out byte[] bytes)
    {
        bytes = [];
        var info = new FileInfo(path);
        if (info.LinkTarget is not null)
        {
            return CredentialFileStatus.Unreadable;
        }

        if (!info.Exists)
        {
            return CredentialFileStatus.Missing;
        }

        if (!OperatingSystem.IsWindows() && (info.UnixFileMode & GroupOrOtherModes) != 0)
        {
            return CredentialFileStatus.Unreadable;
        }

        using var handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return RandomAccess.GetLength(handle) > MaxCredentialFileBytes
            ? CredentialFileStatus.Unreadable
            : ReadBounded(handle, out bytes);
    }

    private const UnixFileMode GroupOrOtherModes =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private static CredentialFileStatus ReadBounded(SafeFileHandle handle, out byte[] bytes)
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
            return CredentialFileStatus.Unreadable;
        }

        bytes = buffer[..total];
        return CredentialFileStatus.Valid;
    }

    private static CommitStatus Commit(
        string path,
        string expectedRefresh,
        Action<JsonObject> rotate)
    {
        if (LoadCredentialFile(path).Root is not { } latest
            || !TryObject(latest, CredentialKey, out var entry) || entry is null
            || !TryString(entry, "refreshToken", out var currentRefresh)
            || currentRefresh != expectedRefresh)
        {
            return CommitStatus.Conflict;
        }

        rotate(entry);
        return Replace(path, latest) ? CommitStatus.Committed : CommitStatus.Failed;
    }

    private static bool Replace(string path, JsonObject root)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(root, PersistOptions);
        if (bytes.Length > MaxCredentialFileBytes)
        {
            return false;
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

            using (var stream = new FileStream(temporaryPath, options))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    temporaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }

        SyncDirectory(Path.GetDirectoryName(path));
        return true;
    }

    private static void SyncDirectory(string? directory)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(directory))
        {
            return;
        }

        var descriptor = Native.open(directory, Native.OpenDirectoryFlags, 0);
        if (descriptor < 0)
        {
            return;
        }

        _ = Native.fsync(descriptor);
        _ = Native.close(descriptor);
    }

    private static string? SanitizePlanLabel(string? value)
    {
        if (value is null)
        {
            return null;
        }

        foreach (var known in KnownPlanLabels)
        {
            if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase))
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
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Disallow,
                }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    private static bool TryNumber(JsonObject obj, string name, out double? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonValue leaf
            && leaf.GetValueKind() == JsonValueKind.Number
            && leaf.TryGetValue<double>(out var number)
            && double.IsFinite(number))
        {
            value = number;
            return true;
        }

        return false;
    }

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

    private static bool TryArray(JsonObject obj, string name, out JsonArray? value)
    {
        value = null;
        if (!obj.TryGetPropertyValue(name, out var node) || node is null)
        {
            return true;
        }

        if (node is JsonArray array)
        {
            value = array;
            return true;
        }

        return false;
    }

    private static bool IsSafeQuotaLabel(string? value, out string label)
    {
        label = null!;
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length is 0 or > MaxQuotaLabelLength)
        {
            return false;
        }

        var pendingSeparator = false;
        var hasWord = false;
        foreach (var character in trimmed)
        {
            if (character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
            {
                hasWord = true;
                pendingSeparator = false;
                continue;
            }

            if ((character is ' ' or '.' or '+' or '/' or '-') && hasWord && !pendingSeparator)
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

    private static ProviderSubscriptionUsageMessage Close(
        DateTimeOffset observedAt,
        string diagnostic,
        bool? authenticated = null,
        string? planLabel = null)
        => new(
            ProviderId,
            SubscriptionUsageStatuses.Error,
            authenticated,
            planLabel,
            Version: null,
            [],
            observedAt,
            Source,
            diagnostic);

    private readonly record struct Closed(string Diagnostic, bool? Authenticated = null);

    private readonly record struct HttpOutcome(
        string? Failure,
        HttpStatusCode Status,
        byte[]? Body);

    private readonly record struct CredentialFile(JsonObject? Root, CredentialFileStatus Status)
    {
        public static readonly CredentialFile Missing = new(null, CredentialFileStatus.Missing);
        public static readonly CredentialFile Unreadable = new(null, CredentialFileStatus.Unreadable);
        public static readonly CredentialFile Malformed = new(null, CredentialFileStatus.Malformed);
    }

    private readonly record struct Rotation(
        string? Access,
        string? Diagnostic,
        bool? Authenticated)
    {
        public static readonly Rotation Conflict = default;

        public bool IsConflict => Access is null && Diagnostic is null;

        public static Rotation Rotated(string access) => new(access, null, null);

        public static Rotation Fail(string diagnostic, bool? authenticated = null)
            => new(null, diagnostic, authenticated);
    }

    private enum CredentialFileStatus
    {
        Valid,
        Missing,
        Unreadable,
        Malformed,
    }

    private enum CommitStatus
    {
        Committed,
        Conflict,
        Failed,
    }

    private readonly record struct FileIdentity(
        ushort Mode,
        uint Uid,
        ulong Inode,
        uint DeviceMajor,
        uint DeviceMinor,
        long Size)
    {
        public static FileIdentity Parse(ReadOnlySpan<byte> statx)
            => new(
                MemoryMarshal.Read<ushort>(statx[28..]),
                MemoryMarshal.Read<uint>(statx[20..]),
                MemoryMarshal.Read<ulong>(statx[32..]),
                MemoryMarshal.Read<uint>(statx[136..]),
                MemoryMarshal.Read<uint>(statx[140..]),
                MemoryMarshal.Read<long>(statx[40..]));

        public bool IsRegular => (Mode & Native.FileTypeMask) == Native.RegularFile;

        public bool IsDirectory => (Mode & Native.FileTypeMask) == Native.DirectoryFile;

        public bool SameInode(FileIdentity other)
            => Inode == other.Inode
               && DeviceMajor == other.DeviceMajor
               && DeviceMinor == other.DeviceMinor;
    }

    private static class Native
    {
        public const int StatxSize = 256;
        public const int AtFdCwd = -100;
        public const int AtSymlinkNoFollow = 0x100;
        public const int AtEmptyPath = 0x1000;
        public const uint StatxBasicStats = 0x7ff;
        public const int Enoent = 2;
        public const int Enotdir = 20;
        public const ushort FileTypeMask = 0xF000;
        public const ushort RegularFile = 0x8000;
        public const ushort DirectoryFile = 0x4000;
        public const ushort GroupOrOtherBits = 0x077;

        private const int ReadOnly = 0x0;
        private const int NoControllingTty = 0x100;
        private const int NonBlocking = 0x800;
        private const int CloseOnExec = 0x80000;

        private static readonly int NoFollow =
            RuntimeInformation.ProcessArchitecture is Architecture.Arm or Architecture.Arm64
                ? 0x8000
                : 0x20000;

        public static readonly int OpenPrivateReadFlags =
            ReadOnly | NoControllingTty | NonBlocking | CloseOnExec | NoFollow;

        public const int OpenDirectoryFlags = ReadOnly | CloseOnExec;

        [DllImport("libc", SetLastError = true)]
        public static extern int open(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            int mode);

        [DllImport("libc", SetLastError = true)]
        public static extern int statx(
            int directoryDescriptor,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            int flags,
            uint mask,
            byte[] buffer);

        [DllImport("libc")]
        public static extern int fsync(int descriptor);

        [DllImport("libc")]
        public static extern int close(int descriptor);

        [DllImport("libc")]
        public static extern uint geteuid();
    }
}
