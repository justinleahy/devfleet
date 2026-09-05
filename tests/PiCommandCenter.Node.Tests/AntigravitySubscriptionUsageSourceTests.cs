using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Runtime.Antigravity;
using PiCommandCenter.Node.SubscriptionUsage;

namespace PiCommandCenter.Node.Tests;

public sealed class AntigravitySubscriptionUsageSourceTests
{
    private static readonly DateTimeOffset Observed = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Observed <c>agy 1.1.27</c> stdout for <c>agy -p /usage --print-timeout 8s</c>.
    /// The official print-mode report contains one four-column TSV row per quota window.
    /// </summary>
    private const string OfficialUsageFixture =
        "Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n"
        + "Gemini Models\tFive Hour Limit Remaining\t100%\t2026-09-05T16:10:47Z\n"
        + "Claude and GPT models\tWeekly Limit Remaining\t100%\t2026-09-12T12:34:46Z\n"
        + "Claude and GPT models\tFive Hour Limit Remaining\t100%\t2026-09-05T17:34:46Z\n";

    [Fact]
    public async Task Production_runner_applies_read_only_sandbox_with_fixed_temp_chdir()
    {
        ProcessStartInfo? captured = null;
        var bwrap = Environment.ProcessPath!;
        var runner = new AntigravitySubscriptionUsageCommandRunner(
            executeAsync: (startInfo, _) =>
            {
                captured = startInfo;
                return Task.FromResult(Ok(string.Empty));
            },
            bwrapPath: bwrap,
            maskedLocations: []);

        await runner.RunAsync("/bin/echo", ["--version"], CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(bwrap, captured.FileName);
        Assert.Equal("/tmp", captured.WorkingDirectory);
        Assert.Equal(
            [
                "--die-with-parent", "--new-session", "--unshare-pid",
                "--ro-bind", "/", "/",
                "--dev", "/dev",
                "--proc", "/proc",
                "--ro-bind", "/tmp", "/tmp",
                "--chdir", "/tmp",
                "--", "/bin/echo", "--version",
            ],
            captured.ArgumentList.ToArray());
    }

    [Fact]
    public async Task Production_runner_refuses_to_start_command_when_sandbox_is_unavailable()
    {
        var started = false;
        var runner = new AntigravitySubscriptionUsageCommandRunner(
            executeAsync: (_, _) =>
            {
                started = true;
                return Task.FromResult(Ok(string.Empty));
            },
            bwrapPath: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "bwrap"),
            maskedLocations: []);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runner.RunAsync("/bin/echo", ["--version"], CancellationToken.None));

