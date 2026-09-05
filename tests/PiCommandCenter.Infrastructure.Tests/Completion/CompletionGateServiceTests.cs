using PiCommandCenter.Application.Completion;
using PiCommandCenter.Application.Projects;
using PiCommandCenter.Application.Requests;
using PiCommandCenter.Domain;
using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Reservations;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Completion;
using PiCommandCenter.Infrastructure.Persistence;
using PiCommandCenter.Infrastructure.Reservations;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure.Tests.Completion;

public class CompletionGateServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 21, 0, 0, TimeSpan.Zero);

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Empty_evidence_lists_every_mandatory_gap()
    {
        var world = await SeedAsync(happy: false);
        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId),
            world.RequestId,
            "root-session",
            new CompletionEvidence(" ", ChangedFiles: null, [], " "),
            CancellationToken.None);

        Assert.False(decision.Accepted);
        Assert.Null(decision.Result);
        Assert.Contains(CompletionRequirements.ResultSummary, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.DiffCaptured, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.PlanEvent, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.ImplementationChild, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.IndependentReviewer, decision.MissingRequirements);
        Assert.Contains(CompletionRequirements.MandatoryVerification, decision.MissingRequirements);
    }

    [Fact]
    public async Task Blocking_unresolved_finding_rejects()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with
        {
            ReviewFindings = [new ReviewFinding("f1", "leak", Blocking: true, Resolved: false, UserOverridden: false)],
        };

        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", evidence);

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.UnresolvedBlockingFinding, decision.MissingRequirements);
    }

    [Fact]
    public async Task User_overridden_blocking_finding_is_allowed()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with
        {
            ReviewFindings = [new ReviewFinding("f1", "nits", true, false, UserOverridden: true)],
        };

        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", evidence);

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
    }

    [Fact]
    public async Task Active_source_reservation_blocks_completion()
    {
        var world = await SeedAsync(happy: true, activeLease: true);
        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.ActiveReservation, decision.MissingRequirements);
    }

    [Fact]
    public async Task RecoveryRequired_lease_does_not_block_completion()
    {
        var world = await SeedAsync(happy: true, recoveryLease: true);
        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", HappyEvidence());

        Assert.True(decision.Accepted, string.Join(",", decision.MissingRequirements));
    }

    [Fact]
    public async Task Active_mutation_activity_blocks_completion()
    {
        var world = await SeedAsync(happy: true, mutating: true);
        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.ActiveMutation, decision.MissingRequirements);
    }

    [Fact]
    public async Task Unknown_changed_file_ownership_blocks()
    {
        var world = await SeedAsync(happy: true);
        var evidence = HappyEvidence() with { ChangedFiles = ["src/Unowned.cs"] };
        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", evidence);

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.OwnershipKnown, decision.MissingRequirements);
    }

    [Fact]
    public async Task Failed_mandatory_verification_blocks()
    {
        var world = await SeedAsync(happy: true, verificationPassed: false);
        var decision = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", HappyEvidence());

        Assert.False(decision.Accepted);
        Assert.Contains(CompletionRequirements.MandatoryVerification, decision.MissingRequirements);
    }

    [Fact]
    public async Task Accepted_gate_persists_result_and_survives_new_context()
    {
        var sqlite = TestRepositories.CreateSqliteFile();
        var world = await SeedAsync(happy: true, sqlitePath: sqlite);
        var evidence = HappyEvidence();
        var accepted = await world.Gate.EvaluateAsync(
            new ProjectId(world.ProjectId), world.RequestId, "root-session", evidence);

        Assert.True(accepted.Accepted, string.Join(",", accepted.MissingRequirements));
        Assert.NotNull(accepted.Result);
        Assert.Equal("Ship it", accepted.Result!.SummaryMarkdown);
        Assert.Equal(["src/Feature.cs"], accepted.Result.ChangedFiles);

        await using var restarted = TestRepositories.CreateContext(sqlite, createSchema: false);
        var gate = new CompletionGateService(new FrozenClock(), restarted, new PiCommandCenter.Application.Live.ProjectionNotifier());
        var loaded = await gate.GetResultAsync(world.RequestId);
        Assert.NotNull(loaded);
        Assert.Equal(accepted.Result.SummaryMarkdown, loaded!.SummaryMarkdown);
        Assert.Equal(accepted.Result.ChangedFiles, loaded.ChangedFiles);

        var request = restarted.WorkRequests.Single(r => r.Id == world.RequestId);
        Assert.Equal(WorkRequestStatus.Completed, request.Status);
    }

    private static CompletionEvidence HappyEvidence() => new(
        "Ship it",
        ["src/Feature.cs"],
        [],
        "dotnet-test passed");

    private static async Task<World> SeedAsync(
        bool happy,
        bool activeLease = false,
        bool recoveryLease = false,
        bool mutating = false,
        bool verificationPassed = true,
        string? sqlitePath = null)
    {
        var approvedRoot = TestRepositories.CreateTempDirectory();
        var repositoryPath = TestRepositories.InitGitRepository(approvedRoot);
        var context = TestRepositories.CreateContext(sqlitePath ?? TestRepositories.CreateSqliteFile());
        var catalog = TestRepositories.CreateCatalog(context, approvedRoot);
        var queue = TestRepositories.CreateQueue(context);
        var project = await catalog.RegisterAsync(new RegisterProjectCommand(
            DisplayName: "Fleet",
            RepositoryPath: repositoryPath,
            DefaultBranch: "main",
            Enabled: true,
            MaxActiveWriteRequests: 2,
            MaxReadOnlyRequests: 4,
            MaxChildAgentsPerRequest: 4,
            RequireCleanStart: true,
            CreateRequestBranch: true,
            CreateRequestCommit: false,
            AutoMerge: false));
        var queued = await queue.EnqueueAsync(
            new ProjectId(project.Id),
            new QueueWorkRequestCommand(
                Kind: WorkRequestKind.Development,
                Priority: RequestPriority.Normal,
                RiskLevel: RiskLevel.Standard,
                Title: "Add feature",
                Prompt: "Implement the feature"));

        var request = context.WorkRequests.Single(r => r.Id == new WorkRequestId(queued.Id));
        request.Start(Now);
        request.BeginPlanning(Now);
        request.BeginExecuting(Now);
        request.BeginReviewing(Now);
        request.BeginVerifying(Now);
        await context.SaveChangesAsync();

        if (happy)
        {
            context.SessionEvents.Add(new SessionEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                NodeId = Guid.NewGuid(),
                ProjectId = project.Id,
                RequestId = queued.Id,
                SessionId = "root-session",
                Sequence = 1,
                Type = "request.phase_changed",
                OccurredAtUtcTicks = Now.UtcTicks,
                ReceivedAtUtcTicks = Now.UtcTicks,
                PayloadJson = """{"phase":"plan"}""",
            });

            AddSession(context, project.Id, queued.Id, "root-session", parent: null, role: "root", work: AgentWorkState.Verifying, activity: AgentActivity.Idle);
            AddSession(context, project.Id, queued.Id, "impl-1", parent: "root-session", role: "implementer", work: AgentWorkState.Completed, activity: AgentActivity.Idle);
            AddSession(
                context,
                project.Id,
                queued.Id,
                "rev-1",
                parent: "root-session",
                role: "reviewer",
                work: AgentWorkState.Completed,
                activity: mutating ? AgentActivity.RunningTool : AgentActivity.Idle);

            context.VerificationRuns.Add(new VerificationRunRow
            {
                Id = Guid.NewGuid(),
                RequestId = queued.Id,
                ProfileId = "default",
                CommandId = "true",
                Status = verificationPassed
                    ? nameof(VerificationRunStatus.Passed)
                    : nameof(VerificationRunStatus.Failed),
                ExitCode = verificationPassed ? 0 : 1,
                StartedAtUtcTicks = Now.UtcTicks,
                CompletedAtUtcTicks = Now.UtcTicks,
                OutputSummary = "ok",
                Mandatory = true,
            });

            var leaseId = Guid.NewGuid();
            var state = activeLease
                ? nameof(ReservationLeaseState.Active)
                : recoveryLease
                    ? nameof(ReservationLeaseState.RecoveryRequired)
                    : nameof(ReservationLeaseState.Released);
            context.ReservationLeases.Add(new ReservationLeaseRow
            {
                Id = leaseId,
                ProjectId = project.Id,
                RequestId = queued.Id,
                OwnerSessionId = "impl-1",
                Reason = "implement",
                FencingToken = 1,
                State = state,
                AcquiredAtUtcTicks = Now.UtcTicks,
                LastRenewedAtUtcTicks = Now.UtcTicks,
                ExpiresAtUtcTicks = Now.AddMinutes(2).UtcTicks,
                ReleasedAtUtcTicks = state == nameof(ReservationLeaseState.Released) ? Now.UtcTicks : null,
                Version = 1,
            });
            context.ReservationScopes.Add(new ReservationScopeRow
            {
                Id = Guid.NewGuid(),
                LeaseId = leaseId,
                Kind = (int)ReservationScopeKind.File,
                Path = "src/Feature.cs",
            });
        }

        await context.SaveChangesAsync();
        return new World(context, new CompletionGateService(new FrozenClock(), context, new PiCommandCenter.Application.Live.ProjectionNotifier()), project.Id, new WorkRequestId(queued.Id));
    }

    private static void AddSession(
        ControlPlaneDbContext db,
        Guid projectId,
        Guid requestId,
        string id,
        string? parent,
        string role,
        AgentWorkState work,
        AgentActivity activity)
    {
        db.AgentSessions.Add(new AgentSessionRow
        {
            Id = id,
            ProjectId = projectId,
            RequestId = requestId,
            ParentSessionId = parent,
            AgentName = id,
            Role = role,
            Runtime = "pi",
            Model = "codex/default",
            Liveness = nameof(AgentLiveness.Online),
            Activity = activity.ToString(),
            Attention = nameof(AgentAttention.None),
            WorkState = work.ToString(),
            StatusReason = "seed",
            StartedAtUtcTicks = Now.UtcTicks,
            LastSequence = 1,
            Version = 1,
        });
    }

    private sealed record World(
        ControlPlaneDbContext Db,
        CompletionGateService Gate,
        Guid ProjectId,
        WorkRequestId RequestId);
}
