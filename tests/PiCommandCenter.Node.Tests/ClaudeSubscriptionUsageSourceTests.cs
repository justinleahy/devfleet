using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class ClaudeSubscriptionUsageSourceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    private const string Access = "sk-ant-oat01-synthetic";
    private const string Refresh = "sk-ant-ort01-synthetic";

    private const string UsageFixture = """
        {
          "five_hour": { "utilization": 36.0, "resets_at": "2026-09-05T15:00:00Z" },
          "seven_day": { "utilization": 9.0, "resets_at": "2026-09-11T00:00:00.000000+00:00" },
          "seven_day_opus": { "utilization": 0, "resets_at": null },
          "seven_day_sonnet": null,
          "extra_usage": { "is_enabled": false },
          "limits": [
            {
              "kind": "weekly_scoped",
              "percent": 14,
              "resets_at": "2026-09-12T00:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("claude-usage-").FullName;

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }

            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task Private_credentials_fixture_authenticates_from_the_store_and_maps_known_windows()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        var before = File.ReadAllText(path);
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, UsageFixture));
        using var source = Create(path, handler);

        var result = await source.ReadAsync(Now, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("anthropic", source.Provider);
        Assert.Equal(TimeSpan.FromSeconds(10), ClaudeSubscriptionUsageSource.DefaultRequestTimeout);
        Assert.Equal(256 * 1024, ClaudeSubscriptionUsageSource.MaxCredentialFileBytes);
        Assert.Equal(64 * 1024, ClaudeSubscriptionUsageSource.MaxResponseBytes);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal(SubscriptionUsageStatuses.Available, result.Status);
        Assert.True(result.Authenticated);
        Assert.Equal("Max", result.PlanLabel);
        Assert.Null(result.Version);
        Assert.Equal(Now, result.ObservedAt);
        Assert.Equal(ClaudeSubscriptionUsageSource.Source, result.Source);
        Assert.Null(result.Diagnostic);
        Assert.Collection(
            result.Windows,
            fiveHour => AssertWindow(fiveHour, "five-hour", 36, 64, new(2026, 9, 5, 15, 0, 0, TimeSpan.Zero)),
            weekly => AssertWindow(weekly, "weekly", 9, 91, new(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)),
            opus => AssertWindow(opus, "weekly opus", 0, 100, null),
            scoped => AssertWindow(scoped, "weekly Fable", 14, 86, new(2026, 9, 12, 0, 0, 0, TimeSpan.Zero)));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(UsageUrl, request.Url);
        Assert.Equal($"Bearer {Access}", request.Authorization);
        Assert.Equal("oauth-2025-04-20", request.AnthropicBeta);
        Assert.True(before == File.ReadAllText(path), "credential store changed");
    }

    [Fact]
    public async Task Missing_store_or_missing_Claude_oauth_entry_is_unconfigured()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        using var missing = Create(Path.Combine(_root, "missing.json"), handler);
        var otherPath = Path.Combine(_root, "other.json");
        WritePrivate(otherPath, """{"other":{"accessToken":"synthetic"}}""");
        using var noEntry = Create(otherPath, handler);

        Assert.Null(await missing.ReadAsync(Now, CancellationToken.None));
        Assert.Null(await noEntry.ReadAsync(Now, CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":"never"}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"","refreshToken":"r","expiresAt":1}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":7,"refreshToken":"r","expiresAt":1}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1,"subscriptionType":[]}}""")]
    public async Task Malformed_configured_store_closes_without_request(string content)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".json");
        WritePrivate(path, content);
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        using var source = Create(path, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.CredentialMalformed);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Oversized_or_non_private_store_is_unreadable_without_request()
    {
        var oversized = Path.Combine(_root, "oversized.json");
        WritePrivate(oversized, "{\"pad\":\"" + new string('x', ClaudeSubscriptionUsageSource.MaxCredentialFileBytes) + "\"}");
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        using var oversizedSource = Create(oversized, handler);

        var oversizedResult = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await oversizedSource.ReadAsync(Now, CancellationToken.None));
        AssertClosed(oversizedResult, ClaudeSubscriptionUsageSource.CredentialUnreadable);

        if (OperatingSystem.IsLinux())
        {
            var shared = WriteCredentials(TimeSpan.FromHours(1), "shared.json");
            File.SetUnixFileMode(
                shared,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            using var sharedSource = Create(shared, handler);
            var sharedResult = Assert.IsType<ProviderSubscriptionUsageMessage>(
                await sharedSource.ReadAsync(Now, CancellationToken.None));
            AssertClosed(sharedResult, ClaudeSubscriptionUsageSource.CredentialUnreadable);
        }

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Symlinked_store_is_rejected_without_following_it()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var target = WriteCredentials(TimeSpan.FromHours(1));
        var link = File.CreateSymbolicLink(Path.Combine(_root, "credentials-link.json"), target).FullName;
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("no request expected"));
        using var source = Create(link, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.CredentialUnreadable);
        Assert.Empty(handler.Requests);
    }

    public static TheoryData<string> DriftedUsageBodies => new()
    {
        "not json",
        "[]",
        """{"five_hour":{"utilization":100.01,"resets_at":null}}""",
        """{"five_hour":{"utilization":-0.01,"resets_at":null}}""",
        """{"five_hour":{"utilization":"36","resets_at":null}}""",
        """{"five_hour":{"utilization":36,"resets_at":"tomorrow"}}""",
        """{"five_hour":[],"limits":[]}""",
        """{"five_hour":{"utilization":1,"resets_at":null},"limits":{}}""",
        """{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":150,"resets_at":null,"scope":{"model":{"display_name":"Fable"}}}]}""",
        """{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":1,"resets_at":null,"scope":{"model":{"display_name":"<secret>"}}}]}""",
        NinthWindowBody(),
    };

    [Theory]
    [MemberData(nameof(DriftedUsageBodies))]
    public async Task Response_schema_drift_closes_as_malformed(string body)
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        using var source = Create(path, new RecordingHandler(_ => Json(HttpStatusCode.OK, body)));

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.HttpMalformed);
        Assert.True(result.Authenticated);
    }

    [Fact]
    public async Task Response_body_over_the_64_KiB_bound_is_not_parsed()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        var body = "{\"five_hour\":{\"utilization\":1,\"resets_at\":null},\"pad\":\""
            + new string('x', ClaudeSubscriptionUsageSource.MaxResponseBytes)
            + "\"}";
        var handler = new RecordingHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, body);
            response.Content.Headers.ContentLength = null;
            return response;
        });
        using var source = Create(path, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.HttpOversized);
    }

    [Fact]
    public async Task Redirect_is_not_followed_and_closes_with_only_the_exact_origin_observed()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        var handler = new RecordingHandler(_ =>
        {
            var response = Json(HttpStatusCode.TemporaryRedirect, """{"secret":"provider-body"}""");
            response.Headers.Location = new Uri("https://evil.example/collect");
            return response;
        });
        using var source = Create(path, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.HttpFailed);
        Assert.Equal(UsageUrl, Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Near_expiry_refreshes_and_atomically_compare_and_swap_persists_owner_only_store()
    {
        var path = WriteCredentials(TimeSpan.FromSeconds(30));
        var handler = new RecordingHandler(request => request.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-rotated","refresh_token":"sk-ant-ort01-rotated","expires_in":28800}""")
            : Json(HttpStatusCode.OK, UsageFixture));
        using var source = Create(path, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        Assert.Equal(SubscriptionUsageStatuses.Available, result.Status);
        Assert.Collection(
            handler.Requests,
            refresh =>
            {
                Assert.Equal(HttpMethod.Post, refresh.Method);
                Assert.Equal(TokenUrl, refresh.Url);
                Assert.Null(refresh.Authorization);
                Assert.Equal("application/json", refresh.ContentType);
                var body = JsonNode.Parse(refresh.Body)!.AsObject();
                Assert.Equal("refresh_token", (string)body["grant_type"]!);
                Assert.Equal(Refresh, (string)body["refresh_token"]!);
                Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", (string)body["client_id"]!);
            },
            usage =>
            {
                Assert.Equal(UsageUrl, usage.Url);
                Assert.Equal("Bearer sk-ant-oat01-rotated", usage.Authorization);
            });

        var persisted = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var oauth = persisted["claudeAiOauth"]!.AsObject();
        Assert.Equal("sk-ant-oat01-rotated", (string)oauth["accessToken"]!);
        Assert.Equal("sk-ant-ort01-rotated", (string)oauth["refreshToken"]!);
        Assert.Equal(Now.ToUnixTimeMilliseconds() + 28_800_000, (long)oauth["expiresAt"]!);
        Assert.Equal("keep", (string)persisted["otherTopLevel"]!);
        AssertOwnerOnly(path);
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task Unauthorized_refreshes_once_and_the_retry_is_final()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        var handler = new RecordingHandler(request => request.RequestUri!.ToString() == TokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-rotated","expires_in":28800}""")
            : Json(HttpStatusCode.Unauthorized, "{}"));
        using var source = Create(path, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.HttpUnauthorized);
        Assert.False(result.Authenticated);
        Assert.Equal([UsageUrl, TokenUrl, UsageUrl], handler.Requests.Select(request => request.Url));
        Assert.Equal("Bearer sk-ant-oat01-rotated", handler.Requests[2].Authorization);
        var oauth = JsonNode.Parse(File.ReadAllText(path))!["claudeAiOauth"]!.AsObject();
        Assert.Equal(Refresh, (string)oauth["refreshToken"]!);
    }

    [Fact]
    public async Task Compare_and_swap_conflict_preserves_Cli_rotation_and_reloads_once()
    {
        var path = WriteCredentials(TimeSpan.FromSeconds(-1));
        var handler = new RecordingHandler(request =>
        {
            if (request.RequestUri!.ToString() == TokenUrl)
            {
                WriteCredentials(
                    TimeSpan.FromHours(8),
                    access: "sk-ant-oat01-cli",
                    refresh: "sk-ant-ort01-cli");
                return Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-stale","refresh_token":"sk-ant-ort01-stale","expires_in":28800}""");
            }

            return request.Headers.Authorization?.Parameter == "sk-ant-oat01-cli"
                ? Json(HttpStatusCode.OK, UsageFixture)
                : Json(HttpStatusCode.Unauthorized, "{}");
        });
        using var source = Create(path, handler);

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        Assert.Equal(SubscriptionUsageStatuses.Available, result.Status);
        Assert.Equal([TokenUrl, UsageUrl], handler.Requests.Select(request => request.Url));
        Assert.Equal("Bearer sk-ant-oat01-cli", handler.Requests[1].Authorization);
        var oauth = JsonNode.Parse(File.ReadAllText(path))!["claudeAiOauth"]!.AsObject();
        Assert.Equal("sk-ant-ort01-cli", (string)oauth["refreshToken"]!);
        Assert.Single(Directory.EnumerateFiles(_root));
    }

    [Fact]
    public async Task Provider_bodies_and_credential_values_never_reach_the_result()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        using var source = Create(path, new RecordingHandler(_ =>
            Json(HttpStatusCode.InternalServerError, """{"email":"private@example.test","token":"raw-secret"}""")));

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));
        var serialized = JsonSerializer.Serialize(result);

        AssertClosed(result, ClaudeSubscriptionUsageSource.HttpFailed);
        Assert.DoesNotContain("private", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("example.test", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-secret", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Access, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(Refresh, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_and_stops_the_inflight_request()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var source = Create(path, handler, TimeSpan.FromSeconds(10));
        using var cancellation = new CancellationTokenSource();

        var read = source.ReadAsync(Now, cancellation.Token);
        await handler.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Internal_request_deadline_closes_with_a_stable_diagnostic()
    {
        var path = WriteCredentials(TimeSpan.FromHours(1));
        var handler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        using var source = Create(path, handler, TimeSpan.FromMilliseconds(250));

        var result = Assert.IsType<ProviderSubscriptionUsageMessage>(
            await source.ReadAsync(Now, CancellationToken.None));

        AssertClosed(result, ClaudeSubscriptionUsageSource.HttpTimeout);
    }

    private ClaudeSubscriptionUsageSource Create(
        string credentialPath,
        RecordingHandler handler,
        TimeSpan? timeout = null)
    {
        var options = Options.Create(new SubscriptionUsageOptions
        {
            ClaudeCredentialPath = credentialPath,
        });
        return new ClaudeSubscriptionUsageSource(
            options,
            new FixedTimeProvider(Now),
            handler,
            timeout ?? TimeSpan.FromSeconds(5));
    }

    private string WriteCredentials(
        TimeSpan expiresIn,
        string fileName = ".credentials.json",
        string access = Access,
        string? refresh = Refresh)
    {
        var oauth = new JsonObject
        {
            ["accessToken"] = access,
            ["expiresAt"] = Now.Add(expiresIn).ToUnixTimeMilliseconds(),
            ["subscriptionType"] = "max",
            ["scopes"] = new JsonArray("user:inference", "user:profile"),
        };
        if (refresh is not null)
        {
            oauth["refreshToken"] = refresh;
        }

        var path = Path.Combine(_root, fileName);
        WritePrivate(path, new JsonObject
        {
            ["claudeAiOauth"] = oauth,
            ["otherTopLevel"] = "keep",
        }.ToJsonString());
        return path;
    }

    private static void WritePrivate(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    private static string NinthWindowBody()
    {
        var scoped = string.Join(
            ",",
            Enumerable.Range(1, RuntimeSubscriptionUsageProbe.MaxWindows).Select(static index =>
                """{"kind":"weekly_scoped","percent":1,"resets_at":null,"scope":{"model":{"display_name":"M"""
                + index
                + "\"}}}"));
        return """{"five_hour":{"utilization":1,"resets_at":null},"limits":[""" + scoped + "]}";
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void AssertWindow(
        SubscriptionUsageWindowMessage window,
        string name,
        double used,
        double remaining,
        DateTimeOffset? resetsAt)
    {
        Assert.Equal(name, window.Name);
        Assert.Equal(used, window.PercentUsed);
        Assert.Equal(remaining, window.PercentRemaining);
        Assert.Equal(resetsAt, window.ResetsAt);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    private static void AssertClosed(ProviderSubscriptionUsageMessage result, string diagnostic)
    {
        Assert.Equal(SubscriptionUsageStatuses.Error, result.Status);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Empty(result.Windows);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Url,
        string? Authorization,
        string? AnthropicBeta,
        string? ContentType,
        string Body);

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<RecordedRequest> Requests { get; } = [];

        public TaskCompletionSource RequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                body));
            RequestStarted.TrySetResult();
            return await respond(request, cancellationToken);
        }
    }
}
