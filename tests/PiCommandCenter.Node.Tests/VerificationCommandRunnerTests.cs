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

    private static VerificationCommandOptions TrueCommand() => new()
    {
        Id = "true",
        Executable = "/bin/true",
        Arguments = [],
        WorkingDirectory = ".",
        TimeoutSeconds = 5,
    };
}