        Assert.Equal(AntigravityReadOnlySandbox.UnavailableMessage, exception.Message);
        Assert.False(started);
    }

    [Fact]
    public async Task Production_runner_preserves_missing_executable_without_starting_process()
    {
        var started = false;
        var runner = new AntigravitySubscriptionUsageCommandRunner(
            executeAsync: (_, _) =>
            {
                started = true;
                return Task.FromResult(Ok(string.Empty));
            },
            bwrapPath: Environment.ProcessPath,
            maskedLocations: []);

        var result = await runner.RunAsync(
            "pi-command-center-no-such-antigravity-executable",
            ["--version"],
            CancellationToken.None);

        Assert.True(result.Missing);
        Assert.Null(result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput);
        Assert.Equal(string.Empty, result.StandardError);
        Assert.False(started);
    }

    [Fact]
    public void AddPiNode_registers_the_dedicated_antigravity_usage_runner()
    {
        var services = new ServiceCollection().AddPiNode();

        Assert.Contains(
            services,
            descriptor =>
                descriptor.ServiceType == typeof(IAntigravitySubscriptionUsageCommandRunner)
                && descriptor.ImplementationType == typeof(AntigravitySubscriptionUsageCommandRunner));
    }

    [Fact]
    public async Task Official_usage_fixture_runs_exact_argv_and_yields_coherent_windows()
    {
        var runner = new FakeSandboxRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27\n")
                : Ok(OfficialUsageFixture),
        };
        var source = Create(runner);

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        Assert.Equal("google-antigravity", source.Provider);
        Assert.Equal(
            [
                ("agy-test", new[] { "--version" }),
                ("agy-test", new[] { "-p", "/usage", "--print-timeout", "8s" }),
            ],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
        Assert.NotNull(provider);
        Assert.Equal("google-antigravity", provider.Provider);
        Assert.Equal(SubscriptionUsageStatuses.Available, provider.Status);
        Assert.Null(provider.Authenticated);
        Assert.Null(provider.PlanLabel);
        Assert.Equal("1.1.27", provider.Version);
        Assert.Equal(Observed, provider.ObservedAt);
        Assert.Equal("agy --version; agy -p /usage --print-timeout 8s", provider.Source);
        Assert.Null(provider.Diagnostic);
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
        Assert.All(
            provider.Windows,
            window => Assert.Equal(100d, window.PercentUsed!.Value + window.PercentRemaining!.Value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\textra\n")]
    [InlineData("Gemini Models\tMonthly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("user@secret.test\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini  Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t101%\t2026-09-10T20:10:25Z\n")]
    [InlineData("Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25\n")]
    public async Task Malformed_usage_is_closed(string stdout)
    {
        var source = Create(new FakeSandboxRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : Ok(stdout),
        });

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        AssertClosed(provider, RuntimeSubscriptionUsageProbe.ProcessMalformed);
    }

    [Fact]
    public async Task Duplicate_window_is_closed()
    {
        const string duplicate =
            "Gemini Models\tWeekly Limit Remaining\t100%\t2026-09-10T20:10:25Z\n"
            + "Gemini Models\tWeekly Limit Remaining\t90%\t2026-09-11T20:10:25Z\n";
        var source = Create(new FakeSandboxRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : Ok(duplicate),
        });

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        AssertClosed(provider, RuntimeSubscriptionUsageProbe.ProcessMalformed);
    }

    [Fact]
    public async Task Ninth_window_is_closed()
    {
        var rows = string.Concat(
            Enumerable.Range(1, RuntimeSubscriptionUsageProbe.MaxWindows + 1)
                .Select(i => $"Group {i}\tWeekly Limit Remaining\t50%\t2026-09-10T20:10:25Z\n"));
        var source = Create(new FakeSandboxRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : Ok(rows),
        });

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        AssertClosed(provider, RuntimeSubscriptionUsageProbe.ProcessMalformed);
    }

    [Fact]
    public async Task Non_semver_version_is_closed_before_usage()
    {
        var runner = new FakeSandboxRunner { Handler = (_, _) => Ok("agy version unknown SECRET_VERSION") };
        var source = Create(runner);

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        AssertClosed(provider, RuntimeSubscriptionUsageProbe.ProcessMalformed);
        Assert.Equal([("agy-test", new[] { "--version" })],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
    }

    [Theory]
    [InlineData(true, false, 0, RuntimeSubscriptionUsageProbe.ProcessTimeout)]
    [InlineData(false, true, 0, RuntimeSubscriptionUsageProbe.ProcessTruncated)]
    [InlineData(false, false, 1, RuntimeSubscriptionUsageProbe.ProcessFailed)]
    public async Task Usage_process_failure_is_closed_without_raw_output(
        bool timedOut,
        bool truncated,
        int exitCode,
        string diagnostic)
    {
        var source = Create(new FakeSandboxRunner
        {
            Handler = (_, arguments) => arguments.SequenceEqual(["--version"])
                ? Ok("1.1.27")
                : new SubscriptionUsageCommandResult(
                    timedOut ? null : exitCode,
                    "SECRET_STDOUT " + OfficialUsageFixture,
                    "SECRET_STDERR /home/private/token",
                    timedOut,
                    truncated,
                    Missing: false),
        });

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        AssertClosed(provider, diagnostic);
        var serialized = JsonSerializer.Serialize(provider);
        Assert.DoesNotContain("SECRET", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("/home/", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("token", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gemini Models", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_executable_returns_null_without_leaking_runner_output()
    {
        var source = Create(new FakeSandboxRunner
        {
            Handler = (_, _) => new SubscriptionUsageCommandResult(
                null,
                "SECRET_STDOUT /home/private",
                "SECRET_STDERR token",
                TimedOut: false,
                Truncated: false,
                Missing: true),
        });

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        Assert.Null(provider);
    }

    [Fact]
    public async Task Unconfigured_executable_returns_null_without_running_commands()
    {
        var runner = new FakeSandboxRunner();
        var source = Create(runner, executable: "  ");

        var provider = await source.ReadAsync(Observed, CancellationToken.None);

        Assert.Null(provider);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Cancellation_before_read_runs_no_commands()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var runner = new FakeSandboxRunner();
        var source = Create(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.ReadAsync(Observed, cancellation.Token));

        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Cancellation_between_commands_skips_usage()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeSandboxRunner
        {
            Handler = (_, _) =>
            {
                cancellation.Cancel();
                return Ok("1.1.27");
            },
        };
        var source = Create(runner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => source.ReadAsync(Observed, cancellation.Token));

        Assert.Equal([("agy-test", new[] { "--version" })],
            runner.Commands.Select(command => (command.Executable, command.Arguments.ToArray())));
    }

    private static AntigravitySubscriptionUsageSource Create(FakeSandboxRunner runner, string executable = "agy-test")
        => new(Options.Create(new AntigravityOptions { Executable = executable }), runner);

    private static SubscriptionUsageCommandResult Ok(string stdout)
        => new(0, stdout, string.Empty, TimedOut: false, Truncated: false, Missing: false);

    private static void AssertClosed(ProviderSubscriptionUsageMessage? provider, string diagnostic)
    {
        Assert.NotNull(provider);
        Assert.Equal("google-antigravity", provider.Provider);
        Assert.Equal(SubscriptionUsageStatuses.Error, provider.Status);
        Assert.Null(provider.Authenticated);
        Assert.Null(provider.PlanLabel);
        Assert.Null(provider.Version);
        Assert.Empty(provider.Windows);
        Assert.Equal(Observed, provider.ObservedAt);
        Assert.Equal("agy --version; agy -p /usage --print-timeout 8s", provider.Source);
        Assert.Equal(diagnostic, provider.Diagnostic);
    }

    private sealed class FakeSandboxRunner : IAntigravitySubscriptionUsageCommandRunner
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

        public Task<SubscriptionUsageCommandResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            lock (_commands)
            {
                _commands.Add((executable, arguments));
            }

            return Task.FromResult(
                Handler?.Invoke(executable, arguments)
                ?? new SubscriptionUsageCommandResult(0, string.Empty, string.Empty, false, false, false));
        }
    }
}
