using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class RuntimeSubscriptionUsageProbeTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid NodeId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string NodeExecutable = "node-test";
    private const string ScriptPath = "/opt/devfleet/runtime/pi-worker/src/usage.ts";

    /// <summary>
    /// Shape of the sidecar's stdout (<c>runtime/pi-worker/src/usage.ts</c>): one report per
    /// checked provider with an explicit status. Four are available, one is unavailable for
    /// lack of a credential, one errored upstream; limit ids and units are present but unread,
    /// and a stray metadata object stands in for anything the sidecar might add later.
    /// </summary>
    private const string Fixture = """
        {
          "reports": [
            {
              "provider": "openai-codex",
              "fetchedAt": 1788618051936,
              "status": "available",
              "limits": [
                {
                  "id": "7d",
                  "label": "7 days",
                  "window": { "label": "7 days", "resetsAt": 1788786170000 },
                  "amount": { "usedFraction": 0.91, "remainingFraction": 0.08999999999999997, "unit": "percent" }
                },
                {
                  "id": "5h",
                  "label": "5 hours (Spark)",
                  "window": { "label": "5 hours", "resetsAt": 1788636052000 },
                  "amount": { "usedFraction": 0, "remainingFraction": 1, "unit": "percent" }
                }
              ],
              "metadata": { "planType": "pro", "email": "SECRET_EMAIL@example.test", "accountId": "SECRET_ACCOUNT" }
            },
            {
              "provider": "anthropic",
              "fetchedAt": 1788618060000,
              "status": "unavailable",
              "diagnostic": "no_credential",
              "limits": []
            },
            {
              "provider": "opencode-go",
              "fetchedAt": 1788618087517,
              "status": "available",
              "limits": [
                {
                  "id": "5h",
                  "label": "5 Hour limit",
                  "window": { "label": "5 Hour", "resetsAt": 1788636087495 },
                  "amount": { "usedFraction": 0, "remainingFraction": 1, "unit": "percent" }
                },
                {
                  "id": "7d",
                  "label": "Weekly limit",
                  "window": { "label": "Weekly", "resetsAt": 1788739200495 },
                  "amount": { "usedFraction": 0.99, "remainingFraction": 0.010000000000000009, "unit": "percent" }
                },
                {
                  "id": "requests",
                  "label": "Request count",
                  "window": { "label": "Monthly", "resetsAt": 1790289547495 },
                  "amount": { "unit": "requests" }
                }
              ]
            },
            {
              "provider": "kimi-code",
              "fetchedAt": 1788618008298,
              "status": "available",
              "limits": [
                {
                  "id": "7d",
                  "label": "Total quota",
                  "window": { "label": "7 Day", "resetsAt": 1788628272585 },
                  "amount": { "usedFraction": 1, "unit": "unknown" }
                }
              ]
            },
            {
              "provider": "xai-oauth",
              "fetchedAt": 1788618070000,
              "status": "error",
              "diagnostic": "request_timeout",
              "limits": []
            },
            {
              "provider": "zai",
              "fetchedAt": 1788617986051,
              "status": "available",
              "limits": [
                {
                  "id": "5h",
                  "label": "ZAI 5 Hours Credit Quota",
                  "window": { "label": "5 Hours" },
                  "amount": { "usedFraction": 0, "remainingFraction": 1, "unit": "credits" }
                },
                {
                  "id": "1w",
                  "label": "ZAI Weekly Credit Quota",
                  "window": { "label": "Weekly", "resetsAt": 1788894781998 },
                  "amount": { "usedFraction": 0.1131, "remainingFraction": 0.8869, "unit": "credits" }
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task Fixture_runs_node_with_the_script_once_and_yields_one_provider_per_report()
    {
        var runner = new FakeRunner { Handler = (_, _) => Ok(Fixture) };
        var probe = Create(runner);

        var snapshot = await probe.GetAsync();

        var command = Assert.Single(runner.Commands);
        Assert.Equal(NodeExecutable, command.Executable);
        Assert.Equal([ScriptPath], command.Arguments);
        Assert.Equal(NodeId, snapshot.NodeId);
        Assert.Equal(
            ["openai-codex", "anthropic", "opencode-go", "kimi-code", "xai-oauth", "zai"],
            snapshot.Providers.Select(p => p.Provider));
        Assert.All(snapshot.Providers, provider =>
        {
            Assert.Null(provider.Authenticated);
            Assert.Null(provider.PlanLabel);
            Assert.Null(provider.Version);
            Assert.Equal(RuntimeSubscriptionUsageProbe.Source, provider.Source);
        });
    }

    [Fact]
    public async Task Fixture_windows_carry_percentages_and_resets_from_the_report()
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Fixture) });

        var snapshot = await probe.GetAsync();

        var openai = snapshot.Providers[0];
        Assert.Equal(SubscriptionUsageStatuses.Available, openai.Status);
        Assert.Null(openai.Diagnostic);
        Assert.Equal(Epoch(1788618051936), openai.ObservedAt);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage("7 days", 91, 9, Epoch(1788786170000)),
                new SubscriptionUsageWindowMessage("5 hours (Spark)", 0, 100, Epoch(1788636052000)),
            ],
            openai.Windows);

        // The request-count limit has no fraction, so it contributes no window and closes nothing.
        var opencode = snapshot.Providers[2];
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage("5 Hour limit", 0, 100, Epoch(1788636087495)),
                new SubscriptionUsageWindowMessage("Weekly limit", 99, 1, Epoch(1788739200495)),
            ],
            opencode.Windows);

        // Only a used fraction: remaining stays unreported rather than derived.
        var kimi = snapshot.Providers[3];
        Assert.Equal([new SubscriptionUsageWindowMessage("Total quota", 100, null, Epoch(1788628272585))], kimi.Windows);

        // A window without resetsAt is a window with an unknown reset, not a malformed one.
        var zai = snapshot.Providers[5];
        Assert.Equal(Epoch(1788617986051), zai.ObservedAt);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage("ZAI 5 Hours Credit Quota", 0, 100, null),
                new SubscriptionUsageWindowMessage("ZAI Weekly Credit Quota", 11.31, 88.69, Epoch(1788894781998)),
            ],
            zai.Windows);
        Assert.All(snapshot.Providers.SelectMany(p => p.Windows).Select(w => w.ResetsAt).OfType<DateTimeOffset>(),
            resets => Assert.Equal(TimeSpan.Zero, resets.Offset));
    }

    [Fact]
    public async Task Fixture_closed_reports_keep_the_sidecar_status_and_diagnostic()
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Fixture) });

        var snapshot = await probe.GetAsync();

        var anthropic = snapshot.Providers[1];
        AssertClosed(anthropic, SubscriptionUsageStatuses.Unavailable, "no_credential");
        Assert.Equal(Epoch(1788618060000), anthropic.ObservedAt);

        var xai = snapshot.Providers[4];
        AssertClosed(xai, SubscriptionUsageStatuses.Error, "request_timeout");
        Assert.Equal(Epoch(1788618070000), xai.ObservedAt);
    }

    [Fact]
    public async Task Metadata_and_ignored_fields_never_reach_the_snapshot()
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Fixture) });

        var snapshot = await probe.GetAsync();
        var json = JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("example.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("planType", json, StringComparison.Ordinal);
        Assert.DoesNotContain("metadata", json, StringComparison.Ordinal);
        Assert.DoesNotContain("unit", json, StringComparison.Ordinal);
        Assert.DoesNotContain("credits", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Request count", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_never_invokes_or_names_omp()
    {
        var runner = new FakeRunner { Handler = (_, _) => Ok(Fixture) };
        var probe = Create(runner);

        var snapshot = await probe.GetAsync();

        var command = Assert.Single(runner.Commands);
        Assert.DoesNotContain("omp", command.Executable, StringComparison.OrdinalIgnoreCase);
        Assert.All(command.Arguments, argument =>
        {
            Assert.DoesNotContain("omp", argument, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("--", argument, StringComparison.Ordinal);
        });
        Assert.Equal("pi ModelRuntime provider usage", RuntimeSubscriptionUsageProbe.Source);
        Assert.DoesNotContain("omp", JsonSerializer.Serialize(snapshot), StringComparison.OrdinalIgnoreCase);

        var defaults = new SubscriptionUsageOptions();
        Assert.Equal("node", defaults.NodeExecutable);
        Assert.Equal("runtime/pi-worker/src/usage.ts", defaults.ScriptPath);
        Assert.Equal("~/.claude/.credentials.json", defaults.ClaudeCredentialPath);
    }

    [Fact]
    public void Subscription_usage_postconfigure_expands_the_Claude_credential_path()
    {
        var options = new SubscriptionUsageOptions();

        new SubscriptionUsageOptionsPostConfigure().PostConfigure(null, options);

        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude",
                ".credentials.json"),
            options.ClaudeCredentialPath);
    }

    [Fact]
    public async Task Sidecar_and_supplements_merge_in_deterministic_provider_order()
    {
        var sidecar = Document(
            Report("openai-codex", Limit("Codex", 0.1, 0.9)),
            Report("kimi-code", Limit("Kimi", 0.2, 0.8)),
            Report("xai-oauth", Limit("xAI", 0.3, 0.7)));
        var anthropic = new FakeSupplementalSource(
            "anthropic",
            SupplementalCard("anthropic", "claude native"));
        var antigravity = new FakeSupplementalSource(
            "google-antigravity",
            SupplementalCard("google-antigravity", "agy native"));
        var probe = Create(
            new FakeRunner { Handler = (_, _) => Ok(sidecar) },
            supplements: [anthropic, antigravity]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            ["openai-codex", "kimi-code", "xai-oauth", "anthropic", "google-antigravity"],
            snapshot.Providers.Select(provider => provider.Provider));
        Assert.Equal("claude native", snapshot.Providers[3].Source);
        Assert.Equal("agy native", snapshot.Providers[4].Source);
        Assert.Equal([Observed], anthropic.ObservedAt);
        Assert.Equal([Observed], antigravity.ObservedAt);
    }

    [Fact]
    public async Task Supplements_survive_malformed_sidecar_output()
    {
        var anthropic = SupplementalCard("anthropic", "claude native");
        var antigravity = SupplementalCard("google-antigravity", "agy native");
        var probe = Create(
            new FakeRunner { Handler = (_, _) => Ok("not json") },
            supplements:
            [
                new FakeSupplementalSource("anthropic", anthropic),
                new FakeSupplementalSource("google-antigravity", antigravity),
            ]);

        var snapshot = await probe.GetAsync();

        Assert.Same(anthropic, snapshot.Providers.Single(provider => provider.Provider == "anthropic"));
        Assert.Same(
            antigravity,
            snapshot.Providers.Single(provider => provider.Provider == "google-antigravity"));
        Assert.All(
            snapshot.Providers.Where(provider => provider.Provider is not "anthropic" and not "google-antigravity"),
            provider => AssertClosed(
                provider,
                SubscriptionUsageStatuses.Error,
                RuntimeSubscriptionUsageProbe.ProcessMalformed));
    }

    [Fact]
    public async Task Supplemental_provider_replaces_same_id_sidecar_report_in_place()
    {
        var replacement = SupplementalCard("anthropic", "claude native");
        var sidecar = Document(
            Report("openai-codex", Limit("Codex", 0.1, 0.9)),
            Report("anthropic", Limit("Legacy", 0.2, 0.8)),
            Report("kimi-code", Limit("Kimi", 0.3, 0.7)));
        var probe = Create(
            new FakeRunner { Handler = (_, _) => Ok(sidecar) },
            supplements: [new FakeSupplementalSource("anthropic", replacement)]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            ["openai-codex", "anthropic", "kimi-code"],
            snapshot.Providers.Select(provider => provider.Provider));
        Assert.Same(replacement, snapshot.Providers[1]);
    }

    [Fact]
    public async Task Supplemental_failure_is_isolated_from_sidecar_and_sibling_supplements()
    {
        var sidecar = Document(
            Report("openai-codex", Limit("Codex", 0.1, 0.9)),
            Report("kimi-code", Limit("Kimi", 0.2, 0.8)),
            Report("xai-oauth", Limit("xAI", 0.3, 0.7)));
        var failed = new FakeSupplementalSource(
            "anthropic",
            (_, _) => Task.FromException<ProviderSubscriptionUsageMessage?>(
                new InvalidOperationException("SECRET provider failure")));
        var antigravity = SupplementalCard("google-antigravity", "agy native");
        var probe = Create(
            new FakeRunner { Handler = (_, _) => Ok(sidecar) },
            supplements:
            [
                failed,
                new FakeSupplementalSource("google-antigravity", antigravity),
            ]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            ["openai-codex", "kimi-code", "xai-oauth", "google-antigravity"],
            snapshot.Providers.Select(provider => provider.Provider));
        Assert.Same(antigravity, snapshot.Providers[^1]);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sidecar_and_supplements_are_started_concurrently()
    {
        var supplementStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FakeRunner
        {
            AsyncHandler = async (_, _) =>
            {
                await supplementStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
                return Ok(Document(Report("openai-codex", Limit("Codex", 0.1, 0.9))));
            },
        };
        var supplement = new FakeSupplementalSource(
            "anthropic",
            (_, _) =>
            {
                supplementStarted.SetResult();
                return Task.FromResult<ProviderSubscriptionUsageMessage?>(
                    SupplementalCard("anthropic", "claude native"));
            });
        var probe = Create(runner, supplements: [supplement]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            ["openai-codex", "anthropic"],
            snapshot.Providers.Select(provider => provider.Provider));
    }

    [Fact]
    public async Task Every_contract_provider_id_is_accepted_and_kept_in_report_order()
    {
        var reports = RuntimeSubscriptionUsageProbe.Providers.Reverse().Select(p => Report(p, Limit("Weekly", 0.5, 0.5))).ToArray();
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(reports)) });

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            ["qwen-token-plan-cn", "qwen-token-plan-individual", "qwen-token-plan", "opencode-go",
                "xai-oauth", "zai", "kimi-code", "anthropic", "openai-codex"],
            snapshot.Providers.Select(p => p.Provider));
        Assert.All(snapshot.Providers, p => Assert.Equal(SubscriptionUsageStatuses.Available, p.Status));
    }

    [Theory]
    [InlineData(SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.ProviderUnavailable)]
    [InlineData(SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProviderError)]
    public async Task Closed_status_without_a_diagnostic_gets_a_stable_default(string status, string expectedDiagnostic)
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(ReportWith("anthropic", $"\"status\":\"{status}\""))) });

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        AssertClosed(provider, status, expectedDiagnostic);
        Assert.Equal(Epoch(FetchedAt), provider.ObservedAt);
    }

    [Theory]
    [InlineData("not_authenticated")]
    [InlineData("http_429")]
    [InlineData("a")]
    [InlineData("0")]
    public async Task Safe_diagnostic_tokens_are_forwarded_verbatim(string diagnostic)
    {
        var probe = Create(new FakeRunner
        {
            Handler = (_, _) => Ok(Document(ReportWith("zai", $"\"status\":\"error\",\"diagnostic\":\"{diagnostic}\""))),
        });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Error, diagnostic);
    }

    [Fact]
    public async Task Diagnostic_at_the_length_bound_is_forwarded()
    {
        var diagnostic = new string('x', RuntimeSubscriptionUsageProbe.MaxLabelLength);
        var probe = Create(new FakeRunner
        {
            Handler = (_, _) => Ok(Document(ReportWith("zai", $"\"status\":\"unavailable\",\"diagnostic\":\"{diagnostic}\""))),
        });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Unavailable, diagnostic);
    }

    public static TheoryData<string, string> UnsafeDiagnostics => new()
    {
        { "empty", "\"\"" },
        { "hyphen", "\"no-credential\"" },
        { "uppercase", "\"Unauthorized\"" },
        { "space", "\"not authenticated\"" },
        { "free text", "\"SECRET_TOKEN expired at /home/secret\"" },
        { "non-ascii", "\"erreur_r\\u00e9seau\"" },
        { "oversized", $"\"{new string('x', RuntimeSubscriptionUsageProbe.MaxLabelLength + 1)}\"" },
        { "number", "403" },
        { "null", "null" },
        { "object", "{\"message\":\"SECRET\"}" },
    };

    [Theory]
    [MemberData(nameof(UnsafeDiagnostics))]
    public async Task Unsafe_diagnostic_closes_the_report_as_malformed(string reason, string diagnostic)
    {
        var document = Document(
            Report("zai", Limit("Fine", 0.25, 0.75)),
            ReportWith("anthropic", $"\"status\":\"error\",\"diagnostic\":{diagnostic}"));
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(document) });

        var snapshot = await probe.GetAsync();

        Assert.Equal(SubscriptionUsageStatuses.Available, snapshot.Providers[0].Status);
        AssertClosed(snapshot.Providers[1], SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
        Assert.Equal(Observed, snapshot.Providers[1].ObservedAt);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public async Task Diagnostic_on_an_available_report_is_ignored()
    {
        var probe = Create(new FakeRunner
        {
            Handler = (_, _) => Ok(Document(ReportWith("zai", "\"status\":\"available\",\"diagnostic\":\"SECRET free text\"", Limit("Weekly", 0.5, 0.5)))),
        });

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Null(provider.Diagnostic);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing", "")]
    [InlineData("unknown", "\"status\":\"ok\"")]
    [InlineData("case", "\"status\":\"Available\"")]
    [InlineData("non-string", "\"status\":1")]
    [InlineData("null", "\"status\":null")]
    public async Task Missing_or_unknown_status_closes_the_report_as_malformed(string reason, string status)
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(ReportWith("kimi-code", status, Limit("Weekly", 0.5, 0.5)))) });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public async Task Closed_report_with_malformed_limits_is_malformed_not_trusted()
    {
        var probe = Create(new FakeRunner
        {
            Handler = (_, _) => Ok(Document(ReportWith("zai", "\"status\":\"error\",\"diagnostic\":\"http_failed\"", "[]"))),
        });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
    }

    [Fact]
    public async Task Repeated_labels_are_disambiguated_by_window_label()
    {
        var report = Report(
            "openai-codex",
            Limit("Usage", 0.0008285, 0.9991715, windowLabel: "5 Hour", resetsAt: 1788624647000),
            Limit("Usage", 0.00032413, 0.99967587, windowLabel: "Weekly", resetsAt: 1789071025000),
            Limit("Spark", 0, 1, windowLabel: "Weekly", resetsAt: 1789222953000));
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(report)) });

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage("Usage \u2014 5 Hour", 0.08, 99.92, Epoch(1788624647000)),
                new SubscriptionUsageWindowMessage("Usage \u2014 Weekly", 0.03, 99.97, Epoch(1789071025000)),
                new SubscriptionUsageWindowMessage("Spark", 0, 100, Epoch(1789222953000)),
            ],
            provider.Windows);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Weekly ")]
    [InlineData("W\u00e9ekly")]
    public async Task Repeated_labels_without_a_safe_window_label_close_the_report(string? windowLabel)
    {
        var report = Report(
            "openai-codex",
            Limit("Usage", 0.1, 0.9, windowLabel: windowLabel),
            Limit("Usage", 0.2, 0.8, windowLabel: "Weekly"));
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(report)) });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
    }

    [Fact]
    public async Task Repeated_labels_with_repeated_window_labels_close_the_report()
    {
        var report = Report(
            "zai",
            Limit("Quota", 0.1, 0.9, windowLabel: "Weekly"),
            Limit("Quota", 0.2, 0.8, windowLabel: "Weekly"));
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(report)) });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
    }

    [Fact]
    public async Task Fractions_round_to_two_decimals_and_stay_coherent()
    {
        var report = Report("anthropic", Limit("Claude 7 Day", 0.14000000000000002, 0.86), Limit("Edge", 0.999, 0.001));
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(report)) });

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage("Claude 7 Day", 14, 86, null),
                new SubscriptionUsageWindowMessage("Edge", 99.9, 0.1, null),
            ],
            provider.Windows);
    }

    [Fact]
    public async Task Available_report_with_no_percentage_limits_is_unavailable_not_error()
    {
        var report = Report("opencode-go", """{"label":"Request count","window":{"label":"Monthly","resetsAt":1790182620000},"amount":{"unit":"requests"}}""");
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(report)) });

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal("opencode-go", provider.Provider);
        AssertClosed(provider, SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.QuotaNotReported);
        // The report itself was readable, so its own timestamp is the observation.
        Assert.Equal(Epoch(FetchedAt), provider.ObservedAt);
    }

    [Fact]
    public async Task Available_report_with_empty_limits_is_unavailable_not_error()
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(Report("xai-oauth"))) });

        var snapshot = await probe.GetAsync();

        AssertClosed(Assert.Single(snapshot.Providers), SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.QuotaNotReported);
    }

    [Fact]
    public async Task Empty_reports_array_is_an_empty_snapshot()
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document()) });

        var snapshot = await probe.GetAsync();

        Assert.Equal(NodeId, snapshot.NodeId);
        Assert.Empty(snapshot.Providers);
    }

    public static TheoryData<string, string> MalformedLimits => new()
    {
        { "missing label", """{"window":{},"amount":{"usedFraction":0.5}}""" },
        { "non-string label", """{"label":7,"window":{},"amount":{"usedFraction":0.5}}""" },
        { "empty label", """{"label":"","window":{},"amount":{"usedFraction":0.5}}""" },
        { "leading space", """{"label":" Weekly","window":{},"amount":{"usedFraction":0.5}}""" },
        { "trailing space", """{"label":"Weekly ","window":{},"amount":{"usedFraction":0.5}}""" },
        { "control char", """{"label":"Week\tly","window":{},"amount":{"usedFraction":0.5}}""" },
        { "non-ascii", """{"label":"W\u00e9ekly","window":{},"amount":{"usedFraction":0.5}}""" },
        { "oversized label", $$$"""{"label":"{{{new string('x', RuntimeSubscriptionUsageProbe.MaxLabelLength + 1)}}}","window":{},"amount":{"usedFraction":0.5}}""" },
        { "missing window", """{"label":"Weekly","amount":{"usedFraction":0.5}}""" },
        { "window not object", """{"label":"Weekly","window":"7d","amount":{"usedFraction":0.5}}""" },
        { "missing amount", """{"label":"Weekly","window":{}}""" },
        { "amount not object", """{"label":"Weekly","window":{},"amount":0.5}""" },
        { "used above one", """{"label":"Weekly","window":{},"amount":{"usedFraction":1.01}}""" },
        { "used percent scale", """{"label":"Weekly","window":{},"amount":{"usedFraction":37}}""" },
        { "remaining negative", """{"label":"Weekly","window":{},"amount":{"remainingFraction":-0.01}}""" },
        { "used as string", """{"label":"Weekly","window":{},"amount":{"usedFraction":"0.5"}}""" },
        { "used null", """{"label":"Weekly","window":{},"amount":{"usedFraction":null}}""" },
        { "incoherent pair", """{"label":"Weekly","window":{},"amount":{"usedFraction":0.3,"remainingFraction":0.6}}""" },
        { "pair off by tolerance", """{"label":"Weekly","window":{},"amount":{"usedFraction":0.5,"remainingFraction":0.503}}""" },
        { "resetsAt string", """{"label":"Weekly","window":{"resetsAt":"2026-09-10T20:10:25Z"},"amount":{"usedFraction":0.5}}""" },
        { "resetsAt fractional", """{"label":"Weekly","window":{"resetsAt":1788786170000.5},"amount":{"usedFraction":0.5}}""" },
        { "resetsAt negative", """{"label":"Weekly","window":{"resetsAt":-1},"amount":{"usedFraction":0.5}}""" },
        { "resetsAt null", """{"label":"Weekly","window":{"resetsAt":null},"amount":{"usedFraction":0.5}}""" },
        { "resetsAt beyond range", """{"label":"Weekly","window":{"resetsAt":253402300800000},"amount":{"usedFraction":0.5}}""" },
        { "resetsAt beyond int64", """{"label":"Weekly","window":{"resetsAt":99999999999999999999},"amount":{"usedFraction":0.5}}""" },
        { "limit not object", "[]" },
    };

    [Theory]
    [MemberData(nameof(MalformedLimits))]
    public async Task Malformed_limit_closes_only_its_own_report(string reason, string limit)
    {
        var document = Document(
            Report("zai", Limit("Fine", 0.25, 0.75)),
            Report("kimi-code", Limit("Also fine", 0.5, 0.5), limit),
            Report("opencode-go", Limit("Still fine", 0, 1)));
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(document) });

        var snapshot = await probe.GetAsync();

        Assert.Equal(["zai", "kimi-code", "opencode-go"], snapshot.Providers.Select(p => p.Provider));
        Assert.Equal(SubscriptionUsageStatuses.Available, snapshot.Providers[0].Status);
        Assert.Equal(SubscriptionUsageStatuses.Available, snapshot.Providers[2].Status);
        AssertClosed(snapshot.Providers[1], SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
        Assert.Equal(Observed, snapshot.Providers[1].ObservedAt);
        Assert.DoesNotContain("Also fine", JsonSerializer.Serialize(snapshot), StringComparison.Ordinal);
        Assert.NotEmpty(reason);
    }

    public static TheoryData<string, string> MalformedReportBodies => new()
    {
        { "missing fetchedAt", $$"""{"provider":"zai","status":"available","limits":[{{Limit("Weekly", 0.5, 0.5)}}]}""" },
        { "fetchedAt string", $$"""{"provider":"zai","status":"available","fetchedAt":"2026-09-05T12:00:00Z","limits":[{{Limit("Weekly", 0.5, 0.5)}}]}""" },
        { "fetchedAt negative", $$"""{"provider":"zai","status":"available","fetchedAt":-5,"limits":[{{Limit("Weekly", 0.5, 0.5)}}]}""" },
        { "fetchedAt fractional", $$"""{"provider":"zai","status":"available","fetchedAt":1788618008298.7,"limits":[{{Limit("Weekly", 0.5, 0.5)}}]}""" },
        { "missing limits", """{"provider":"zai","status":"available","fetchedAt":1788618008298}""" },
        { "limits not array", """{"provider":"zai","status":"available","fetchedAt":1788618008298,"limits":{}}""" },
        { "closed report missing limits", """{"provider":"zai","status":"error","diagnostic":"http_failed","fetchedAt":1788618008298}""" },
        {
            "too many limits",
            $$"""{"provider":"zai","status":"available","fetchedAt":1788618008298,"limits":[{{string.Join(',', Enumerable.Range(0, RuntimeSubscriptionUsageProbe.MaxWindows + 1).Select(i => Limit($"Window {i}", 0.5, 0.5)))}}]}"""
        },
        { "duplicate labels without window labels", $$"""{"provider":"zai","status":"available","fetchedAt":1788618008298,"limits":[{{Limit("Weekly", 0.5, 0.5)}},{{Limit("Weekly", 0.5, 0.5)}}]}""" },
    };

    [Theory]
    [MemberData(nameof(MalformedReportBodies))]
    public async Task Malformed_report_closes_that_provider_with_report_malformed(string reason, string report)
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(Report("anthropic", Limit("Claude 5 Hour", 0.37, 0.63)), report)) });

        var snapshot = await probe.GetAsync();

        Assert.Equal(["anthropic", "zai"], snapshot.Providers.Select(p => p.Provider));
        Assert.Equal(SubscriptionUsageStatuses.Available, snapshot.Providers[0].Status);
        AssertClosed(snapshot.Providers[1], SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ReportMalformed);
        Assert.NotEmpty(reason);
    }

    [Fact]
    public async Task Window_cap_is_inclusive_when_fraction_less_limits_are_not_counted_out()
    {
        var limits = Enumerable.Range(0, RuntimeSubscriptionUsageProbe.MaxWindows).Select(i => Limit($"Window {i}", 0.5, 0.5)).ToArray();
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(Document(Report("zai", limits))) });

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Equal(RuntimeSubscriptionUsageProbe.MaxWindows, provider.Windows.Count);
    }

    public static TheoryData<string, string> MalformedDocuments => new()
    {
        { "empty", "" },
        { "whitespace", "   \n" },
        { "not json", "Error: SECRET_TOKEN expired" },
        { "node module error", "node:internal/modules/cjs/loader:1228\n  throw err;\nError: Cannot find module '/home/secret/usage.ts'" },
        { "array root", "[]" },
        { "string root", "\"reports\"" },
        { "missing reports", """{"generatedAt":1788618200549}""" },
        { "reports not array", """{"reports":{}}""" },
        { "report not object", """{"reports":[1]}""" },
        { "report missing provider", $$"""{"reports":[{"status":"available","fetchedAt":1788618008298,"limits":[{{Limit("Weekly", 0.5, 0.5)}}]}]}""" },
        { "provider not string", """{"reports":[{"provider":7,"status":"available","fetchedAt":1788618008298,"limits":[]}]}""" },
        { "unknown provider", $$"""{"reports":[{{Report("SECRET_PROVIDER", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "pi id instead of emitted id", $$"""{"reports":[{{Report("kimi-coding", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "pi id instead of emitted id (xai)", $$"""{"reports":[{{Report("xai", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "dropped provider cursor", $$"""{"reports":[{{Report("cursor", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "dropped provider google-antigravity", $$"""{"reports":[{{Report("google-antigravity", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "legacy provider id", $$"""{"reports":[{{Report("pi", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "provider case", $$"""{"reports":[{{Report("ZAI", Limit("Weekly", 0.5, 0.5))}}]}""" },
        { "duplicate provider", $$"""{"reports":[{{Report("zai", Limit("A", 0.5, 0.5))}},{{Report("zai", Limit("B", 0.5, 0.5))}}]}""" },
        { "trailing content", $$"""{"reports":[]} {"reports":[]}""" },
        { "trailing comma", """{"reports":[],}""" },
        { "comment", """{"reports":[] /* c */}""" },
        {
            "more reports than providers",
            $$"""{"reports":[{{string.Join(',', RuntimeSubscriptionUsageProbe.Providers.Append("zai").Select(p => Report(p, Limit("Weekly", 0.5, 0.5))))}}]}"""
        },
    };

    [Theory]
    [MemberData(nameof(MalformedDocuments))]
    public async Task Malformed_document_closes_every_contract_provider(string reason, string stdout)
    {
        var probe = Create(new FakeRunner { Handler = (_, _) => Ok(stdout) });

        var snapshot = await probe.GetAsync();

        AssertAllClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessMalformed);
        Assert.NotEmpty(reason);
    }

    [Theory]
    [InlineData(true, false, false, null, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessTimeout)]
    [InlineData(false, true, false, 0, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessTruncated)]
    [InlineData(false, false, true, null, SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.ProcessMissing)]
    [InlineData(false, false, false, 1, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessFailed)]
    [InlineData(false, false, false, null, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessFailed)]
    public async Task Command_failures_close_every_provider_without_partial_output(
        bool timedOut,
        bool truncated,
        bool missing,
        int? exitCode,
        string status,
        string diagnostic)
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(exitCode, Fixture, "SECRET_STDERR /home/secret", timedOut, truncated, missing),
        };
        var probe = Create(runner);

        var snapshot = await probe.GetAsync();

        Assert.Single(runner.Commands);
        AssertAllClosed(snapshot, status, diagnostic);
    }

    [Theory]
    [InlineData("", ScriptPath)]
    [InlineData("  ", ScriptPath)]
    [InlineData(NodeExecutable, "")]
    [InlineData(NodeExecutable, "  ")]
    public async Task Unconfigured_executable_or_script_is_unavailable_without_commands(string executable, string script)
    {
        var runner = new FakeRunner();
        var probe = Create(runner, executable, script);

        var snapshot = await probe.GetAsync();

        Assert.Empty(runner.Commands);
        AssertAllClosed(snapshot, SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.ProcessMissing);
    }

    [Fact]
    public async Task Cancelled_token_launches_nothing()
    {
        var runner = new FakeRunner { Handler = (_, _) => Ok(Fixture) };
        var probe = Create(runner);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetAsync(cts.Token));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Runner_rejects_a_cancelled_token_before_starting_a_process()
    {
        var runner = new RuntimeSubscriptionUsageCommandRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Without the pre-start check this would report Missing instead of cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("pi-command-center-no-such-executable", [ScriptPath], cts.Token));
    }

    [Fact]
    public async Task Runner_reports_a_missing_executable_without_throwing()
    {
        var runner = new RuntimeSubscriptionUsageCommandRunner();

        var result = await runner.RunAsync("pi-command-center-no-such-executable", [ScriptPath], CancellationToken.None);

        Assert.True(result.Missing);
        Assert.Null(result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task Runner_drains_both_pipes_to_eof_and_keeps_the_exit_code()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new RuntimeSubscriptionUsageCommandRunner();

        var result = await runner.RunAsync("/bin/sh", ["-c", "echo 1.2.3; echo warn >&2; exit 3"], CancellationToken.None);

        Assert.Equal(new SubscriptionUsageCommandResult(3, "1.2.3\n", "warn\n", false, false, false), result);
    }

    [Fact]
    public async Task Runner_truncates_at_the_shared_budget_while_still_draining_the_child()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new RuntimeSubscriptionUsageCommandRunner();

        // Both pipes exceed the budget and the OS pipe buffer; a reader that stopped at the budget would block the child.
        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "head -c 300000 /dev/zero; head -c 300000 /dev/zero >&2"],
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.False(result.TimedOut);
        Assert.Equal(
            RuntimeSubscriptionUsageCommandRunner.MaxOutputBytes,
            result.StandardOutput.Length + result.StandardError.Length);
    }

    [Fact]
    public async Task Runner_keeps_output_well_above_a_real_report_intact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new RuntimeSubscriptionUsageCommandRunner();

        var result = await runner.RunAsync("/bin/sh", ["-c", "head -c 200000 /dev/zero | tr '\\0' 'x'"], CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.Truncated);
        Assert.Equal(200000, result.StandardOutput.Length);
    }

    [Fact]
    public async Task Runner_parent_exit_with_a_descendant_holding_the_pipe_is_timeout_without_partial_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new RuntimeSubscriptionUsageCommandRunner(TimeSpan.FromMilliseconds(300));

        // The parent prints a valid report and exits 0 while the background child keeps stdout open.
        var result = await runner.RunAsync("/bin/sh", ["-c", "echo '{\"reports\":[]}'; sleep 2 & exit 0"], CancellationToken.None);

        Assert.Equal(
            new SubscriptionUsageCommandResult(null, string.Empty, string.Empty, TimedOut: true, Truncated: false, Missing: false),
            result);
    }

    [Fact]
    public async Task Runner_propagates_caller_cancellation_while_draining_and_stops_promptly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new RuntimeSubscriptionUsageCommandRunner();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var started = Stopwatch.GetTimestamp();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("/bin/sh", ["-c", "echo partial; sleep 30"], cts.Token));

        Assert.True(Stopwatch.GetElapsedTime(started) < RuntimeSubscriptionUsageCommandRunner.CleanupTimeout + TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Reader_drops_partial_text_when_the_pipe_read_fails()
    {
        var stream = new ScriptedStream("{\"re"u8.ToArray(), _ => ValueTask.FromException<int>(new IOException("reset")));

        var read = await RuntimeSubscriptionUsageCommandRunner.ReadBoundedAsync(
            stream,
            new RuntimeSubscriptionUsageCommandRunner.OutputBudget(16),
            CancellationToken.None);

        Assert.False(read.Drained);
        Assert.Equal(string.Empty, read.Text);
    }

    [Fact]
    public async Task Reader_drops_partial_text_when_cancelled_before_eof()
    {
        using var cts = new CancellationTokenSource();
        var stream = new ScriptedStream("{\"re"u8.ToArray(), token =>
        {
            cts.Cancel();
            return ValueTask.FromCanceled<int>(token);
        });

        var read = await RuntimeSubscriptionUsageCommandRunner.ReadBoundedAsync(
            stream,
            new RuntimeSubscriptionUsageCommandRunner.OutputBudget(16),
            cts.Token);

        Assert.False(read.Drained);
        Assert.Equal(string.Empty, read.Text);
    }

    private const long FetchedAt = 1788618008298;

    private static DateTimeOffset Epoch(long milliseconds) => DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);

    private static string Document(params string[] reports)
        => "{\"reports\":[" + string.Join(',', reports) + "]}";

    /// <summary>An <c>available</c> report carrying <paramref name="limits"/>.</summary>
    private static string Report(string provider, params string[] limits)
        => ReportWith(provider, "\"status\":\"available\"", limits);

    /// <summary>A report whose status/diagnostic fragment is spelled by the caller; empty omits both.</summary>
    private static string ReportWith(string provider, string statusFragment, params string[] limits)
        => "{\"provider\":"
            + JsonSerializer.Serialize(provider)
            + (statusFragment.Length == 0 ? "" : "," + statusFragment)
            + $",\"fetchedAt\":{FetchedAt},\"limits\":["
            + string.Join(',', limits)
            + "],\"metadata\":{\"email\":\"SECRET_EMAIL\",\"accountId\":\"SECRET_ACCOUNT\"}}";

    private static string Limit(
        string label,
        double? usedFraction,
        double? remainingFraction,
        string? windowLabel = "Weekly",
        long? resetsAt = null)
    {
        var window = (windowLabel is null ? "" : $"\"label\":{JsonSerializer.Serialize(windowLabel)},")
            + (resetsAt is { } reset ? $"\"resetsAt\":{reset}," : "")
            + "\"id\":\"w\"";
        var amount = (usedFraction is { } used ? $"\"usedFraction\":{used.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}," : "")
            + (remainingFraction is { } remaining ? $"\"remainingFraction\":{remaining.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}," : "")
            + "\"unit\":\"percent\"";
        return "{\"id\":\"limit\",\"label\":"
            + JsonSerializer.Serialize(label)
            + ",\"window\":{"
            + window
            + "},\"amount\":{"
            + amount
            + "}}";
    }

    private static void AssertClosed(ProviderSubscriptionUsageMessage provider, string status, string diagnostic)
    {
        Assert.Equal(status, provider.Status);
        Assert.Equal(diagnostic, provider.Diagnostic);
        Assert.Empty(provider.Windows);
        Assert.Null(provider.Version);
        Assert.Null(provider.PlanLabel);
        Assert.Null(provider.Authenticated);
        Assert.Equal(RuntimeSubscriptionUsageProbe.Source, provider.Source);
    }

    private static void AssertAllClosed(NodeSubscriptionUsageMessage snapshot, string status, string diagnostic)
    {
        Assert.Equal(NodeId, snapshot.NodeId);
        Assert.Equal(
            ["openai-codex", "anthropic", "kimi-code", "zai", "xai-oauth", "opencode-go",
                "qwen-token-plan", "qwen-token-plan-individual", "qwen-token-plan-cn"],
            snapshot.Providers.Select(p => p.Provider));
        Assert.All(snapshot.Providers, provider =>
        {
            AssertClosed(provider, status, diagnostic);
            Assert.Equal(Observed, provider.ObservedAt);
        });
        var json = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("reports", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Weekly", json, StringComparison.Ordinal);
        Assert.DoesNotContain("omp", json, StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeSubscriptionUsageProbe Create(
        IRuntimeSubscriptionUsageCommandRunner runner,
        string executable = NodeExecutable,
        string script = ScriptPath,
        IReadOnlyList<ISupplementalSubscriptionUsageSource>? supplements = null)
        => new(
            Options.Create(new NodeOptions { Id = NodeId }),
            Options.Create(new SubscriptionUsageOptions
            {
                NodeExecutable = executable,
                ScriptPath = script,
            }),
            new FixedTime(),
            supplements ?? [],
            runner);

    private static SubscriptionUsageCommandResult Ok(string stdout)
        => new(0, stdout, string.Empty, false, false, false);

    private static ProviderSubscriptionUsageMessage SupplementalCard(string provider, string source)
        => new(
            provider,
            SubscriptionUsageStatuses.Available,
            Authenticated: null,
            PlanLabel: null,
            Version: null,
            [new SubscriptionUsageWindowMessage("Weekly", 25, 75, null)],
            Observed,
            source,
            Diagnostic: null);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Observed;
    }

    private sealed class FakeRunner : IRuntimeSubscriptionUsageCommandRunner
    {
        private readonly List<(string Executable, IReadOnlyList<string> Arguments)> _commands = [];

        public IReadOnlyList<(string Executable, IReadOnlyList<string> Arguments)> Commands
        {
            get
            {
                lock (_commands)
                {
                    return [.. _commands];
                }
            }
        }

        public Func<string, IReadOnlyList<string>, SubscriptionUsageCommandResult>? Handler { get; init; }
        public Func<string, IReadOnlyList<string>, Task<SubscriptionUsageCommandResult>>? AsyncHandler { get; init; }

        public Task<SubscriptionUsageCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            lock (_commands)
            {
                _commands.Add((executable, arguments));
            }

            return AsyncHandler?.Invoke(executable, arguments)
                ?? Task.FromResult(
                    Handler?.Invoke(executable, arguments)
                    ?? new SubscriptionUsageCommandResult(0, string.Empty, string.Empty, false, false, false));
        }
    }

    private sealed class FakeSupplementalSource : ISupplementalSubscriptionUsageSource
    {
        private readonly ProviderSubscriptionUsageMessage? _message;
        private readonly Func<DateTimeOffset, CancellationToken, Task<ProviderSubscriptionUsageMessage?>>? _handler;

        public FakeSupplementalSource(string provider, ProviderSubscriptionUsageMessage? message)
        {
            Provider = provider;
            _message = message;
        }

        public FakeSupplementalSource(
            string provider,
            Func<DateTimeOffset, CancellationToken, Task<ProviderSubscriptionUsageMessage?>> handler)
        {
            Provider = provider;
            _handler = handler;
        }

        public string Provider { get; }

        public List<DateTimeOffset> ObservedAt { get; } = [];

        public Task<ProviderSubscriptionUsageMessage?> ReadAsync(
            DateTimeOffset observedAt,
            CancellationToken cancellationToken)
        {
            ObservedAt.Add(observedAt);
            return _handler?.Invoke(observedAt, cancellationToken) ?? Task.FromResult(_message);
        }
    }


    /// <summary>Yields <paramref name="prefix"/> once, then answers every later read with <paramref name="next"/>.</summary>
    private sealed class ScriptedStream(byte[] prefix, Func<CancellationToken, ValueTask<int>> next) : Stream
    {
        private bool _prefixSent;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_prefixSent)
            {
                return next(cancellationToken);
            }

            _prefixSent = true;
            prefix.CopyTo(buffer);
            return ValueTask.FromResult(prefix.Length);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
