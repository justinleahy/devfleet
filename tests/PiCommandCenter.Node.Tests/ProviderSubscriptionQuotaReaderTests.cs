using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class ProviderSubscriptionQuotaReaderTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private const string PiUsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string PiTokenUrl = "https://auth.openai.com/oauth/token";
    private const string ClaudeUsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string ClaudeTokenUrl = "https://platform.claude.com/v1/oauth/token";

    private const string PiAccess = "synthetic-pi-access";
    private const string PiRefresh = "synthetic-pi-refresh";
    private const string PiAccount = "acct-synthetic-stored";
    private const string ClaudeAccess = "sk-ant-oat01-synthetic";
    private const string ClaudeRefresh = "sk-ant-ort01-synthetic";

    private const string PiUsageBody = """
        {
          "plan_type": "plus",
          "rate_limit": {
            "allowed": true,
            "limit_reached": false,
            "primary_window": { "used_percent": 12.5, "limit_window_seconds": 18000, "reset_after_seconds": 3600, "reset_at": 1788613200 },
            "secondary_window": { "used_percent": 40, "limit_window_seconds": 604800, "reset_after_seconds": 500000, "reset_at": 1789000000 }
          },
          "additional_rate_limits": [
            {
              "limit_name": "GPT-5.3-Codex-Spark",
              "metered_feature": "codex_bengalfox",
              "rate_limit": {
                "primary_window": { "used_percent": 3.5, "limit_window_seconds": 18000, "reset_after_seconds": 7200, "reset_at": 1788616800 },
                "secondary_window": { "used_percent": 22, "limit_window_seconds": 604800, "reset_after_seconds": 500000, "reset_at": 1789000000 }
              }
            }
          ],
          "credits": { "unused": 0 }
        }
        """;

    private const string ClaudeUsageBody = """
        {
          "five_hour": { "utilization": 36.0, "resets_at": "2026-09-05T15:00:00Z" },
          "seven_day": { "utilization": 9.0, "resets_at": "2026-09-11T00:00:00.000000+00:00" },
          "seven_day_opus": { "utilization": 0, "resets_at": null },
          "seven_day_sonnet": null,
          "extra_usage": { "is_enabled": false },
          "limits": [
            {
              "kind": "weekly_scoped",
              "group": "weekly",
              "percent": 14,
              "resets_at": "2026-09-12T00:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("quota-reader-").FullName;
    private readonly MutableTimeProvider _clock = new(Now);

    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }

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

    // ----- Pi -----

    [Fact]
    public async Task Pi_valid_credential_and_usage_yield_available_windows()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        var before = File.ReadAllText(path);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, PiUsageBody));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Null(result.Diagnostic);
        Assert.True(result.Authenticated);
        Assert.Equal("Plus", result.PlanLabel);
        Assert.Equal(ProviderSubscriptionQuotaReader.PiSource, result.Source);
        Assert.Collection(
            result.Windows,
            primary =>
            {
                Assert.Equal("five-hour", primary.Name);
                Assert.Equal(12.5, primary.PercentUsed);
                Assert.Equal(87.5, primary.PercentRemaining);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788613200), primary.ResetsAt);
            },
            secondary =>
            {
                Assert.Equal("weekly", secondary.Name);
                Assert.Equal(40.0, secondary.PercentUsed);
                Assert.Equal(60.0, secondary.PercentRemaining);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789000000), secondary.ResetsAt);
            },
            sparkFiveHour =>
            {
                Assert.Equal("GPT-5.3-Codex-Spark five-hour", sparkFiveHour.Name);
                Assert.Equal(3.5, sparkFiveHour.PercentUsed);
                Assert.Equal(96.5, sparkFiveHour.PercentRemaining);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788616800), sparkFiveHour.ResetsAt);
            },
            sparkWeekly =>
            {
                Assert.Equal("GPT-5.3-Codex-Spark weekly", sparkWeekly.Name);
                Assert.Equal(22.0, sparkWeekly.PercentUsed);
                Assert.Equal(78.0, sparkWeekly.PercentRemaining);
                Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789000000), sparkWeekly.ResetsAt);
            });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(PiUsageUrl, request.Url);
        Assert.Equal($"Bearer {PiAccess}", request.Authorization);
        Assert.Equal(PiAccount, request.AccountId);
        AssertStoreUnchanged(before, path);
    }

    [Fact]
    public async Task Pi_near_expiry_refreshes_then_reads_and_persists_rotation_preserving_other_entries()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(30));
        var newAccess = SyntheticJwt("acct-from-jwt");
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == PiTokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"__ACCESS__","refresh_token":"rotated-refresh","expires_in":3600,"id_token":"x"}"""
                .Replace("__ACCESS__", newAccess, StringComparison.Ordinal))
            : Json(HttpStatusCode.OK, PiUsageBody));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Collection(
            handler.Requests,
            refresh =>
            {
                Assert.Equal(HttpMethod.Post, refresh.Method);
                Assert.Equal(PiTokenUrl, refresh.Url);
                Assert.Null(refresh.Authorization);
                Assert.Equal("application/x-www-form-urlencoded", refresh.ContentType);
                Assert.Contains("grant_type=refresh_token", refresh.Body, StringComparison.Ordinal);
                Assert.Contains($"refresh_token={PiRefresh}", refresh.Body, StringComparison.Ordinal);
                Assert.Contains("client_id=app_EMoamEEZ73f0CkXaXp7hrann", refresh.Body, StringComparison.Ordinal);
            },
            usage =>
            {
                Assert.Equal(PiUsageUrl, usage.Url);
                Assert.Equal($"Bearer {newAccess}", usage.Authorization);
                Assert.Equal("acct-from-jwt", usage.AccountId);
            });

        var persisted = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var entry = persisted["openai-codex"]!.AsObject();
        Assert.Equal(newAccess, (string)entry["access"]!);
        Assert.Equal("rotated-refresh", (string)entry["refresh"]!);
        Assert.Equal(Now.ToUnixTimeMilliseconds() + 3_600_000, (long)entry["expires"]!);
        Assert.Equal("acct-from-jwt", (string)entry["accountId"]!);
        Assert.Equal("oauth", (string)entry["type"]!);
        Assert.Equal("sk-other-synthetic", (string)persisted["anthropic"]!["key"]!);
        Assert.Equal(7, (int)persisted["unrelated"]!["nested"]!);
        AssertOwnerOnly(path);
    }

    [Fact]
    public async Task Pi_refresh_failure_closes_without_touching_usage_or_file()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5));
        var before = File.ReadAllText(path);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.RefreshFailed);
        Assert.Equal(PiTokenUrl, Assert.Single(handler.Requests).Url);
        AssertStoreUnchanged(before, path);
    }

    [Fact]
    public async Task Pi_expired_without_refresh_token_is_credential_expired_and_makes_no_request()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5), refresh: "");
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialExpired);
        Assert.False(result.Authenticated);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pi_missing_file_is_unavailable_without_requests()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: Path.Combine(_root, "absent.json"));

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.CredentialMissing);
        Assert.Null(result.Authenticated);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pi_empty_path_disables_the_read()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: "");

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.CredentialMissing);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pi_file_without_codex_entry_is_unavailable_and_not_authenticated()
    {
        var path = Path.Combine(_root, "auth.json");
        WriteCredentialFile(path, """{"anthropic":{"type":"api_key","key":"sk-other-synthetic"}}""");
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.CredentialMissing);
        Assert.False(result.Authenticated);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"openai-codex":{"type":"oauth","access":"a","refresh":"r","expires":"soon"}}""")]
    [InlineData("""{"openai-codex":{"type":"oauth","access":"","refresh":"r","expires":1}}""")]
    [InlineData("""{"openai-codex":{"type":"oauth","access":42,"refresh":"r","expires":1}}""")]
    public async Task Pi_malformed_credential_is_an_error_without_requests(string content)
    {
        var path = Path.Combine(_root, "auth.json");
        WriteCredentialFile(path, content);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialMalformed);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pi_oversized_credential_file_is_unreadable()
    {
        var path = Path.Combine(_root, "auth.json");
        WriteCredentialFile(path, "{\"pad\":\"" + new string('x', ProviderSubscriptionQuotaReader.MaxCredentialFileBytes) + "\"}");
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialUnreadable);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Pi_directory_at_credential_path_is_missing_not_readable()
    {
        var path = Path.Combine(_root, "auth.json");
        Directory.CreateDirectory(path);
        using var reader = Create(new FakeHandler(_ => throw new InvalidOperationException("no request expected")), piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.CredentialMissing);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderSubscriptionQuotaReader.HttpUnauthorized, false)]
    [InlineData(HttpStatusCode.Forbidden, ProviderSubscriptionQuotaReader.HttpUnauthorized, false)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderSubscriptionQuotaReader.HttpRateLimited, null)]
    [InlineData(HttpStatusCode.InternalServerError, ProviderSubscriptionQuotaReader.HttpFailed, null)]
    [InlineData(HttpStatusCode.Found, ProviderSubscriptionQuotaReader.HttpFailed, null)]
    public async Task Pi_non_success_status_closes_with_stable_diagnostic(
        HttpStatusCode status,
        string diagnostic,
        bool? authenticated)
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        var handler = new FakeHandler(_ =>
        {
            var response = Json(status, """{"detail":"secret provider body must never surface"}""");
            response.Headers.Location = new Uri("https://evil.example/collect");
            return response;
        });
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, diagnostic);
        Assert.Equal(authenticated, result.Authenticated);
        Assert.Equal(PiUsageUrl, Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Pi_oversized_response_is_closed_without_parsing()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        var body = "{\"plan_type\":\"plus\",\"pad\":\"" + new string('x', ProviderSubscriptionQuotaReader.MaxResponseBytes) + "\"}";
        var handler = new FakeHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, body);
            response.Content.Headers.ContentLength = null;
            return response;
        });
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpOversized);
    }

    [Fact]
    public async Task Pi_declared_oversized_content_length_is_closed_before_reading()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        var handler = new FakeHandler(_ =>
        {
            var response = Json(HttpStatusCode.OK, PiUsageBody);
            response.Content.Headers.ContentLength = ProviderSubscriptionQuotaReader.MaxResponseBytes + 1;
            return response;
        });
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpOversized);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":150}}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":"12"}}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{}}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1,"reset_after_seconds":-1}}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000,"reset_after_seconds":300000000000,"reset_at":1788613200}}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000.5,"reset_after_seconds":1,"reset_at":1788613200}}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":[]}}""")]
    [InlineData("""{"plan_type":7,"rate_limit":null}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1}},"additional_rate_limits":{}}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1}},"additional_rate_limits":[{"limit_name":"GPT-5.3-Codex-Spark","metered_feature":"codex_bengalfox","rate_limit":{"primary_window":{"used_percent":150}}}]}""")]
    public async Task Pi_malformed_usage_body_is_closed(string body)
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpMalformed);
    }

    [Fact]
    public async Task Pi_null_additional_rate_limits_keeps_aggregate_windows()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        const string body = """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000,"reset_after_seconds":1,"reset_at":1788613200}},"additional_rate_limits":null}""";
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), piPath: path);

        var result = await reader.ReadPiAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        var window = Assert.Single(result.Windows);
        Assert.Equal("five-hour", window.Name);
        Assert.Equal(1, window.PercentUsed);
    }

    [Theory]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1}},"additional_rate_limits":[{"rate_limit":{"primary_window":{"used_percent":1}}}]}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1}},"additional_rate_limits":[{"limit_name":"<script>","rate_limit":{"primary_window":{"used_percent":1}}}]}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1}},"additional_rate_limits":[{"limit_name":"This-Label-Is-Way-Too-Long-For-The-Bound","rate_limit":{"primary_window":{"used_percent":1}}}]}""")]
    [InlineData("""{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1}},"additional_rate_limits":[null]}""")]
    public async Task Pi_malformed_additional_rate_limits_are_closed(string body)
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpMalformed);
    }

    [Fact]
    public async Task Pi_ninth_window_is_closed()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        const string extra = """{"limit_name":"Extra","rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000,"reset_after_seconds":1,"reset_at":1788613200},"secondary_window":{"used_percent":2,"limit_window_seconds":604800,"reset_after_seconds":1,"reset_at":1789000000}}}""";
        var extraB = extra.Replace("Extra", "ExtraB", StringComparison.Ordinal);
        var extraC = extra.Replace("Extra", "ExtraC", StringComparison.Ordinal);
        var extraD = extra.Replace("Extra", "ExtraD", StringComparison.Ordinal);
        var body =
            """{"plan_type":"plus","rate_limit":{"primary_window":{"used_percent":1,"limit_window_seconds":18000,"reset_after_seconds":1,"reset_at":1788613200},"secondary_window":{"used_percent":2,"limit_window_seconds":604800,"reset_after_seconds":1,"reset_at":1789000000}},"additional_rate_limits":["""
            + extra + "," + extraB + "," + extraC + "," + extraD + "]}";
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpMalformed);
    }

    [Theory]
    [InlineData("""{"plan_type":"free","rate_limit":null}""")]
    [InlineData("""{"plan_type":"free"}""")]
    [InlineData("""{"plan_type":"free","rate_limit":{"primary_window":null,"secondary_window":null}}""")]
    public async Task Pi_usage_without_windows_is_unavailable_but_authenticated(string body)
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.QuotaNotReported);
        Assert.True(result.Authenticated);
        Assert.Equal("Free", result.PlanLabel);
    }

    [Fact]
    public async Task Pi_unknown_plan_label_is_dropped_and_odd_window_lengths_fall_back_to_slot_names()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        const string body = """
            {
              "plan_type": "internal-tier<script>",
              "rate_limit": {
                "primary_window": { "used_percent": 1, "limit_window_seconds": 5400, "reset_after_seconds": 1, "reset_at": 1788613200 },
                "secondary_window": { "used_percent": 2, "limit_window_seconds": 172800, "reset_after_seconds": 1, "reset_at": 1789000000 }
              }
            }
            """;
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), piPath: path);

        var result = await reader.ReadPiAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Null(result.PlanLabel);
        Assert.Equal(["primary", "2-day"], result.Windows.Select(w => w.Name));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788613200), result.Windows[0].ResetsAt);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789000000), result.Windows[1].ResetsAt);
    }

    [Fact]
    public async Task Pi_request_timeout_is_closed_as_timeout()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        var handler = new FakeHandler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.OK, PiUsageBody);
        });
        using var reader = Create(handler, piPath: path, timeout: TimeSpan.FromMilliseconds(100));

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpTimeout);
    }

    [Fact]
    public async Task Pi_transport_failure_is_closed_as_failed()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        using var reader = Create(new FakeHandler(_ => throw new HttpRequestException("dns")), piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpFailed);
    }

    [Fact]
    public async Task Pi_caller_cancellation_propagates()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromHours(1));
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(async (_, token) =>
        {
            cts.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json(HttpStatusCode.OK, PiUsageBody);
        });
        using var reader = Create(handler, piPath: path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadPiAsync(cts.Token));
    }

    [Fact]
    public async Task Pi_unwritable_directory_is_persist_failed_and_leaves_the_store_untouched()
    {
        if (OperatingSystem.IsWindows() || IsRoot())
        {
            return;
        }

        var directory = Path.Combine(_root, "mount");
        Directory.CreateDirectory(directory);
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(1), Path.Combine(directory, "auth.json"));
        var before = File.ReadAllText(path);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == PiTokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":600}""")
            : Json(HttpStatusCode.OK, PiUsageBody));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialPersistFailed);
        Assert.Equal(PiTokenUrl, Assert.Single(handler.Requests).Url);
        AssertStoreUnchanged(before, path);
        Assert.Single(Directory.EnumerateFileSystemEntries(directory));
    }

    [Fact]
    public async Task Pi_persist_failure_after_refresh_is_reported_and_usage_is_not_read()
    {
        if (OperatingSystem.IsWindows() || IsRoot())
        {
            return;
        }

        var directory = Path.Combine(_root, "readonly");
        Directory.CreateDirectory(directory);
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(1), Path.Combine(directory, "auth.json"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var handler = new FakeHandler(_ =>
            Json(HttpStatusCode.OK, """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":600}"""));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialPersistFailed);
        Assert.Equal(PiTokenUrl, Assert.Single(handler.Requests).Url);
    }

    [Fact]
    public async Task Pi_refresh_without_rotated_refresh_token_is_refresh_failed_and_keeps_the_store()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5));
        var before = File.ReadAllText(path);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"rotated-access","expires_in":600}"""));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.RefreshFailed);
        Assert.Equal(PiTokenUrl, Assert.Single(handler.Requests).Url);
        AssertStoreUnchanged(before, path);
    }

    [Fact]
    public async Task Pi_cli_rotation_during_refresh_is_kept_and_the_newer_token_is_used()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5));
        string? cliState = null;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() != PiTokenUrl)
            {
                return Json(HttpStatusCode.OK, PiUsageBody);
            }

            WritePiAuth(expiresIn: TimeSpan.FromHours(1), access: "cli-rotated-access", refresh: "cli-rotated-refresh");
            cliState = File.ReadAllText(path);
            return Json(HttpStatusCode.OK, """{"access_token":"stale-access","refresh_token":"stale-refresh","expires_in":600}""");
        });
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Equal([PiTokenUrl, PiUsageUrl], handler.Requests.Select(r => r.Url));
        Assert.Equal("Bearer cli-rotated-access", handler.Requests[1].Authorization);
        Assert.Equal(PiAccount, handler.Requests[1].AccountId);
        AssertStoreUnchanged(cliState, path);
        var entry = JsonNode.Parse(File.ReadAllText(path))!["openai-codex"]!.AsObject();
        Assert.Equal("cli-rotated-refresh", (string)entry["refresh"]!);
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Pi_second_conflict_after_reload_fails_closed_without_overwriting_the_store()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5));
        var cliRotations = 0;
        string? cliState = null;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() != PiTokenUrl)
            {
                return Json(HttpStatusCode.OK, PiUsageBody);
            }

            cliRotations++;
            WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5), access: $"cli-access-{cliRotations}", refresh: $"cli-refresh-{cliRotations}");
            cliState = File.ReadAllText(path);
            return Json(HttpStatusCode.OK, """{"access_token":"stale-access","refresh_token":"stale-refresh","expires_in":600}""");
        });
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialPersistFailed);
        Assert.Equal([PiTokenUrl, PiTokenUrl], handler.Requests.Select(r => r.Url));
        Assert.Contains($"refresh_token={PiRefresh}", handler.Requests[0].Body, StringComparison.Ordinal);
        Assert.Contains("refresh_token=cli-refresh-1", handler.Requests[1].Body, StringComparison.Ordinal);
        AssertStoreUnchanged(cliState, path);
        var entry = JsonNode.Parse(File.ReadAllText(path))!["openai-codex"]!.AsObject();
        Assert.Equal("cli-refresh-2", (string)entry["refresh"]!);
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Pi_live_lock_held_by_another_writer_times_out_without_losing_tokens()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5));
        var before = File.ReadAllText(path);
        var lockPath = path + ".lock";
        Directory.CreateDirectory(lockPath);
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == PiTokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":600}""")
            : Json(HttpStatusCode.OK, PiUsageBody));
        using var reader = Create(handler, piPath: path, timeout: TimeSpan.FromMilliseconds(250));

        var result = await reader.ReadPiAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialPersistFailed);
        Assert.Equal(PiTokenUrl, Assert.Single(handler.Requests).Url);
        AssertStoreUnchanged(before, path);
        Assert.True(Directory.Exists(lockPath), "another writer's live lock was removed");
        Assert.Equal(2, Directory.EnumerateFileSystemEntries(_root).Count());
    }

    [Fact]
    public async Task Pi_stale_lock_is_reclaimed_and_the_rotation_commits()
    {
        var path = WritePiAuth(expiresIn: TimeSpan.FromSeconds(-5));
        var lockPath = path + ".lock";
        Directory.CreateDirectory(lockPath);
        Directory.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-2));
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == PiTokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"rotated-access","refresh_token":"rotated-refresh","expires_in":600}""")
            : Json(HttpStatusCode.OK, PiUsageBody));
        using var reader = Create(handler, piPath: path);

        var result = await reader.ReadPiAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Equal([PiTokenUrl, PiUsageUrl], handler.Requests.Select(r => r.Url));
        Assert.Equal("Bearer rotated-access", handler.Requests[1].Authorization);
        var entry = JsonNode.Parse(File.ReadAllText(path))!["openai-codex"]!.AsObject();
        Assert.Equal("rotated-access", (string)entry["access"]!);
        Assert.Equal("rotated-refresh", (string)entry["refresh"]!);
        Assert.False(Directory.Exists(lockPath), "reclaimed lock was not released");
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
        AssertOwnerOnly(path);
    }

    // ----- Claude -----

    [Fact]
    public async Task Claude_valid_credential_and_usage_yield_known_windows()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        var before = File.ReadAllText(path);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, ClaudeUsageBody));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Null(result.Diagnostic);
        Assert.True(result.Authenticated);
        Assert.Equal("Max", result.PlanLabel);
        Assert.Equal(ProviderSubscriptionQuotaReader.ClaudeSource, result.Source);
        Assert.Collection(
            result.Windows,
            fiveHour =>
            {
                Assert.Equal("five-hour", fiveHour.Name);
                Assert.Equal(36.0, fiveHour.PercentUsed);
                Assert.Equal(64.0, fiveHour.PercentRemaining);
                Assert.Equal(new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero), fiveHour.ResetsAt);
            },
            weekly =>
            {
                Assert.Equal("weekly", weekly.Name);
                Assert.Equal(9.0, weekly.PercentUsed);
                Assert.Equal(91.0, weekly.PercentRemaining);
                Assert.Equal(new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero), weekly.ResetsAt);
            },
            opus =>
            {
                Assert.Equal("weekly opus", opus.Name);
                Assert.Equal(0.0, opus.PercentUsed);
                Assert.Equal(100.0, opus.PercentRemaining);
                Assert.Null(opus.ResetsAt);
            },
            fable =>
            {
                Assert.Equal("weekly Fable", fable.Name);
                Assert.Equal(14.0, fable.PercentUsed);
                Assert.Equal(86.0, fable.PercentRemaining);
                Assert.Equal(new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero), fable.ResetsAt);
            });

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(ClaudeUsageUrl, request.Url);
        Assert.Equal($"Bearer {ClaudeAccess}", request.Authorization);
        Assert.Equal("oauth-2025-04-20", request.AnthropicBeta);
        Assert.Null(request.AccountId);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task Claude_unauthorized_refreshes_once_and_retries_with_the_new_token()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() == ClaudeTokenUrl)
            {
                return Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-rotated","refresh_token":"sk-ant-ort01-rotated","expires_in":28800,"scope":"user:inference"}""");
            }

            var bearer = request.Headers.Authorization?.Parameter;
            return bearer == "sk-ant-oat01-rotated"
                ? Json(HttpStatusCode.OK, ClaudeUsageBody)
                : Json(HttpStatusCode.Unauthorized, """{"error":{"type":"authentication_error"}}""");
        });
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Collection(
            handler.Requests,
            first =>
            {
                Assert.Equal(ClaudeUsageUrl, first.Url);
                Assert.Equal($"Bearer {ClaudeAccess}", first.Authorization);
            },
            refresh =>
            {
                Assert.Equal(HttpMethod.Post, refresh.Method);
                Assert.Equal(ClaudeTokenUrl, refresh.Url);
                Assert.Null(refresh.Authorization);
                Assert.Equal("application/json", refresh.ContentType);
                var body = JsonNode.Parse(refresh.Body)!.AsObject();
                Assert.Equal("refresh_token", (string)body["grant_type"]!);
                Assert.Equal(ClaudeRefresh, (string)body["refresh_token"]!);
                Assert.Equal("9d1c250a-e61b-44d9-88ed-5944d1962f5e", (string)body["client_id"]!);
            },
            retry =>
            {
                Assert.Equal(ClaudeUsageUrl, retry.Url);
                Assert.Equal("Bearer sk-ant-oat01-rotated", retry.Authorization);
            });

        var persisted = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var entry = persisted["claudeAiOauth"]!.AsObject();
        Assert.Equal("sk-ant-oat01-rotated", (string)entry["accessToken"]!);
        Assert.Equal("sk-ant-ort01-rotated", (string)entry["refreshToken"]!);
        Assert.Equal(Now.ToUnixTimeMilliseconds() + 28_800_000, (long)entry["expiresAt"]!);
        Assert.Equal("max", (string)entry["subscriptionType"]!);
        Assert.Equal(2, entry["scopes"]!.AsArray().Count);
        Assert.Equal("keep", (string)persisted["otherTopLevel"]!);
        AssertOwnerOnly(path);
    }

    [Fact]
    public async Task Claude_unauthorized_after_refresh_is_closed_and_not_retried_again()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == ClaudeTokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-rotated","refresh_token":"sk-ant-ort01-rotated","expires_in":28800}""")
            : Json(HttpStatusCode.Unauthorized, "{}"));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpUnauthorized);
        Assert.False(result.Authenticated);
        Assert.Equal("Max", result.PlanLabel);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Claude_expired_token_refreshes_before_the_first_usage_request()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromSeconds(-1));
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == ClaudeTokenUrl
            ? Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-rotated","refresh_token":"sk-ant-ort01-rotated","expires_in":28800}""")
            : Json(HttpStatusCode.OK, ClaudeUsageBody));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Equal([ClaudeTokenUrl, ClaudeUsageUrl], handler.Requests.Select(r => r.Url));
        Assert.Equal("Bearer sk-ant-oat01-rotated", handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task Claude_expired_token_without_refresh_token_is_credential_expired()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromSeconds(-1), refresh: null);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialExpired);
        Assert.False(result.Authenticated);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Claude_refresh_failure_keeps_the_file_and_stops()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromSeconds(-1));
        var before = File.ReadAllText(path);
        var handler = new FakeHandler(_ => Json(HttpStatusCode.OK, """{"access_token":"only-access"}"""));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.RefreshFailed);
        Assert.Equal(ClaudeTokenUrl, Assert.Single(handler.Requests).Url);
        AssertStoreUnchanged(before, path);
    }

    [Fact]
    public async Task Claude_file_without_oauth_entry_is_unavailable()
    {
        var path = Path.Combine(_root, ".credentials.json");
        WriteCredentialFile(path, """{"somethingElse":{"token":"synthetic"}}""");
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.CredentialMissing);
        Assert.False(result.Authenticated);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":"never"}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"","expiresAt":1}}""")]
    [InlineData("""{"claudeAiOauth":{"expiresAt":1}}""")]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1,"subscriptionType":["max"]}}""")]
    public async Task Claude_malformed_credential_is_an_error_without_requests(string content)
    {
        var path = Path.Combine(_root, ".credentials.json");
        WriteCredentialFile(path, content);
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialMalformed);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":36,"resets_at":"tomorrow"}}""")]
    [InlineData("""{"five_hour":{"utilization":100.01,"resets_at":null}}""")]
    [InlineData("""{"five_hour":{"utilization":-0.01,"resets_at":null}}""")]
    [InlineData("""{"five_hour":{"utilization":"36"}}""")]
    [InlineData("""{"five_hour":{}}""")]
    [InlineData("""{"five_hour":"36%"}""")]
    [InlineData("""{"seven_day":{"utilization":9,"resets_at":1789000000}}""")]
    [InlineData("[]")]
    [InlineData("""{"five_hour":{"utilization":36,"resets_at":null},"limits":{}}""")]
    [InlineData("""{"five_hour":{"utilization":36,"resets_at":null},"limits":[{"kind":"weekly_scoped","group":"weekly","percent":150,"resets_at":"2026-09-12T00:00:00Z","scope":{"model":{"display_name":"Fable"}}}]}""")]
    public async Task Claude_malformed_usage_body_is_closed(string body)
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpMalformed);
        Assert.Equal("Max", result.PlanLabel);
    }

    [Fact]
    public async Task Claude_cinder_cove_and_unknown_or_unscoped_limits_are_handled()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        const string body = """
            {
              "cinder_cove": { "utilization": 5, "resets_at": null },
              "limits": [
                { "kind": "future_kind", "percent": 9, "resets_at": "2026-09-12T00:00:00Z" },
                { "kind": "weekly", "percent": 8, "resets_at": "2026-09-12T00:00:00Z" },
                { "kind": "weekly_scoped", "percent": 7, "resets_at": "2026-09-12T00:00:00Z", "scope": { "model": { "display_name": "Fable" } } }
              ]
            }
            """;
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), claudePath: path);

        var result = await reader.ReadClaudeAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Collection(
            result.Windows,
            cowork =>
            {
                Assert.Equal("cowork credit", cowork.Name);
                Assert.Equal(5.0, cowork.PercentUsed);
                Assert.Equal(95.0, cowork.PercentRemaining);
                Assert.Null(cowork.ResetsAt);
            },
            fable =>
            {
                Assert.Equal("weekly Fable", fable.Name);
                Assert.Equal(7.0, fable.PercentUsed);
                Assert.Equal(93.0, fable.PercentRemaining);
            });
    }

    [Theory]
    [InlineData("""{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":1,"resets_at":null,"scope":{"model":{"display_name":"<script>"}}}]}""")]
    [InlineData("""{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":8,"resets_at":"2026-09-12T00:00:00Z"}]}""")]
    [InlineData("""{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":8,"resets_at":"2026-09-12T00:00:00Z","scope":null}]}""")]
    [InlineData("""{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":8,"resets_at":"2026-09-12T00:00:00Z","scope":{"model":null}}]}""")]
    [InlineData("""{"five_hour":{"utilization":1,"resets_at":null},"limits":[{"kind":"weekly_scoped","percent":8,"resets_at":"2026-09-12T00:00:00Z","scope":{"model":{"display_name":null}}}]}""")]
    [InlineData("""{"five_hour":{"utilization":1,"resets_at":null},"limits":[null]}""")]
    public async Task Claude_malformed_recognized_limits_are_closed(string body)
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpMalformed);
    }

    [Fact]
    public async Task Claude_ninth_window_is_closed()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        var scoped = string.Join(
            ",",
            Enumerable.Range(1, 8).Select(static i =>
                """{"kind":"weekly_scoped","percent":1,"resets_at":null,"scope":{"model":{"display_name":"M"""
                + i
                + "\"}}}"));
        var body = """{"five_hour":{"utilization":1,"resets_at":null},"limits":[""" + scoped + "]}";
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpMalformed);
    }

    [Fact]
    public async Task Claude_usage_without_known_windows_is_unavailable()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        const string body = """{"five_hour":null,"seven_day":null,"future_window":{"utilization":5}}""";
        using var reader = Create(new FakeHandler(_ => Json(HttpStatusCode.OK, body)), claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Unavailable, ProviderSubscriptionQuotaReader.QuotaNotReported);
        Assert.True(result.Authenticated);
    }

    [Fact]
    public async Task Claude_redirect_is_not_followed_and_closes()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        var handler = new FakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://evil.example/oauth/usage");
            return response;
        });
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.HttpFailed);
        Assert.Equal(ClaudeUsageUrl, Assert.Single(handler.Requests).Url);
    }

    [Theory]
    [InlineData("""{"access_token":"sk-ant-oat01-rotated","expires_in":28800}""")]
    [InlineData("""{"access_token":"sk-ant-oat01-rotated","refresh_token":"","expires_in":28800}""")]
    public async Task Claude_refresh_without_rotated_refresh_token_keeps_the_prior_one(string tokenBody)
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromSeconds(-1));
        var handler = new FakeHandler(request => request.RequestUri!.ToString() == ClaudeTokenUrl
            ? Json(HttpStatusCode.OK, tokenBody)
            : Json(HttpStatusCode.OK, ClaudeUsageBody));
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Equal([ClaudeTokenUrl, ClaudeUsageUrl], handler.Requests.Select(r => r.Url));
        Assert.Equal("Bearer sk-ant-oat01-rotated", handler.Requests[1].Authorization);
        var entry = JsonNode.Parse(File.ReadAllText(path))!["claudeAiOauth"]!.AsObject();
        Assert.Equal("sk-ant-oat01-rotated", (string)entry["accessToken"]!);
        Assert.Equal(ClaudeRefresh, (string)entry["refreshToken"]!);
        Assert.Equal(Now.ToUnixTimeMilliseconds() + 28_800_000, (long)entry["expiresAt"]!);
        AssertOwnerOnly(path);
    }

    [Fact]
    public async Task Claude_cli_rotation_during_refresh_is_kept_and_the_newer_token_is_used()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        string? cliState = null;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() == ClaudeTokenUrl)
            {
                WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(8), access: "sk-ant-oat01-cli", refresh: "sk-ant-ort01-cli");
                cliState = File.ReadAllText(path);
                return Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-stale","refresh_token":"sk-ant-ort01-stale","expires_in":28800}""");
            }

            return request.Headers.Authorization?.Parameter == "sk-ant-oat01-cli"
                ? Json(HttpStatusCode.OK, ClaudeUsageBody)
                : Json(HttpStatusCode.Unauthorized, "{}");
        });
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        Assert.Equal(SubscriptionQuotaReadStatus.Available, result.Status);
        Assert.Equal([ClaudeUsageUrl, ClaudeTokenUrl, ClaudeUsageUrl], handler.Requests.Select(r => r.Url));
        Assert.Equal("Bearer sk-ant-oat01-cli", handler.Requests[2].Authorization);
        AssertStoreUnchanged(cliState, path);
        var entry = JsonNode.Parse(File.ReadAllText(path))!["claudeAiOauth"]!.AsObject();
        Assert.Equal("sk-ant-ort01-cli", (string)entry["refreshToken"]!);
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task Claude_second_conflict_after_reload_fails_closed_without_overwriting_the_store()
    {
        var path = WriteClaudeCredentials(expiresIn: TimeSpan.FromSeconds(-1));
        var cliRotations = 0;
        string? cliState = null;
        var handler = new FakeHandler(request =>
        {
            if (request.RequestUri!.ToString() != ClaudeTokenUrl)
            {
                return Json(HttpStatusCode.OK, ClaudeUsageBody);
            }

            cliRotations++;
            WriteClaudeCredentials(
                expiresIn: TimeSpan.FromSeconds(-1),
                access: $"sk-ant-oat01-cli-{cliRotations}",
                refresh: $"sk-ant-ort01-cli-{cliRotations}");
            cliState = File.ReadAllText(path);
            return Json(HttpStatusCode.OK, """{"access_token":"sk-ant-oat01-stale","refresh_token":"sk-ant-ort01-stale","expires_in":28800}""");
        });
        using var reader = Create(handler, claudePath: path);

        var result = await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialPersistFailed);
        Assert.Equal("Max", result.PlanLabel);
        Assert.Equal([ClaudeTokenUrl, ClaudeTokenUrl], handler.Requests.Select(r => r.Url));
        Assert.Equal(ClaudeRefresh, (string)JsonNode.Parse(handler.Requests[0].Body)!["refresh_token"]!);
        Assert.Equal("sk-ant-ort01-cli-1", (string)JsonNode.Parse(handler.Requests[1].Body)!["refresh_token"]!);
        AssertStoreUnchanged(cliState, path);
        var entry = JsonNode.Parse(File.ReadAllText(path))!["claudeAiOauth"]!.AsObject();
        Assert.Equal("sk-ant-ort01-cli-2", (string)entry["refreshToken"]!);
        Assert.Single(Directory.EnumerateFileSystemEntries(_root));
    }

    // ----- credential file identity (both providers share one loader) -----

    [Theory]
    [InlineData("pi", "symlink")]
    [InlineData("pi", "fifo")]
    [InlineData("pi", "group-readable")]
    [InlineData("claude", "symlink")]
    [InlineData("claude", "fifo")]
    [InlineData("claude", "group-readable")]
    public async Task Non_private_regular_credential_path_is_unreadable_before_any_request(string provider, string kind)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var pi = provider == "pi";
        var stored = pi ? WritePiAuth(expiresIn: TimeSpan.FromHours(1)) : WriteClaudeCredentials(expiresIn: TimeSpan.FromHours(1));
        var path = kind switch
        {
            "symlink" => File.CreateSymbolicLink(Path.Combine(_root, "link.json"), stored).FullName,
            "fifo" => CreateFifo(Path.Combine(_root, "fifo.json")),
            _ => GroupReadable(stored),
        };
        var handler = new FakeHandler(_ => throw new InvalidOperationException("no request expected"));
        using var reader = Create(handler, piPath: pi ? path : null, claudePath: pi ? null : path);

        var result = pi ? await reader.ReadPiAsync() : await reader.ReadClaudeAsync();

        AssertClosed(result, SubscriptionQuotaReadStatus.Error, ProviderSubscriptionQuotaReader.CredentialUnreadable);
        Assert.Empty(handler.Requests);
    }

    // ----- helpers -----

    private ProviderSubscriptionQuotaReader Create(
        FakeHandler handler,
        string? piPath = null,
        string? claudePath = null,
        TimeSpan? timeout = null)
    {
        var options = new SubscriptionUsageOptions
        {
            PiCredentialPath = piPath ?? Path.Combine(_root, "unused-pi.json"),
            ClaudeCredentialPath = claudePath ?? Path.Combine(_root, "unused-claude.json"),
        };
        return new ProviderSubscriptionQuotaReader(
            Options.Create(options), _clock, handler, timeout ?? TimeSpan.FromSeconds(5));
    }

    private string WritePiAuth(TimeSpan expiresIn, string? path = null, string refresh = PiRefresh, string access = PiAccess)
    {
        path ??= Path.Combine(_root, "auth.json");
        var root = new JsonObject
        {
            ["anthropic"] = new JsonObject { ["type"] = "api_key", ["key"] = "sk-other-synthetic" },
            ["openai-codex"] = new JsonObject
            {
                ["type"] = "oauth",
                ["access"] = access,
                ["refresh"] = refresh,
                ["expires"] = Now.Add(expiresIn).ToUnixTimeMilliseconds(),
                ["accountId"] = PiAccount,
            },
            ["unrelated"] = new JsonObject { ["nested"] = 7 },
        };
        WriteCredentialFile(path, root.ToJsonString());
        return path;
    }

    private string WriteClaudeCredentials(TimeSpan expiresIn, string? refresh = ClaudeRefresh, string access = ClaudeAccess)
    {
        var path = Path.Combine(_root, ".credentials.json");
        var entry = new JsonObject
        {
            ["accessToken"] = access,
            ["expiresAt"] = Now.Add(expiresIn).ToUnixTimeMilliseconds(),
            ["scopes"] = new JsonArray("user:inference", "user:profile"),
            ["subscriptionType"] = "max",
            ["rateLimitTier"] = "default_claude_max_5x",
        };
        if (refresh is not null)
        {
            entry["refreshToken"] = refresh;
        }

        var root = new JsonObject { ["claudeAiOauth"] = entry, ["otherTopLevel"] = "keep" };
        WriteCredentialFile(path, root.ToJsonString());
        return path;
    }

    private static string SyntheticJwt(string accountId)
    {
        static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var payload = new JsonObject
        {
            ["https://api.openai.com/auth"] = new JsonObject { ["chatgpt_account_id"] = accountId },
            ["exp"] = 1_800_000_000,
        };
        return $"{Encode("""{"alg":"none"}""")}.{Encode(payload.ToJsonString())}.sig";
    }

    /// <summary>Writes the way the owning CLIs do: owner-only, since the reader refuses anything wider.</summary>
    private static void WriteCredentialFile(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [SupportedOSPlatform("linux")]
    private static string GroupReadable(string path)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        return path;
    }

    private static string CreateFifo(string path)
    {
        Assert.Equal(0, mkfifo(path, OwnerOnlyMode));
        return path;
    }

    private const uint OwnerOnlyMode = 0x180; // 0600

    [DllImport("libc", SetLastError = true)]
    private static extern int mkfifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static void AssertClosed(
        ProviderSubscriptionQuotaReadResult result,
        SubscriptionQuotaReadStatus status,
        string diagnostic)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(diagnostic, result.Diagnostic);
        Assert.Empty(result.Windows);
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    /// <summary>Compares without echoing either document, so a failure never prints stored tokens.</summary>
    private static void AssertStoreUnchanged(string? expected, string path)
        => Assert.True(expected == File.ReadAllText(path), "credential store changed");

    private static bool IsRoot() => Environment.UserName == "root";

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Url,
        string? Authorization,
        string? AccountId,
        string? AnthropicBeta,
        string? ContentType,
        string Body);

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request)))
        {
        }

        public List<RecordedRequest> Requests { get; } = [];

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
                request.Headers.TryGetValues("ChatGPT-Account-Id", out var account) ? account.Single() : null,
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.Single() : null,
                request.Content?.Headers.ContentType?.MediaType,
                body));
            return await respond(request, cancellationToken);
        }
    }
}
