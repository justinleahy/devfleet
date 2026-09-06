using Microsoft.Extensions.Options;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Quiescence;
using PiCommandCenter.Node.Verification;

namespace PiCommandCenter.Node.Tests;

public sealed class RequestVerificationCoordinatorTests
{
    private const int MaxSummaryLength = 2048;
    private const string SensitivePath =
        "/home/verification-user/.config/provider/credentials.json";
    private static readonly string SensitiveToken = "sk-ant-" + new string('s', 64);

    [Fact]
    public async Task Empty_profiles_still_run_the_baseline_verification()
    {
        var scenario = new Scenario();

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Passed, decision.Kind);
        Assert.Equal(1, scenario.Baseline.RunCount);
        Assert.Equal(0, scenario.Profiles.RunCount);
        Assert.Equal(
            [VerificationRunKind.Baseline, VerificationRunKind.Baseline],
            scenario.PersistedRuns.Select(run => run.RunKind));
        Assert.Equal(
            [
                "verification.started",
                "verification.command.started",
                "verification.command.started",
                "verification.completed",
            ],
            scenario.Events.Select(item => item.Type));
    }

    [Fact]
    public async Task Active_source_mutation_is_rejected_without_a_started_event()
    {
        var scenario = new Scenario();
        scenario.Reservations.GrantLease(
            "active-writer",
            new ReservationScopeSpec("file", "src/PendingChange.cs"));

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Rejected, decision.Kind);
        Assert.Equal("active_source_mutation", decision.ErrorCode);
        Assert.Equal(["verification.rejected"], scenario.Events.Select(item => item.Type));
        Assert.Empty(scenario.Reservations.Acquires);
        Assert.Empty(scenario.PersistedRuns);
        Assert.Equal(0, scenario.Baseline.CaptureCount);
        Assert.Equal(0, scenario.Baseline.RunCount);
    }

    [Fact]
    public async Task An_admitted_failed_run_emits_one_started_and_one_terminal_event()
    {
        var scenario = new Scenario();
        scenario.Baseline.RepositoryIntegrityExitCode = 1;

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Failed, decision.Kind);
        Assert.Single(scenario.Events, item => item.Type == "verification.started");
        Assert.Single(scenario.Events, item => item.Type == "verification.failed");
        Assert.DoesNotContain(scenario.Events, item => item.Type == "verification.completed");
    }

    [Fact]
    public async Task Command_started_events_are_bounded_facts_before_each_baseline_command()
    {
        var scenario = new Scenario();

        _ = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        var progress = scenario.Events
            .Where(item => item.Type == "verification.command.started")
            .ToArray();
        Assert.Equal(2, progress.Length);
        Assert.Equal("verification.started", scenario.Events[0].Type);
        Assert.Equal("repository-integrity", progress[0].Payload["commandId"]);
        Assert.Equal(true, progress[0].Payload["mandatory"]);
        Assert.Equal("Baseline", progress[0].Payload["runKind"]);
        Assert.Equal(scenario.Baseline.Fingerprint, progress[0].Payload["fingerprint"]);
        Assert.Equal("baseline-policy-r1", progress[0].Payload["policyRevision"]);
        Assert.Equal(900, progress[0].Payload["timeoutSeconds"]);
        Assert.NotNull(progress[0].Payload["startedAt"]);
        Assert.NotNull(progress[0].Payload["eventTime"]);
        Assert.False(progress[0].Payload.ContainsKey("executable"));
        Assert.False(progress[0].Payload.ContainsKey("arguments"));
        Assert.Equal("whitespace", progress[1].Payload["commandId"]);
        Assert.Equal(false, progress[1].Payload["mandatory"]);
        Assert.Equal("verification.completed", scenario.Events[^1].Type);
    }

    [Fact]
    public async Task Oversized_precondition_exception_is_bounded_and_sanitized()
    {
        var scenario = new Scenario();
        scenario.Baseline.CaptureException = new InvalidOperationException(OversizedDiagnostic());

        var captureError = await Assert.ThrowsAsync<VerificationRejectedException>(
            () => scenario.Coordinator.CaptureFingerprintAsync(
                scenario.CreateContext(),
                CancellationToken.None));
        Assert.Equal("verification_precondition_failed", captureError.Code);
        AssertSafeSummary(captureError.Message);
        Assert.EndsWith("…", captureError.Message, StringComparison.Ordinal);

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Rejected, decision.Kind);
        Assert.Equal("verification_precondition_failed", decision.ErrorCode);
        AssertSafeSummary(decision.Summary);
        Assert.EndsWith("…", decision.Summary, StringComparison.Ordinal);
        var rejected = Assert.Single(scenario.Events);
        Assert.Equal("verification.rejected", rejected.Type);
        Assert.Equal(decision.Summary, Assert.IsType<string>(rejected.Payload["summary"]));
        Assert.Equal(
            "verification_precondition_failed",
            Assert.IsType<string>(rejected.Payload["errorCode"]));
    }

    [Fact]
    public async Task Oversized_admitted_failure_exception_is_bounded_and_sanitized()
    {
        var scenario = new Scenario();
        scenario.Baseline.RunException = new InvalidOperationException(OversizedDiagnostic());

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Failed, decision.Kind);
        Assert.Equal("verification_failed", decision.ErrorCode);
        AssertSafeSummary(decision.Summary);
        Assert.EndsWith("…", decision.Summary, StringComparison.Ordinal);
        var failed = Assert.Single(scenario.Events, item => item.Type == "verification.failed");
        Assert.Equal(decision.Summary, Assert.IsType<string>(failed.Payload["summary"]));
        Assert.Equal("verification_failed", Assert.IsType<string>(failed.Payload["errorCode"]));
    }

    [Fact]
    public async Task Oversized_cancelled_command_output_is_bounded_and_sanitized()
    {
        var scenario = new Scenario();
        scenario.Baseline.RepositoryIntegrityResult = Command(
            IBaselineVerification.RepositoryIntegrityCommandId,
            exitCode: 1,
            mandatory: true,
            standardOutput: string.Empty,
            standardError: OversizedDiagnostic(),
            cancelled: true);

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Cancelled, decision.Kind);
        Assert.Equal("verification_cancelled", decision.ErrorCode);
        AssertSafeSummary(decision.Summary);
        var cancelled = Assert.Single(scenario.Events, item => item.Type == "verification.cancelled");
        Assert.Equal(decision.Summary, Assert.IsType<string>(cancelled.Payload["summary"]));
        Assert.Equal(
            "verification_cancelled",
            Assert.IsType<string>(cancelled.Payload["errorCode"]));

        var run = Assert.Single(
            scenario.PersistedRuns,
            item => item.CommandId == IBaselineVerification.RepositoryIntegrityCommandId);
        var outputSummary = Assert.IsType<string>(run.OutputSummary);
        AssertSafeSummary(outputSummary);
        Assert.EndsWith("…", outputSummary, StringComparison.Ordinal);
        Assert.Contains("[redacted", outputSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persisted_green_fingerprint_is_reused_without_duplicate_persistence()
    {
        var scenario = new Scenario();
        var existing = scenario.CreateExistingRun(
            "content-fingerprint-a",
            VerificationRunStatus.Passed);

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext([existing]),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Reused, decision.Kind);
        Assert.Empty(scenario.PersistedRuns);
        Assert.Empty(scenario.Events);
        Assert.Equal(1, scenario.Baseline.CaptureCount);
        Assert.Equal(0, scenario.Baseline.RunCount);
    }

    [Fact]
    public async Task Persisted_failure_suppresses_a_rerun_for_the_unchanged_fingerprint()
    {
        var scenario = new Scenario();
        var existing = scenario.CreateExistingRun(
            "content-fingerprint-a",
            VerificationRunStatus.Failed);

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext([existing]),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Failed, decision.Kind);
        Assert.Equal("verification_failed", decision.ErrorCode);
        Assert.Empty(scenario.PersistedRuns);
        Assert.Empty(scenario.Events);
        Assert.Equal(0, scenario.Baseline.RunCount);
    }

    [Fact]
    public async Task Changed_content_runs_again_after_a_persisted_failure()
    {
        var scenario = new Scenario();
        var existing = scenario.CreateExistingRun(
            "content-fingerprint-before-repair",
            VerificationRunStatus.Failed);
        scenario.Baseline.Fingerprint = "content-fingerprint-after-repair";

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext([existing]),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Passed, decision.Kind);
        Assert.Equal("content-fingerprint-after-repair", decision.Fingerprint);
        Assert.Equal(1, scenario.Baseline.RunCount);
        Assert.Equal(2, scenario.PersistedRuns.Count);
        Assert.Equal(
            [
                "verification.started",
                "verification.command.started",
                "verification.command.started",
                "verification.completed",
            ],
            scenario.Events.Select(item => item.Type));
    }

    [Fact]
    public async Task Configured_final_verification_runs_baseline_before_the_project_profile()
    {
        var scenario = new Scenario(configureProfile: true);

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(policy: scenario.ProfilePolicy),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Passed, decision.Kind);
        Assert.Equal(["baseline", "profile"], scenario.ExecutionOrder);
        Assert.Equal(
            [
                VerificationRunKind.Baseline,
                VerificationRunKind.Baseline,
                VerificationRunKind.ProjectCheck,
            ],
            scenario.PersistedRuns.Select(run => run.RunKind));
        Assert.Single(scenario.PersistedRuns.Select(run => run.AttemptId).Distinct());
        Assert.DoesNotContain(scenario.PersistedRuns, run => run.AttemptId == Guid.Empty);
    }

    [Fact]
    public async Task Intermediate_checks_are_inert_to_final_lifecycle_and_baseline_execution()
    {
        var scenario = new Scenario(configureProfile: true);

        var decision = await scenario.Coordinator.VerifyIntermediateAsync(
            scenario.CreateContext(policy: scenario.ProfilePolicy),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Passed, decision.Kind);
        Assert.Equal(1, scenario.Baseline.CaptureCount);
        Assert.Equal(0, scenario.Baseline.RunCount);
        Assert.Equal(1, scenario.Profiles.RunCount);
        Assert.Equal(
            ["verification.command.started", "verification.intermediate"],
            scenario.Events.Select(item => item.Type));
        var run = Assert.Single(scenario.PersistedRuns);
        Assert.Equal(VerificationRunKind.Intermediate, run.RunKind);
        Assert.NotEqual(Guid.Empty, run.AttemptId);
        Assert.Equal("content-fingerprint-a", run.Fingerprint);
        Assert.Equal(scenario.ProfilePolicy.Revision, run.PolicyRevision);
    }

    [Fact]
    public async Task Unavailable_captured_profile_fails_and_blocks_instead_of_rejecting()
    {
        var scenario = new Scenario(configureProfile: true);
        var stale = scenario.ProfilePolicy with { TrustedProfileRevision = "stale-revision" };

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(policy: stale),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Failed, decision.Kind);
        Assert.Equal("verification_policy_unavailable", decision.ErrorCode);
        Assert.Equal(stale.Revision, decision.PolicyRevision);
        var failed = Assert.Single(scenario.Events);
        Assert.Equal("verification.failed", failed.Type);
        Assert.Equal(stale.Revision, failed.Payload["policyRevision"]);
        Assert.Null(failed.Payload["fingerprint"]);
        Assert.Empty(scenario.ExecutionOrder);
    }

    [Fact]
    public async Task Unavailable_profile_during_intermediate_check_remains_inert()
    {
        var scenario = new Scenario(configureProfile: true);
        var stale = scenario.ProfilePolicy with { TrustedProfileRevision = "stale-revision" };

        var decision = await scenario.Coordinator.VerifyIntermediateAsync(
            scenario.CreateContext(policy: stale),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Rejected, decision.Kind);
        Assert.Equal("verification_policy_unavailable", decision.ErrorCode);
        Assert.Equal("verification.intermediate", Assert.Single(scenario.Events).Type);
        Assert.Empty(scenario.ExecutionOrder);
    }

    [Fact]
    public async Task Incomplete_assignment_snapshot_is_rejected_before_running()
    {
        var scenario = new Scenario(configureProfile: true);
        var context = scenario.CreateContext(policy: scenario.ProfilePolicy) with
        {
            WorkspaceBindingId = null,
        };

        var decision = await scenario.Coordinator.VerifyFinalAsync(context, CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Rejected, decision.Kind);
        Assert.Equal("verification_policy_unavailable", decision.ErrorCode);
        Assert.Equal("verification.rejected", Assert.Single(scenario.Events).Type);
        Assert.Empty(scenario.ExecutionOrder);
    }

    [Fact]
    public async Task Reverse_mandatory_command_order_with_the_same_set_passes()
    {
        var options = new VerificationOptions();
        options.Profiles.Add(
            "quality",
            new VerificationProfileOptions
            {
                Id = "quality",
                Revision = "quality-profile-r3",
                Commands =
                [
                    new VerificationCommandOptions
                    {
                        Id = "lint",
                        Executable = "dotnet",
                        Arguments = ["format", "--verify-no-changes"],
                        WorkingDirectory = ".",
                        TimeoutSeconds = 60,
                        Mandatory = true,
                    },
                    new VerificationCommandOptions
                    {
                        Id = "unit-tests",
                        Executable = "dotnet",
                        Arguments = ["test", "--no-build"],
                        WorkingDirectory = ".",
                        TimeoutSeconds = 120,
                        Mandatory = true,
                    },
                ],
            });
        var scenario = new Scenario(options);
        var policy = new RequestVerificationPolicy(
            "quality-policy-r4",
            IBaselineVerification.Version,
            "quality",
            "quality-profile-r3",
            ["unit-tests", IBaselineVerification.RepositoryIntegrityCommandId, "lint"]);

        var decision = await scenario.Coordinator.VerifyFinalAsync(
            scenario.CreateContext(policy: policy),
            CancellationToken.None);

        Assert.Equal(RequestVerificationDecisionKind.Passed, decision.Kind);
        Assert.Equal(["baseline", "profile"], scenario.ExecutionOrder);
    }


    [Fact]
    public async Task Caller_cancellation_during_lease_acquisition_still_releases_the_granted_lease()
    {
        var gateway = new DeferredAcquireReservationGateway();
        var scenario = new Scenario(reservations: gateway);
        using var cts = new CancellationTokenSource();

        var verify = scenario.Coordinator.VerifyFinalAsync(scenario.CreateContext(), cts.Token);
        await gateway.AcquireStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        gateway.CompleteAcquire();

        var decision = await verify.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(RequestVerificationDecisionKind.Cancelled, decision.Kind);
        Assert.Equal("verification_cancelled", decision.ErrorCode);
        Assert.Equal(gateway.AcquiredLeaseId, Assert.Single(gateway.Releases));
        Assert.Equal(0, scenario.Baseline.RunCount);
        Assert.Equal(0, scenario.Baseline.CaptureCount);
        Assert.Empty(scenario.PersistedRuns);
    }


    private sealed class Scenario
    {
        private static readonly DateTimeOffset RecordedAt =
            new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

        public Scenario(bool configureProfile = false, INodeReservationGateway? reservations = null)
            : this(CreateOptions(configureProfile), reservations)
        {
        }

        public Scenario(VerificationOptions options, INodeReservationGateway? reservations = null)
        {
            Baseline = new RecordingBaselineVerification(ExecutionOrder);
            Profiles = new RecordingProfileRunner(ExecutionOrder);
            Reservations = new FakeReservationGateway();
            Coordinator = new RequestVerificationCoordinator(
                Options.Create(options),
                Baseline,
                Profiles,
                reservations ?? Reservations,
                new RequestAdmissionGate(TimeProvider.System),
                TimeProvider.System);
        }

        private static VerificationOptions CreateOptions(bool configureProfile)
        {
            var options = new VerificationOptions();
            if (!configureProfile)
            {
                return options;
            }

            options.Profiles.Add(
                "quality",
                new VerificationProfileOptions
                {
                    Id = "quality",
                    Revision = "quality-profile-r3",
                    Commands =
                    [
                        new VerificationCommandOptions
                        {
                            Id = "unit-tests",
                            Executable = "dotnet",
                            Arguments = ["test", "--no-build"],
                            WorkingDirectory = ".",
                            TimeoutSeconds = 120,
                            Mandatory = true,
                        },
                    ],
                });
            return options;
        }

        public Guid ProjectId { get; } = Guid.NewGuid();
        public Guid RequestId { get; } = Guid.NewGuid();
        public Guid WorkspaceBindingId { get; } = Guid.NewGuid();
        public FakeReservationGateway Reservations { get; } = new();
        public RecordingBaselineVerification Baseline { get; }
        public RecordingProfileRunner Profiles { get; }
        public RequestVerificationCoordinator Coordinator { get; }
        public List<string> ExecutionOrder { get; } = [];
        public List<(string Type, IReadOnlyDictionary<string, object?> Payload)> Events { get; } = [];
        public List<VerificationRunDto> PersistedRuns { get; } = [];

        public RequestVerificationPolicy BaselinePolicy { get; } = new(
            "baseline-policy-r1",
            IBaselineVerification.Version,
            TrustedProfileId: null,
            TrustedProfileRevision: null,
            MandatoryCommandIds: [IBaselineVerification.RepositoryIntegrityCommandId]);

        public RequestVerificationPolicy ProfilePolicy { get; } = new(
            "quality-policy-r4",
            IBaselineVerification.Version,
            "quality",
            "quality-profile-r3",
            [IBaselineVerification.RepositoryIntegrityCommandId, "unit-tests"]);

        public RequestVerificationContext CreateContext(
            IReadOnlyList<VerificationRunDto>? existingRuns = null,
            RequestVerificationPolicy? policy = null)
            => new(
                ProjectId,
                RequestId,
                WorkspaceBindingId,
                BindingValidationRevision: 23,
                RequestingSessionId: "root-session-1",
                RepositoryRoot: "/workspace/devfleet",
                BaselineCommit: "baseline-commit-a1b2c3",
                BaselineBranch: "main",
                Policy: policy ?? BaselinePolicy,
                ExistingRuns: existingRuns ?? [],
                EmitAsync: (type, payload, _) =>
                {
                    Events.Add((type, payload));
                    return Task.CompletedTask;
                },
                PersistRunAsync: (run, _) =>
                {
                    PersistedRuns.Add(run);
                    return Task.CompletedTask;
                });

        public VerificationRunDto CreateExistingRun(
            string fingerprint,
            VerificationRunStatus status)
            => new(
                Guid.NewGuid(),
                RequestId,
                IBaselineVerification.ProfileId,
                IBaselineVerification.RepositoryIntegrityCommandId,
                status,
                status == VerificationRunStatus.Passed ? 0 : 1,
                RecordedAt - TimeSpan.FromSeconds(1),
                RecordedAt,
                status == VerificationRunStatus.Passed
                    ? "repository integrity passed"
                    : "repository integrity failed",
                OutputArtifactPath: null,
                Mandatory: true,
                Fingerprint: fingerprint,
                PolicyRevision: BaselinePolicy.Revision,
                RunKind: VerificationRunKind.Baseline,
                AttemptId: Guid.NewGuid());
    }

    private sealed class RecordingBaselineVerification(List<string> executionOrder)
        : IBaselineVerification
    {
        public string Fingerprint { get; set; } = "content-fingerprint-a";
        public int RepositoryIntegrityExitCode { get; set; }
        public Exception? CaptureException { get; set; }
        public Exception? RunException { get; set; }
        public VerificationCommandResult? RepositoryIntegrityResult { get; set; }
        public int CaptureCount { get; private set; }
        public int RunCount { get; private set; }

        public Task<string> CaptureFingerprintAsync(
            BaselineVerificationContext context,
            CancellationToken cancellationToken)
        {
            CaptureCount++;
            if (CaptureException is not null)
            {
                throw CaptureException;
            }

            return Task.FromResult(Fingerprint);
        }

        public async Task<BaselineVerificationResult> RunAsync(
            BaselineVerificationContext context,
            string fingerprint,
            CancellationToken cancellationToken)
        {
            RunCount++;
            executionOrder.Add("baseline");
            if (RunException is not null)
            {
                throw RunException;
            }

            if (context.OnCommandStarting is not null)
            {
                await context.OnCommandStarting(
                    new VerificationCommandStarting(
                        IBaselineVerification.RepositoryIntegrityCommandId,
                        Mandatory: true,
                        TimeoutSeconds: 900),
                    cancellationToken).ConfigureAwait(false);
                await context.OnCommandStarting(
                    new VerificationCommandStarting(
                        IBaselineVerification.WhitespaceCommandId,
                        Mandatory: false,
                        TimeoutSeconds: 900),
                    cancellationToken).ConfigureAwait(false);
            }

            var repositoryIntegrity = RepositoryIntegrityResult ?? Command(
                IBaselineVerification.RepositoryIntegrityCommandId,
                RepositoryIntegrityExitCode,
                mandatory: true);
            var whitespace = Command(
                IBaselineVerification.WhitespaceCommandId,
                exitCode: 0,
                mandatory: false);
            return new BaselineVerificationResult(
                fingerprint,
                repositoryIntegrity,
                whitespace);
        }
    }

    private sealed class RecordingProfileRunner(List<string> executionOrder)
        : IAdmittedVerificationCommandRunner
    {
        public int RunCount { get; private set; }

        public async Task<VerificationProfileRunResult> RunAdmittedAsync(
            VerificationRunContext context,
            string profileId,
            CancellationToken cancellationToken)
        {
            RunCount++;
            executionOrder.Add("profile");
            if (context.OnCommandStarting is not null)
            {
                await context.OnCommandStarting(
                    new VerificationCommandStarting("unit-tests", Mandatory: true, TimeoutSeconds: 120),
                    cancellationToken).ConfigureAwait(false);
            }

            return new VerificationProfileRunResult(
                profileId,
                [Command("unit-tests", exitCode: 0, mandatory: true)],
                Succeeded: true);
        }
    }

    private sealed class DeferredAcquireReservationGateway : INodeReservationGateway
    {
        private readonly FakeReservationGateway _inner = new();
        private readonly TaskCompletionSource _continueAcquire = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AcquireStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid AcquiredLeaseId { get; private set; }
        public IReadOnlyList<Guid> Releases => _inner.Releases;

        public void CompleteAcquire() => _continueAcquire.TrySetResult();

        public async Task<ReservationOperationResult> AcquireAsync(
            Guid projectId,
            Guid requestId,
            string ownerSessionId,
            IReadOnlyList<ReservationScopeSpec> scopes,
            string reason,
            CancellationToken cancellationToken)
        {
            AcquireStarted.TrySetResult();
            await _continueAcquire.Task.ConfigureAwait(false);
            var result = await _inner.AcquireAsync(
                projectId,
                requestId,
                ownerSessionId,
                scopes,
                reason,
                cancellationToken).ConfigureAwait(false);
            AcquiredLeaseId = result.Lease!.LeaseId;
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
                leaseId, projectId, fencingToken, sessionId, scopes, cancellationToken);

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
                leaseId, fencingToken, sessionId, targetPath, operation, cancellationToken);

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


    private static VerificationCommandResult Command(
        string commandId,
        int exitCode,
        bool mandatory,
        string? standardOutput = null,
        string? standardError = null,
        bool cancelled = false)
        => new(
            commandId,
            commandId == "unit-tests" ? "dotnet" : "git",
            commandId == "unit-tests" ? ["test", "--no-build"] : ["status", "--porcelain"],
            ".",
            exitCode,
            TimeSpan.FromMilliseconds(25),
            standardOutput ?? (exitCode == 0 ? "passed" : string.Empty),
            standardError ?? (exitCode == 0 ? string.Empty : "failed"),
            TimedOut: false,
            Cancelled: cancelled,
            Crashed: false,
            OutputTruncated: false,
            ArtifactPath: null,
            Mandatory: mandatory);

    private static string OversizedDiagnostic() =>
        $"{SensitiveToken} failed at {SensitivePath}: {new string('x', MaxSummaryLength * 2)}";

    private static void AssertSafeSummary(string summary)
    {
        Assert.InRange(summary.Length, 1, MaxSummaryLength);
        Assert.DoesNotContain(SensitiveToken, summary, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitivePath, summary, StringComparison.Ordinal);
    }
}
