using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Verification;

namespace PiCommandCenter.Node.Tests;

public class VerificationOptionsValidatorTests
{
    [Fact]
    public void Empty_profiles_are_valid()
    {
        var result = new VerificationOptionsValidator().Validate(
            VerificationOptions.SectionName,
            new VerificationOptions());
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Shell_metacharacters_in_the_executable_fail_startup_validation()
    {
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["default"] = new VerificationProfileOptions
                {
                    Id = "default",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = "bad",
                            Executable = "dotnet; rm -rf /",
                            Arguments = ["test"],
                            WorkingDirectory = ".",
                            TimeoutSeconds = 1,
                        },
                    ],
                },
            },
        };

        var result = new VerificationOptionsValidator().Validate(VerificationOptions.SectionName, options);
        Assert.True(result.Failed);
    }

    [Fact]
    public void Parent_working_directories_fail_startup_validation()
    {
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["default"] = new VerificationProfileOptions
                {
                    Id = "default",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = "bad",
                            Executable = "dotnet",
                            WorkingDirectory = "../escape",
                            TimeoutSeconds = 1,
                        },
                    ],
                },
            },
        };

        var result = new VerificationOptionsValidator().Validate(VerificationOptions.SectionName, options);
        Assert.True(result.Failed);
    }
}

