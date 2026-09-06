using Microsoft.Extensions.Options;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Quiescence;
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

    [Theory]
    [InlineData("repository-integrity")]
    [InlineData("whitespace")]
    [InlineData(" repository-integrity ")]
    public void Baseline_command_ids_fail_startup_validation(string commandId)
    {
        var options = new VerificationOptions
        {
            Profiles =
            {
                ["quality"] = new VerificationProfileOptions
                {
                    Id = "quality",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = commandId,
                            Executable = "dotnet",
                            WorkingDirectory = ".",
                            TimeoutSeconds = 1,
                            Mandatory = true,
                        },
                    ],
                },
            },
        };

        var result = new VerificationOptionsValidator().Validate(
            VerificationOptions.SectionName,
            options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure =>
            failure.Contains("reserved by the built-in baseline", StringComparison.Ordinal));
    }
}

public class VerificationCommandRunnerTests : IDisposable
{
    private readonly IRequestAdmissionGate _admission = new RequestAdmissionGate(TimeProvider.System);
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

    private VerificationCommandRunner Runner(
        INodeReservationGateway gateway,
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
        return new VerificationCommandRunner(options, gateway, _admission);
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
    public async Task Closed_admission_rejects_before_reservation_or_process_work()
    {
        var gateway = new FakeReservationGateway();
        var runner = Runner(gateway, TrueCommand());
        var context = Ctx(_repo);
        _admission.CloseAdmission(context.RequestId);

        var exception = await Assert.ThrowsAsync<VerificationRejectedException>(
            () => runner.RunAsync(context, "default", null, CancellationToken.None));

        Assert.Equal("admission_closed", exception.Code);
        Assert.Equal(0, gateway.ListCount);
        Assert.Empty(gateway.Acquires);
        var outcome = await _admission.ProveQuiescenceAsync(
            context.RequestId,
            new QuiescenceObservation(
                _ => Task.FromResult(0),
                _ => Task.FromResult(0),
                _ => Task.FromResult(true)),
            TimeSpan.FromSeconds(1));
        var proof = Assert.IsType<QuiescenceOutcome.Proven>(outcome).Proof;
        Assert.Equal(0, proof.ActiveOperations);
        Assert.Equal(0, proof.ActiveProcesses);
    }

    [Fact]
    public async Task Verification_operation_and_process_remain_active_through_reservation_cleanup()
    {
        var releaseStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAllowed = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new FakeReservationGateway
        {
            OnReleaseAsync = _ =>
            {
                releaseStarted.TrySetResult();
                return releaseAllowed.Task;
            },
        };
        var runner = Runner(gateway, TrueCommand());
        var context = Ctx(_repo);

        var running = runner.RunAsync(context, "default", null, CancellationToken.None);
        await releaseStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _admission.CloseAdmission(context.RequestId);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        QuiescenceOutcome draining;
        try
        {
            draining = await _admission.ProveQuiescenceAsync(
                context.RequestId,
                new QuiescenceObservation(
                    _ => Task.FromResult(1),
                    _ => Task.FromResult(0),
                    _ => Task.FromResult(true)),
                TimeSpan.FromSeconds(1),
                cancelled.Token);
        }
        finally
        {
            releaseAllowed.TrySetResult();
        }

        await running;
        var observed = Assert.IsType<QuiescenceOutcome.Uncertain>(draining).Observed;
        Assert.Equal(1, observed.ActiveOperations);
        Assert.Equal(1, observed.ActiveProcesses);
        Assert.Equal(1, observed.ActiveReservations);

        var drained = await _admission.ProveQuiescenceAsync(
            context.RequestId,
            new QuiescenceObservation(
                _ => Task.FromResult(0),
                _ => Task.FromResult(0),
                _ => Task.FromResult(true)),
            TimeSpan.FromSeconds(1));
        var proof = Assert.IsType<QuiescenceOutcome.Proven>(drained).Proof;
        Assert.Equal(0, proof.ActiveOperations);
        Assert.Equal(0, proof.ActiveProcesses);
        Assert.Equal(0, proof.ActiveReservations);
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
    public async Task Coordinator_facing_run_does_not_acquire_or_release_the_coordinator_owned_lease()
    {
        var gateway = new FakeReservationGateway();
        var coordinatorLease = gateway.GrantLease(
            "session-root",
            new ReservationScopeSpec("resource", VerificationOptions.ProjectBuildResource));
        var runner = Runner(gateway, TrueCommand());
        var context = Ctx(_repo);

        var result = await ((IAdmittedVerificationCommandRunner)runner).RunAdmittedAsync(
            context,
            "default",
            CancellationToken.None);

        Assert.True(
            result.Succeeded,
            string.Join(Environment.NewLine, result.Commands.Select(command =>
                $"{command.CommandId}: exit={command.ExitCode?.ToString() ?? "null"}; error={command.StandardError}")));
        Assert.Empty(gateway.Acquires);
        Assert.Empty(gateway.Releases);
        var activeLease = Assert.Single(
            await gateway.ListAsync(context.ProjectId, includeReleased: false, CancellationToken.None));
        Assert.Equal(coordinatorLease.LeaseId, activeLease.LeaseId);
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
    public async Task Cancellation_during_acquire_releases_the_granted_build_lease()
    {
        var gateway = new CancellationSensitiveAcquireGateway();
        var runner = Runner(gateway, new VerificationCommandOptions
        {
            Id = "sleep",
            Executable = "/bin/sleep",
            Arguments = ["30"],
            WorkingDirectory = ".",
            TimeoutSeconds = 30,
        });
        var context = Ctx(_repo);
        using var cts = new CancellationTokenSource();

        var run = runner.RunAsync(context, "default", null, cts.Token);
        await gateway.Acquired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();

        var result = await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Commands[0].Cancelled);
        Assert.Equal(gateway.AcquiredLeaseId, Assert.Single(gateway.Releases));
        Assert.Empty(await gateway.ListAsync(
            context.ProjectId,
            includeReleased: false,
            CancellationToken.None));
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
        var runner = new VerificationCommandRunner(options, gateway, _admission);

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
            Assert.True(
                ok.Succeeded,
                string.Join(Environment.NewLine, ok.Commands.Select(command =>
                    $"{command.CommandId}: exit={command.ExitCode?.ToString() ?? "null"}; error={command.StandardError}")));
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
    public async Task Repository_verification_cannot_observe_the_host_process_identity()
    {
        var hostCommandLine = await File.ReadAllTextAsync($"/proc/{Environment.ProcessId}/cmdline");
        var runner = Runner(new FakeReservationGateway(), new VerificationCommandOptions
        {
            Id = "host-process-check",
            Executable = "/usr/bin/cat",
            Arguments = [$"/proc/{Environment.ProcessId}/cmdline"],
            WorkingDirectory = ".",
            TimeoutSeconds = 5,
        });

        var result = await runner.RunAsync(Ctx(_repo), "default", null, CancellationToken.None);

        if (result.Succeeded)
        {
            Assert.NotEqual(hostCommandLine, result.Commands[0].StandardOutput);
        }
        else
        {
            Assert.NotEqual(0, result.Commands[0].ExitCode);
        }
    }

    private sealed class CancellationSensitiveAcquireGateway : INodeReservationGateway
    {
        private readonly FakeReservationGateway _inner = new();

        public TaskCompletionSource Acquired { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid AcquiredLeaseId { get; private set; }
        public IReadOnlyList<Guid> Releases => _inner.Releases;

        public async Task<ReservationOperationResult> AcquireAsync(
            Guid projectId,
            Guid requestId,
            string ownerSessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            string reason,
            CancellationToken cancellationToken)
        {
            var result = await _inner.AcquireAsync(
                projectId,
                requestId,
                ownerSessionId,
                scopes,
                reason,
                CancellationToken.None);
            AcquiredLeaseId = result.Lease!.LeaseId;
            Acquired.TrySetResult();

            if (cancellationToken.CanBeCanceled)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            return result;
        }

        public Task<ReservationOperationResult> ExpandAsync(
            Guid leaseId,
            Guid projectId,
            long fencingToken,
            string sessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            CancellationToken cancellationToken) =>
            _inner.ExpandAsync(
                leaseId,
                projectId,
                fencingToken,
                sessionId,
                scopes,
                cancellationToken);

        public Task<ReservationOperationResult> ReleaseAsync(
            Guid leaseId,
            Guid projectId,
            string sessionId,
            CancellationToken cancellationToken) =>
            _inner.ReleaseAsync(leaseId, projectId, sessionId, cancellationToken);

        public Task<ReservationOperationResult> TransferAsync(
            Guid leaseId,
            string fromSessionId,
            string toSessionId,
            CancellationToken cancellationToken) =>
            _inner.TransferAsync(leaseId, fromSessionId, toSessionId, cancellationToken);

        public Task<ReservationOperationResult> RenewAsync(
            Guid leaseId,
            long fencingToken,
            string sessionId,
            CancellationToken cancellationToken) =>
            _inner.RenewAsync(leaseId, fencingToken, sessionId, cancellationToken);

        public Task<MutationAuthorizationResult> AuthorizeAsync(
            Guid leaseId,
            long fencingToken,
            string sessionId,
            string targetPath,
            string operation,
            CancellationToken cancellationToken) =>
            _inner.AuthorizeAsync(
                leaseId,
                fencingToken,
                sessionId,
                targetPath,
                operation,
                cancellationToken);

        public Task<IReadOnlyList<ReservationLeaseInfo>> ListAsync(
            Guid projectId,
            bool includeReleased,
            CancellationToken cancellationToken) =>
            _inner.ListAsync(projectId, includeReleased, cancellationToken);

        public Task<ReservationOperationResult> MarkRecoveryRequiredAsync(
            Guid leaseId,
            string reason,
            CancellationToken cancellationToken) =>
            _inner.MarkRecoveryRequiredAsync(leaseId, reason, cancellationToken);
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
