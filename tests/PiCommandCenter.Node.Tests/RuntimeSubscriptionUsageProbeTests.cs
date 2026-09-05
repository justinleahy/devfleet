using System.Diagnostics;
using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.RuntimeRouting;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class RuntimeSubscriptionUsageProbeTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid NodeId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    /// <summary>
    /// Only documented non-interactive quota surface: standalone print-mode slash with a bounded wait
    /// advertised under the runner's 10s deadline so the CLI's own timeout path completes.
    /// </summary>
    private static readonly string[] AntigravityUsageArguments = ["-p", "/usage", "--print-timeout", "8s"];

    /// <summary>
    /// Observed <c>agy 1.1.27</c> stdout for <see cref="AntigravityUsageArguments"/>: one tab-separated row per
    /// window with model group, window label, remaining percent, and RFC 3339 reset. No account fields.
    /// </summary>
    private const string AntigravityUsageRows =
        "Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n"
        + "Gemini Models\tFive Hour Limit Remaining\t100%\t2026-09-05T16:10:47Z\n"
        + "Claude and GPT models\tWeekly Limit Remaining\t100%\t2026-09-12T12:34:46Z\n"
        + "Claude and GPT models\tFive Hour Limit Remaining\t100%\t2026-09-05T17:34:46Z\n";

    private static readonly SubscriptionUsageWindowMessage[] ReaderWindows =
    [
        new("primary", 12.5, 87.5, new DateTimeOffset(2026, 9, 5, 15, 0, 0, TimeSpan.Zero)),
        new("secondary", 40, 60, new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero)),
    ];

    [Fact]
    public async Task Pi_reads_quota_from_the_reader_without_launching_a_command()
    {
        var runner = new FakeRunner();
        var reader = new FakeQuotaReader
        {
            Pi = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, ReaderWindows, "pi-quota-fake", null, true, "Pro"),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.LocalPi], reader);

        var snapshot = await probe.GetAsync();

        Assert.Empty(runner.Commands);
        Assert.Equal(1, reader.PiReads);
        Assert.Equal(0, reader.ClaudeReads);
        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal("pi", provider.Provider);
        Assert.Equal([AgentRuntimeProfiles.LocalPi], provider.RuntimeProfiles);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Null(provider.Diagnostic);
        Assert.Equal(ReaderWindows, provider.Windows);
        Assert.Equal("pi-quota-fake", provider.Source);
        Assert.True(provider.Authenticated);
        Assert.Equal("Pro", provider.PlanLabel);
        Assert.Null(provider.Version);
        Assert.Equal(NodeId, snapshot.NodeId);
        Assert.Equal(Observed, provider.ObservedAt);
    }

    [Theory]
    [InlineData(SubscriptionQuotaReadStatus.Unavailable, SubscriptionUsageStatuses.Unavailable)]
    [InlineData(SubscriptionQuotaReadStatus.Error, SubscriptionUsageStatuses.Error)]
    public async Task Pi_closed_reader_result_keeps_its_diagnostic_and_drops_windows(
        SubscriptionQuotaReadStatus readStatus,
        string expectedStatus)
    {
        var reader = new FakeQuotaReader
        {
            // A closed answer that still carries windows must not leak them into the DTO.
            Pi = new ProviderSubscriptionQuotaReadResult(
                readStatus, ReaderWindows, "pi-quota-fake", "credential_missing", null, null),
        };
        var probe = Create(new FakeRunner(), [AgentRuntimeProfiles.LocalPi], reader);

        var snapshot = await probe.GetAsync();

        AssertClosed(snapshot, expectedStatus, "credential_missing");
        Assert.Equal("pi-quota-fake", Assert.Single(snapshot.Providers).Source);
    }

    [Fact]
    public async Task Claude_runs_version_and_auth_status_then_merges_reader_quota()
    {
        var runner = new FakeRunner
        {
            Handler = (executable, arguments) =>
            {
                Assert.Equal("claude-test", executable);
                if (arguments.SequenceEqual(["--version"]))
                {
                    return Ok("2.1.248 (Claude Code)\n");
                }

                Assert.Equal(["auth", "status"], arguments);
                return Ok("""{"loggedIn":true,"subscriptionType":"Max","email":"user@example.com","org":"Secret Org"}""");
            },
        };
        var reader = new FakeQuotaReader
        {
            Claude = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, ReaderWindows, "claude-quota-fake", null, false, "Pro"),
        };
        var probe = Create(
            runner,
            [AgentRuntimeProfiles.ClaudeReadOnly, AgentRuntimeProfiles.ClaudeReservedWrite],
            reader);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            [
                ("claude-test", new[] { "--version" }),
                ("claude-test", new[] { "auth", "status" }),
            ],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
        Assert.Equal(1, reader.ClaudeReads);
        Assert.Equal(0, reader.PiReads);
        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal("claude", provider.Provider);
        Assert.Equal(
            [AgentRuntimeProfiles.ClaudeReadOnly, AgentRuntimeProfiles.ClaudeReservedWrite],
            provider.RuntimeProfiles);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Null(provider.Diagnostic);
        Assert.Equal(ReaderWindows, provider.Windows);
        Assert.Equal("claude --version; claude auth status; claude-quota-fake", provider.Source);
        // The CLI's own sign-in answer and plan label win over what the reader inferred.
        Assert.True(provider.Authenticated);
        Assert.Equal("Max", provider.PlanLabel);
        Assert.Equal("2.1.248", provider.Version);
    }

    [Fact]
    public async Task Claude_closed_reader_result_keeps_version_and_sign_in_state()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("2.1.248")
                : Ok("""{"loggedIn":true}"""),
        };
        var reader = new FakeQuotaReader
        {
            Claude = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Unavailable, [], "claude-quota-fake", "credential_missing", null, "Team"),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly], reader);

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Unavailable, provider.Status);
        Assert.Equal("credential_missing", provider.Diagnostic);
        Assert.Empty(provider.Windows);
        Assert.Equal("2.1.248", provider.Version);
        Assert.True(provider.Authenticated);
        // The CLI reported no plan label, so the reader's fills the gap.
        Assert.Equal("Team", provider.PlanLabel);
    }

    public static TheoryData<IReadOnlyList<SubscriptionUsageWindowMessage>> IncoherentWindows => new()
    {
        Array.Empty<SubscriptionUsageWindowMessage>(),
        new SubscriptionUsageWindowMessage[] { new("primary", 10, 90, null), new("primary", 20, 80, null) },
        new SubscriptionUsageWindowMessage[] { new(" ", 10, 90, null) },
        new SubscriptionUsageWindowMessage[] { new("primary", null, null, null) },
        new SubscriptionUsageWindowMessage[] { new("primary", 101, null, null) },
        new SubscriptionUsageWindowMessage[] { new("primary", null, -0.5, null) },
        new SubscriptionUsageWindowMessage[] { new("primary", double.NaN, null, null) },
        new SubscriptionUsageWindowMessage[] { new("primary", 30, 60, null) },
        Enumerable.Range(0, RuntimeSubscriptionUsageProbe.MaxWindows + 1)
            .Select(i => new SubscriptionUsageWindowMessage($"window-{i}", 0, 100, null))
            .ToArray(),
    };

    [Theory]
    [MemberData(nameof(IncoherentWindows))]
    public async Task Reader_available_with_incoherent_windows_is_error(
        IReadOnlyList<SubscriptionUsageWindowMessage> windows)
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("2.1.248")
                : Ok("""{"loggedIn":true}"""),
        };
        var reader = new FakeQuotaReader
        {
            Pi = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, windows, "pi-quota-fake", null, null, null),
            Claude = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, windows, "claude-quota-fake", null, null, null),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.LocalPi, AgentRuntimeProfiles.ClaudeReadOnly], reader);

        var snapshot = await probe.GetAsync();

        Assert.Equal(["pi", "claude"], snapshot.Providers.Select(provider => provider.Provider));
        Assert.All(snapshot.Providers, provider =>
        {
            Assert.Equal(SubscriptionUsageStatuses.Error, provider.Status);
            Assert.Equal(RuntimeSubscriptionUsageProbe.QuotaIncoherent, provider.Diagnostic);
            Assert.Empty(provider.Windows);
        });
        Assert.Equal("2.1.248", snapshot.Providers[1].Version);
        Assert.True(snapshot.Providers[1].Authenticated);
    }

    [Fact]
    public async Task Closed_reader_result_without_a_diagnostic_is_still_diagnosed()
    {
        var reader = new FakeQuotaReader
        {
            Pi = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Error, [], "pi-quota-fake", null, null, null),
        };
        var probe = Create(new FakeRunner(), [AgentRuntimeProfiles.LocalPi], reader);

        var snapshot = await probe.GetAsync();

        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.QuotaIncoherent);
    }

    [Fact]
    public async Task Claude_auth_json_excludes_pii_from_snapshot()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : Ok("""{"loggedIn":false,"subscriptionType":"Pro","email":"alice@secret.test","organization":"Hidden LLC"}"""),
        };
        var reader = new FakeQuotaReader();
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly], reader);

        var snapshot = await probe.GetAsync();
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);

        Assert.DoesNotContain("alice@secret.test", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Hidden LLC", json, StringComparison.Ordinal);
        Assert.DoesNotContain("email", json, StringComparison.OrdinalIgnoreCase);
        var provider = Assert.Single(snapshot.Providers);
        Assert.False(provider.Authenticated);
        Assert.Equal("Pro", provider.PlanLabel);
        // Signed out per the CLI is closed here; the credential file is never consulted.
        Assert.Equal(SubscriptionUsageStatuses.Unavailable, provider.Status);
        Assert.Equal(RuntimeSubscriptionUsageProbe.SignedOut, provider.Diagnostic);
        Assert.Equal(0, reader.ClaudeReads);
    }

    [Theory]
    [InlineData("pro", "Pro")]
    [InlineData("MAX", "Max")]
    [InlineData("team", "Team")]
    [InlineData("enterprise", "Enterprise")]
    [InlineData("api", "API")]
    [InlineData("Pro ", null)]
    [InlineData("", null)]
    [InlineData("Pro Plus", null)]
    [InlineData("<script>alert(1)</script>", null)]
    [InlineData("alice@secret.test", null)]
    public async Task Claude_plan_label_is_canonical_allowlist_only(string raw, string? expected)
    {
        var escaped = System.Text.Json.JsonSerializer.Serialize(raw);
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : Ok($$"""{"loggedIn":true,"subscriptionType":{{escaped}} }"""),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(expected, provider.PlanLabel);
        if (expected is null && raw.Length > 0)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
            Assert.DoesNotContain(raw, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Claude_oversized_plan_label_never_reaches_dto()
    {
        var oversized = new string('M', 4096);
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : Ok($$"""{"loggedIn":true,"subscriptionType":"{{oversized}}"}"""),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Null(provider.PlanLabel);
        Assert.True(provider.Authenticated);
    }

    [Fact]
    public async Task Claude_non_string_plan_label_is_dropped_not_malformed()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : Ok("""{"loggedIn":true,"subscriptionType":{"tier":"Max"}}"""),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Unavailable, provider.Status);
        Assert.Null(provider.PlanLabel);
        Assert.True(provider.Authenticated);
    }

    [Fact]
    public async Task Claude_auth_exit_one_with_logged_out_json_is_signed_out_without_reading_quota()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : new SubscriptionUsageCommandResult(
                    1, """{"loggedIn":false}""", string.Empty, false, false, false),
        };
        // Stale credentials that would still answer must not be read once the CLI says signed out.
        var reader = new FakeQuotaReader
        {
            Claude = new ProviderSubscriptionQuotaReadResult(
                SubscriptionQuotaReadStatus.Available, ReaderWindows, "claude-quota-fake", null, true, "Max"),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly], reader);

        var snapshot = await probe.GetAsync();

        Assert.Equal(0, reader.ClaudeReads);
        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Unavailable, provider.Status);
        Assert.Equal(RuntimeSubscriptionUsageProbe.SignedOut, provider.Diagnostic);
        Assert.False(provider.Authenticated);
        Assert.Null(provider.PlanLabel);
        Assert.Empty(provider.Windows);
        Assert.Equal("1.0.0", provider.Version);
        Assert.Equal("claude --version; claude auth status", provider.Source);
    }

    [Theory]
    [InlineData("""{"loggedIn":true}""", RuntimeSubscriptionUsageProbe.ProcessFailed)]
    [InlineData("Not logged in", RuntimeSubscriptionUsageProbe.ProcessMalformed)]
    [InlineData("""{"loggedIn":false} trailing""", RuntimeSubscriptionUsageProbe.ProcessMalformed)]
    [InlineData("""{"loggedIn":false,}""", RuntimeSubscriptionUsageProbe.ProcessMalformed)]
    [InlineData("""{"loggedIn":"false"}""", RuntimeSubscriptionUsageProbe.ProcessMalformed)]
    [InlineData("", RuntimeSubscriptionUsageProbe.ProcessMalformed)]
    public async Task Claude_auth_exit_one_without_strict_logged_out_json_is_error(string stdout, string diagnostic)
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : new SubscriptionUsageCommandResult(1, stdout, "SECRET_STDERR", false, false, false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();

        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, diagnostic);
    }

    [Fact]
    public async Task Claude_auth_exit_two_is_error_even_with_logged_out_json()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.0.0")
                : new SubscriptionUsageCommandResult(2, """{"loggedIn":false}""", string.Empty, false, false, false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();

        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessFailed);
    }

    [Fact]
    public async Task Claude_readonly_and_write_collapse_to_one_provider()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("3.4.5 extra")
                : Ok("""{"loggedIn":true}"""),
        };
        var probe = Create(
            runner,
            [
                AgentRuntimeProfiles.ClaudeReservedWrite,
                AgentRuntimeProfiles.LocalPi,
                AgentRuntimeProfiles.ClaudeReadOnly,
            ]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(["claude", "pi"], snapshot.Providers.Select(provider => provider.Provider));
        Assert.Equal(
            [AgentRuntimeProfiles.ClaudeReadOnly, AgentRuntimeProfiles.ClaudeReservedWrite],
            snapshot.Providers[0].RuntimeProfiles);
        Assert.Equal(2, runner.Commands.Count);
    }

    [Fact]
    public async Task Antigravity_usage_rows_become_available_windows()
    {
        var runner = new FakeRunner
        {
            Handler = (executable, arguments) =>
            {
                Assert.Equal("agy-test", executable);
                return arguments.SequenceEqual(["--version"])
                    ? Ok("1.1.27\n")
                    : Ok(AntigravityUsageRows);
            },
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            [
                ("agy-test", new[] { "--version" }),
                ("agy-test", AntigravityUsageArguments),
            ],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal("antigravity", provider.Provider);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Null(provider.Diagnostic);
        Assert.Equal("1.1.27", provider.Version);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage(
                    "Gemini Models weekly", 0, 100, new DateTimeOffset(2026, 9, 10, 20, 10, 25, TimeSpan.Zero)),
                new SubscriptionUsageWindowMessage(
                    "Gemini Models five-hour", 0, 100, new DateTimeOffset(2026, 9, 5, 16, 10, 47, TimeSpan.Zero)),
                new SubscriptionUsageWindowMessage(
                    "Claude and GPT models weekly", 0, 100, new DateTimeOffset(2026, 9, 12, 12, 34, 46, TimeSpan.Zero)),
                new SubscriptionUsageWindowMessage(
                    "Claude and GPT models five-hour", 0, 100, new DateTimeOffset(2026, 9, 5, 17, 34, 46, TimeSpan.Zero)),
            ],
            provider.Windows);
        Assert.Equal(Observed, provider.ObservedAt);
    }

    [Fact]
    public async Task Antigravity_partial_remaining_is_used_plus_remaining_equals_one_hundred()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : Ok(
                    "Gemini Models\tWeekly Limit Remaining\t37%\t2026-09-10T20:10:25Z\n"
                    + "Gemini Models\tFive Hour Limit Remaining\t0%\t2026-09-05T16:10:47Z\n"),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage(
                    "Gemini Models weekly", 63, 37, new DateTimeOffset(2026, 9, 10, 20, 10, 25, TimeSpan.Zero)),
                new SubscriptionUsageWindowMessage(
                    "Gemini Models five-hour", 100, 0, new DateTimeOffset(2026, 9, 5, 16, 10, 47, TimeSpan.Zero)),
            ],
            provider.Windows);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Error: authentication required. Run 'agy' to log in, then retry.\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\textra\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t101%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t-1%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\tnext week\n")]
    [InlineData("Gemini Models\tMonthly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\nSECRET_TRAILER\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\nGemini Models\tWeekly Limit Remaining\t90%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10 20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t+5%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t 5%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tweekly limit remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini  Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("user@secret.test\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini <b>Models</b>\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models Gemini Models Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("   \nGemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    public async Task Antigravity_malformed_usage_rows_are_error_without_windows(string usageStdout)
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : Ok(usageStdout),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();

        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessMalformed);
    }

    [Fact]
    public async Task Antigravity_tolerates_blank_lines_crlf_and_numeric_offsets()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : Ok(
                    "\r\nGemini-2.5 Models\tWeekly Limit Remaining\t7%\t2026-09-10T22:10:25+02:00\r\n"
                    + "\r\n"
                    + "Gemini-2.5 Models\tFive Hour Limit Remaining\t100%\t2026-09-05T16:10:47.250Z"),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();

        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Equal(
            [
                new SubscriptionUsageWindowMessage(
                    "Gemini-2.5 Models weekly", 93, 7, new DateTimeOffset(2026, 9, 10, 20, 10, 25, TimeSpan.Zero)),
                new SubscriptionUsageWindowMessage(
                    "Gemini-2.5 Models five-hour", 0, 100, new DateTimeOffset(2026, 9, 5, 16, 10, 47, 250, TimeSpan.Zero)),
            ],
            provider.Windows);
        Assert.All(provider.Windows, window => Assert.Equal(TimeSpan.Zero, window.ResetsAt!.Value.Offset));
    }

    [Fact]
    public async Task Antigravity_accepts_the_window_cap_and_rejects_one_more()
    {
        static string Rows(int count)
            => string.Concat(
                Enumerable.Range(0, count)
                    .Select(i => $"Group {i}\tWeekly Limit Remaining\t50%\t2026-09-10T20:10:25Z\n"));

        var capped = Create(
            new FakeRunner
            {
                Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                    ? Ok("1.1.27")
                    : Ok(Rows(RuntimeSubscriptionUsageProbe.MaxWindows)),
            },
            [AgentRuntimeProfiles.AntigravityReadOnly]);
        var overflowing = Create(
            new FakeRunner
            {
                Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                    ? Ok("1.1.27")
                    : Ok(Rows(RuntimeSubscriptionUsageProbe.MaxWindows + 1)),
            },
            [AgentRuntimeProfiles.AntigravityReadOnly]);

        var cappedSnapshot = await capped.GetAsync();
        var overflowingSnapshot = await overflowing.GetAsync();

        var provider = Assert.Single(cappedSnapshot.Providers);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Equal(RuntimeSubscriptionUsageProbe.MaxWindows, provider.Windows.Count);
        AssertClosed(overflowingSnapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessMalformed);
    }

    [Theory]
    [InlineData(true, false, false, RuntimeSubscriptionUsageProbe.ProcessTimeout)]
    [InlineData(false, true, false, RuntimeSubscriptionUsageProbe.ProcessTruncated)]
    [InlineData(false, false, true, RuntimeSubscriptionUsageProbe.ProcessMissing)]
    public async Task Antigravity_usage_process_failures_close_without_partial_windows(
        bool timedOut,
        bool truncated,
        bool missing,
        string diagnostic)
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : new SubscriptionUsageCommandResult(
                    missing || timedOut ? null : 0, AntigravityUsageRows, "SECRET_STDERR", timedOut, truncated, missing),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();

        var status = missing ? SubscriptionUsageStatuses.Unavailable : SubscriptionUsageStatuses.Error;
        AssertClosed(snapshot, status, diagnostic);
    }

    [Fact]
    public async Task Antigravity_usage_nonzero_exit_is_error_without_raw_output()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : new SubscriptionUsageCommandResult(
                    1, string.Empty, "Error: authentication required. SECRET_STDERR", false, false, false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            [
                ("agy-test", new[] { "--version" }),
                ("agy-test", AntigravityUsageArguments),
            ],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessFailed);
    }

    [Fact]
    public async Task Providers_probe_concurrently_and_keep_profile_order()
    {
        var claudeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var antigravityStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var piStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var barrier = TimeSpan.FromSeconds(5);
        var runner = new FakeRunner
        {
            AsyncHandler = async (executable, arguments) =>
            {
                if (executable == "agy-test")
                {
                    if (!arguments.SequenceEqual(["--version"]))
                    {
                        return Ok(AntigravityUsageRows);
                    }

                    antigravityStarted.TrySetResult();
                    // Sequential probing would never reach Claude or Pi while this awaits, so the
                    // barrier fails instead of deadlocking.
                    await Task.WhenAll(claudeStarted.Task, piStarted.Task).WaitAsync(barrier);
                    return Ok("1.1.27");
                }

                if (arguments.SequenceEqual(["--version"]))
                {
                    claudeStarted.TrySetResult();
                    await antigravityStarted.Task.WaitAsync(barrier);
                    return Ok("2.0.0");
                }

                // Claude finishes last; output order must still follow the allowed profiles.
                await Task.Delay(50);
                return Ok("""{"loggedIn":true}""");
            },
        };
        var reader = new FakeQuotaReader
        {
            PiHandler = async _ =>
            {
                piStarted.TrySetResult();
                await antigravityStarted.Task.WaitAsync(barrier);
                return new ProviderSubscriptionQuotaReadResult(
                    SubscriptionQuotaReadStatus.Available, ReaderWindows, "pi-quota-fake", null, null, null);
            },
        };
        var probe = Create(
            runner,
            [
                AgentRuntimeProfiles.AntigravityReadOnly,
                AgentRuntimeProfiles.ClaudeReadOnly,
                AgentRuntimeProfiles.LocalPi,
                "mystery-runtime",
            ],
            reader);

        var snapshot = await probe.GetAsync();

        Assert.Equal(
            ["antigravity", "claude", "pi", "unknown"],
            snapshot.Providers.Select(provider => provider.Provider));
        Assert.Equal("1.1.27", snapshot.Providers[0].Version);
        Assert.Equal(SubscriptionUsageStatuses.Available, snapshot.Providers[0].Status);
        Assert.Equal("2.0.0", snapshot.Providers[1].Version);
        Assert.Equal(SubscriptionUsageStatuses.Available, snapshot.Providers[2].Status);
        Assert.Equal(4, runner.Commands.Count);
    }

    [Fact]
    public async Task Timeout_is_error_without_raw_output()
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(
                null, "SECRET_STDOUT", "SECRET_STDERR", TimedOut: true, Truncated: false, Missing: false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();
        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessTimeout);
    }

    [Fact]
    public async Task Missing_executable_is_unavailable_without_raw_output()
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(
                null, "not found /home/secret", "ENOENT", false, false, Missing: true),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();
        AssertClosed(snapshot, SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.ProcessMissing);
    }

    [Fact]
    public async Task Unconfigured_executable_is_unavailable_without_commands()
    {
        var runner = new FakeRunner();
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly], claudeExecutable: "  ");

        var snapshot = await probe.GetAsync();

        Assert.Empty(runner.Commands);
        AssertClosed(snapshot, SubscriptionUsageStatuses.Unavailable, RuntimeSubscriptionUsageProbe.ProcessMissing);
    }

    [Fact]
    public async Task Start_failure_without_exit_code_is_error()
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(
                null, string.Empty, string.Empty, false, false, false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();
        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessFailed);
    }

    [Fact]
    public async Task Malformed_version_and_auth_are_error()
    {
        var versionProbe = Create(
            new FakeRunner { Handler = (_, _) => Ok("not-a-version") },
            [AgentRuntimeProfiles.ClaudeReadOnly]);
        var authProbe = Create(
            new FakeRunner
            {
                Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                    ? Ok("9.9.9")
                    : Ok("logged in as user@example.com"),
            },
            [AgentRuntimeProfiles.ClaudeReadOnly]);

        var versionSnapshot = await versionProbe.GetAsync();
        var authSnapshot = await authProbe.GetAsync();

        AssertClosed(versionSnapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessMalformed);
        AssertClosed(authSnapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessMalformed);
        var json = System.Text.Json.JsonSerializer.Serialize(authSnapshot);
        Assert.DoesNotContain("user@example.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Truncated_output_is_error_without_raw_output()
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(
                0, new string('x', 200), "truncated-secret", false, Truncated: true, Missing: false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.AntigravityReadOnly]);

        var snapshot = await probe.GetAsync();
        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessTruncated);
    }

    [Fact]
    public async Task Unknown_profile_is_unavailable_without_commands()
    {
        var runner = new FakeRunner();
        var probe = Create(runner, ["mystery-runtime"]);

        var snapshot = await probe.GetAsync();

        Assert.Empty(runner.Commands);
        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal("unknown", provider.Provider);
        Assert.Equal(["mystery-runtime"], provider.RuntimeProfiles);
        Assert.Equal(SubscriptionUsageStatuses.Unavailable, provider.Status);
        Assert.Equal(RuntimeSubscriptionUsageProbe.UnknownDiagnostic, provider.Diagnostic);
        Assert.Empty(provider.Windows);
    }

    [Fact]
    public async Task Nonzero_exit_is_error_with_stable_process_failed()
    {
        var runner = new FakeRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(
                1, "oops", "stack /home/me/token", false, false, false),
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        var snapshot = await probe.GetAsync();
        AssertClosed(snapshot, SubscriptionUsageStatuses.Error, RuntimeSubscriptionUsageProbe.ProcessFailed);
    }

    [Fact]
    public async Task No_outcome_is_available_with_empty_windows()
    {
        SubscriptionUsageCommandResult[] outcomes =
        [
            Ok("1.0.0"),
            Ok("""{"loggedIn":true,"subscriptionType":"Max"}"""),
            Ok("garbage"),
            new(1, """{"loggedIn":false}""", string.Empty, false, false, false),
            new(2, string.Empty, string.Empty, false, false, false),
            new(null, string.Empty, string.Empty, TimedOut: true, Truncated: false, Missing: false),
            new(0, string.Empty, string.Empty, false, Truncated: true, Missing: false),
            new(null, string.Empty, string.Empty, false, false, Missing: true),
        ];

        foreach (var version in outcomes)
        {
            foreach (var auth in outcomes)
            {
                var runner = new FakeRunner
                {
                    Handler = (_, arguments) => arguments.SequenceEqual(["--version"]) ? version : auth,
                };
                var probe = Create(
                    runner,
                    [AgentRuntimeProfiles.ClaudeReadOnly, AgentRuntimeProfiles.AntigravityReadOnly, AgentRuntimeProfiles.LocalPi]);

                var snapshot = await probe.GetAsync();

                Assert.All(snapshot.Providers, provider =>
                {
                    Assert.NotEqual(SubscriptionUsageStatuses.Available, provider.Status);
                    Assert.Empty(provider.Windows);
                    Assert.False(string.IsNullOrEmpty(provider.Diagnostic));
                });
            }
        }
    }

    [Fact]
    public async Task Duplicate_non_claude_profiles_are_probed_once_in_first_appearance_order()
    {
        var runner = new FakeRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"]) ? Ok("1.1.27") : Ok(AntigravityUsageRows),
        };
        var reader = new FakeQuotaReader();
        var probe = Create(
            runner,
            [
                AgentRuntimeProfiles.LocalPi,
                AgentRuntimeProfiles.AntigravityReadOnly,
                "mystery-runtime",
                AgentRuntimeProfiles.LocalPi,
                AgentRuntimeProfiles.AntigravityReadOnly,
                "mystery-runtime",
            ],
            reader);

        var snapshot = await probe.GetAsync();

        Assert.Equal(["pi", "antigravity", "unknown"], snapshot.Providers.Select(provider => provider.Provider));
        Assert.Equal(1, reader.PiReads);
        Assert.Equal(
            [
                ("agy-test", new[] { "--version" }),
                ("agy-test", AntigravityUsageArguments),
            ],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
    }

    [Fact]
    public async Task Cancelled_token_launches_nothing()
    {
        var runner = new FakeRunner { Handler = (_, _) => Ok("1.0.0") };
        var reader = new FakeQuotaReader();
        var probe = Create(
            runner,
            [AgentRuntimeProfiles.ClaudeReadOnly, AgentRuntimeProfiles.AntigravityReadOnly, AgentRuntimeProfiles.LocalPi],
            reader);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetAsync(cts.Token));

        Assert.Empty(runner.Commands);
        Assert.Equal(0, reader.PiReads);
        Assert.Equal(0, reader.ClaudeReads);
    }

    [Fact]
    public async Task Cancellation_between_claude_commands_skips_auth_status()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner
        {
            Handler = (_, _) =>
            {
                cts.Cancel();
                return Ok("1.0.0");
            },
        };
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetAsync(cts.Token));

        Assert.Equal(
            [("claude-test", new[] { "--version" })],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
    }

    [Fact]
    public async Task Cancellation_after_claude_auth_status_skips_the_quota_reader()
    {
        using var cts = new CancellationTokenSource();
        var runner = new FakeRunner
        {
            Handler = (_, arguments) =>
            {
                if (arguments.SequenceEqual(["--version"]))
                {
                    return Ok("1.0.0");
                }

                cts.Cancel();
                return Ok("""{"loggedIn":true}""");
            },
        };
        var reader = new FakeQuotaReader();
        var probe = Create(runner, [AgentRuntimeProfiles.ClaudeReadOnly], reader);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.GetAsync(cts.Token));

        Assert.Equal(2, runner.Commands.Count);
        Assert.Equal(0, reader.ClaudeReads);
    }

    [Fact]
    public async Task Runner_rejects_a_cancelled_token_before_starting_a_process()
    {
        var runner = new RuntimeSubscriptionUsageCommandRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Without the pre-start check this would report Missing instead of cancellation.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunAsync("pi-command-center-no-such-executable", ["--version"], cts.Token));
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

        // Both pipes exceed the OS pipe buffer; a reader that stopped at the budget would block the child.
        var result = await runner.RunAsync(
            "/bin/sh",
            ["-c", "head -c 100000 /dev/zero; head -c 100000 /dev/zero >&2"],
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.False(result.TimedOut);
        Assert.Equal(
            RuntimeSubscriptionUsageCommandRunner.MaxOutputBytes,
            result.StandardOutput.Length + result.StandardError.Length);
    }

    [Fact]
    public async Task Runner_parent_exit_with_a_descendant_holding_the_pipe_is_timeout_without_partial_output()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runner = new RuntimeSubscriptionUsageCommandRunner(TimeSpan.FromMilliseconds(300));

        // The parent prints a valid version and exits 0 while the background child keeps stdout open.
        var result = await runner.RunAsync("/bin/sh", ["-c", "echo 1.2.3; sleep 2 & exit 0"], CancellationToken.None);

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
        var stream = new ScriptedStream("1.2."u8.ToArray(), _ => ValueTask.FromException<int>(new IOException("reset")));

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
        var stream = new ScriptedStream("1.2."u8.ToArray(), token =>
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

    private static void AssertClosed(NodeSubscriptionUsageMessage snapshot, string status, string diagnostic)
    {
        var provider = Assert.Single(snapshot.Providers);
        Assert.Equal(status, provider.Status);
        Assert.Equal(diagnostic, provider.Diagnostic);
        Assert.Empty(provider.Windows);
        Assert.Null(provider.Version);
        Assert.Null(provider.PlanLabel);
        Assert.Null(provider.Authenticated);
        var json = System.Text.Json.JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("SECRET", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("oops", json, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated-secret", json, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", json, StringComparison.Ordinal);
        Assert.DoesNotContain("loggedIn", json, StringComparison.Ordinal);
    }

    private static RuntimeSubscriptionUsageProbe Create(
        IRuntimeSubscriptionUsageCommandRunner runner,
        IReadOnlyList<string> profiles,
        FakeQuotaReader? reader = null,
        string claudeExecutable = "claude-test")
        => new(
            new StubRoutes(profiles),
            Options.Create(new NodeOptions { Id = NodeId }),
            Options.Create(new ClaudeCodeOptions { Executable = claudeExecutable }),
            Options.Create(new AntigravityOptions { Executable = "agy-test" }),
            new FixedTime(),
            reader ?? new FakeQuotaReader(),
            runner);

    private static SubscriptionUsageCommandResult Ok(string stdout)
        => new(0, stdout, string.Empty, false, false, false);

    /// <summary>Answers closed by default so command-path tests never depend on quota; counts every read.</summary>
    private sealed class FakeQuotaReader : IProviderSubscriptionQuotaReader
    {
        public const string ClosedDiagnostic = "quota_fake_closed";

        private int _piReads;
        private int _claudeReads;

        public ProviderSubscriptionQuotaReadResult Pi { get; init; } = new(
            SubscriptionQuotaReadStatus.Unavailable, [], "pi-quota-fake", ClosedDiagnostic, null, null);

        public ProviderSubscriptionQuotaReadResult Claude { get; init; } = new(
            SubscriptionQuotaReadStatus.Unavailable, [], "claude-quota-fake", ClosedDiagnostic, null, null);

        public Func<CancellationToken, Task<ProviderSubscriptionQuotaReadResult>>? PiHandler { get; init; }

        public int PiReads => Volatile.Read(ref _piReads);

        public int ClaudeReads => Volatile.Read(ref _claudeReads);

        public Task<ProviderSubscriptionQuotaReadResult> ReadPiAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _piReads);
            return PiHandler?.Invoke(cancellationToken) ?? Task.FromResult(Pi);
        }

        public Task<ProviderSubscriptionQuotaReadResult> ReadClaudeAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _claudeReads);
            return Task.FromResult(Claude);
        }
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Observed;
    }

    private sealed class StubRoutes : INodeRuntimeRoutingStore
    {
        public StubRoutes(IReadOnlyList<string> profiles)
        {
            Current = new NodeRuntimeConfigurationMessage(NodeId, ["root"], profiles, []);
        }

        public NodeRuntimeConfigurationMessage Current { get; }

        public Task<NodeRuntimeConfigurationMessage> UpdateAsync(
            UpdateNodeRuntimeConfigurationMessage update,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
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

            if (AsyncHandler is not null)
            {
                return AsyncHandler(executable, arguments);
            }

            return Task.FromResult(
                Handler?.Invoke(executable, arguments)
                ?? new SubscriptionUsageCommandResult(0, string.Empty, string.Empty, false, false, false));
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