public class VerificationCommandRunnerTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "pi-cc-verify", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static VerificationRunContext Ctx(string repo) => new(
        Guid.NewGuid(), Guid.NewGuid(), "session-root", repo);

    private static VerificationCommandRunner Runner(
        FakeReservationGateway gateway,
        params VerificationCommandOptions[] commands)
    {
        var options = Options.Create(new VerificationOptions
        {
            MaxOutputBytes = 1024,
            Profiles =
            {
                ["default"] = new VerificationProfileOptions
                {
                    Id = "default",
                    Commands = [.. commands],
                },
            },
        });
        return new VerificationCommandRunner(options, gateway);
    }

    [Fact]
    public async Task Unknown_profile_is_rejected_without_acquiring_a_lease()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, TrueCommand());

        var ex = await Assert.ThrowsAsync<VerificationRejectedException>(
            () => runner.RunAsync(Ctx(_repo), "not-configured", null, CancellationToken.None));

        Assert.Equal("unknown_profile", ex.Code);
        Assert.Empty(gateway.Acquires);
    }

    [Fact]
    public async Task Unknown_command_id_is_rejected_without_acquiring_a_lease()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, TrueCommand());

        var ex = await Assert.ThrowsAsync<VerificationRejectedException>(
            () => runner.RunAsync(Ctx(_repo), "default", "nope", CancellationToken.None));

        Assert.Equal("unknown_command", ex.Code);
        Assert.Empty(gateway.Acquires);
    }

    [Fact]
    public async Task Successful_command_acquires_project_build_and_releases_it()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, TrueCommand());

        var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Commands[0].ExitCode);
        var scope = Assert.Single(Assert.Single(gateway.Acquires).Scopes);
        Assert.Equal("resource", scope.Kind);
        Assert.Equal(VerificationOptions.ProjectBuildResource, scope.Path);
        Assert.Single(gateway.Releases);
    }

    [Fact]
    public async Task Active_source_mutation_blocks_verification_before_build_lease()
    {
        var gateway = new FakeReservationGateway();
        gateway.Seed(new ReservationLeaseInfo(
            Guid.NewGuid(),
            1,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(1),
            [new ReservationScopeSpec("file", "src/A.cs")],
            "implementer"));
        var runner = Runner(gateway, TrueCommand());

        var ex = await Assert.ThrowsAsync<VerificationRejectedException>(
            () => runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None));

        Assert.Equal("active_source_mutation", ex.Code);
        Assert.Empty(gateway.Acquires);
    }

    [Fact]
    public async Task Timeout_kills_the_process_and_still_releases_the_build_lease()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, new VerificationCommandOptions
        {
            Id = "sleep",
            Executable = "/bin/sleep",
            Arguments = ["30"],
            WorkingDirectory = ".",
            TimeoutSeconds = 1,
        });

        var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Commands[0].TimedOut);
        Assert.Single(gateway.Releases);
    }

    [Fact]
    public async Task Cancel_kills_the_process_and_still_releases_the_build_lease()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, new VerificationCommandOptions
        {
            Id = "sleep",
            Executable = "/bin/sleep",
            Arguments = ["30"],
            WorkingDirectory = ".",
            TimeoutSeconds = 30,
        });
        using var cts = new CancellationTokenSource();
        var run = runner.RunAsync(Ctx(_repo), "default", null, cts.Token);
        await Task.Delay(200);
        cts.Cancel();

        var result = await run;

        Assert.True(result.Commands[0].Cancelled);
        Assert.Single(gateway.Releases);
    }

    [Fact]
    public async Task Process_crash_is_recorded_and_the_build_lease_is_released()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, new VerificationCommandOptions
        {
            Id = "crash",
            Executable = "/bin/sh",
            Arguments = ["-c", "kill -9 $$"],
            WorkingDirectory = ".",
            TimeoutSeconds = 5,
        });

        var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.True(result.Commands[0].Crashed || result.Commands[0].ExitCode is not 0);
        Assert.Single(gateway.Releases);
    }

    [Fact]
    public async Task Output_is_truncated_at_the_configured_bound()
    {
        var gateway = new FakeReservationGateway();
        var options = Options.Create(new VerificationOptions
        {
            MaxOutputBytes = 64,
            Profiles =
            {
                ["default"] = new VerificationProfileOptions
                {
                    Id = "default",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = "yes",
                            Executable = "/usr/bin/head",
                            Arguments = ["-c", "4096", "/dev/zero"],
                            WorkingDirectory = ".",
                            TimeoutSeconds = 5,
                        },
                    ],
                },
            },
        });
        var runner = new VerificationCommandRunner(options, gateway);

        var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

        Assert.True(result.Commands[0].OutputTruncated);
        Assert.True(result.Commands[0].StandardOutput.Length <= 64);
        Assert.Single(gateway.Releases);
    }

    [Fact]
    public async Task Failed_acquire_does_not_run_the_command()
    {
        var gateway = new FakeReservationGateway
        {
            AcquireError = new GatewayError("conflict", "project-build held"),
        };
        var runner = Runner(gateway, TrueCommand());

        var ex = await Assert.ThrowsAsync<VerificationRejectedException>(
            () => runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None));

        Assert.Equal("conflict", ex.Code);
        Assert.Empty(gateway.Releases);
    }

    [Fact]
    public async Task Injected_secret_and_host_home_are_not_exposed_and_a_normal_command_still_works()
    {
        const string secret = "pi-cc-injected-secret-marker-9f3a";
        const string homeMarker = "/home/real-home-marker-9f3a";
        var previousSecret = Environment.GetEnvironmentVariable("PI_CC_INJECTED_SECRET");
        var previousHome = Environment.GetEnvironmentVariable("PI_CC_HOME_MARKER");
        Environment.SetEnvironmentVariable("PI_CC_INJECTED_SECRET", secret);
        Environment.SetEnvironmentVariable("PI_CC_HOME_MARKER", homeMarker);
        try
        {
            var gateway = new FakeReservationGateway();
            var leak = Runner(gateway, new VerificationCommandOptions
            {
                Id = "printenv",
                Executable = "/usr/bin/printenv",
                Arguments = [],
                WorkingDirectory = ".",
                TimeoutSeconds = 5,
            });

            var leaked = await leak.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);
            var combined = leaked.Commands[0].StandardOutput + leaked.Commands[0].StandardError;
            Assert.DoesNotContain(secret, combined, StringComparison.Ordinal);
            Assert.DoesNotContain(homeMarker, combined, StringComparison.Ordinal);
            Assert.DoesNotContain("PI_CC_INJECTED_SECRET", combined, StringComparison.Ordinal);

            var ok = await Runner(new FakeReservationGateway(), TrueCommand())
                .RunAsync(Ctx(_repo), "default", null, CancellationToken.None);
            Assert.True(ok.Succeeded);
            Assert.Equal(0, ok.Commands[0].ExitCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PI_CC_INJECTED_SECRET", previousSecret);
            Environment.SetEnvironmentVariable("PI_CC_HOME_MARKER", previousHome);
        }
    }

    [Fact]
    public async Task Repository_verification_cannot_read_host_home_credentials()
    {
        var hostSecretPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            $".pi-cc-verification-secret-{Guid.NewGuid():N}");
        const string marker = "credential-must-not-enter-verification";
        await File.WriteAllTextAsync(hostSecretPath, marker);
        try
        {
            var runner = Runner(new FakeReservationGateway(), new VerificationCommandOptions
            {
                Id = "credential-read",
                Executable = "/usr/bin/cat",
                Arguments = [hostSecretPath],
                WorkingDirectory = ".",
                TimeoutSeconds = 5,
            });

            var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.NotEqual(0, result.Commands[0].ExitCode);
            Assert.DoesNotContain(marker, result.Commands[0].StandardOutput, StringComparison.Ordinal);
            Assert.DoesNotContain(marker, result.Commands[0].StandardError, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(hostSecretPath);
        }
    }

    [Fact]
    public async Task Repository_verification_cannot_see_the_host_process_table()
    {
        var runner = Runner(new FakeReservationGateway(), new VerificationCommandOptions
        {
            Id = "host-process-check",
            Executable = "/usr/bin/test",
            Arguments = ["!", "-e", $"/proc/{Environment.ProcessId}/cmdline"],
            WorkingDirectory = ".",
            TimeoutSeconds = 5,
        });

        var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.Commands[0].ExitCode);
    }

    private static VerificationCommandOptions TrueCommand() => new()
    {
        Id = "true",
        Executable = "/bin/true",
        Arguments = [],
        WorkingDirectory = ".",
        TimeoutSeconds = 5,
    };
}
