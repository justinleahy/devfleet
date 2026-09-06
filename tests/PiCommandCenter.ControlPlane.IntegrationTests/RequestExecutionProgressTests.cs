using PiCommandCenter.Application.Requests;
using PiCommandCenter.Application.Reservations;
using PiCommandCenter.Application.Sessions;
using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Sessions;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Web.Components.Requests;

namespace PiCommandCenter.ControlPlane.IntegrationTests;

/// <summary>
/// Covers the read helpers behind the request page: the "Ongoing progress" reducer, which must
/// read a live request's durable facts (including Pi's nested payload shape), freeze a terminal
/// request's duration at the last durable timestamp, keep only the newest few facts in
/// newest-first order, and never surface model-authored response or thinking text; the
/// verification view, which must keep baseline, project, and intermediate runs apart and treat a
/// superseded fingerprint as history; and the attention scanner's verification rule.
/// </summary>
public class RequestExecutionProgressTests
{
    private static readonly DateTimeOffset Queued = new(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_live_request_reports_phase_elapsed_tools_and_agent_operations()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(1));
        var sessions = new[]
        {
            Session(
                "sess-root",
                AgentActivity.RunningTool,
                startedAt: Queued.AddSeconds(30),
                currentOperation: "read_file",
                statusReason: "Running read_file"),
        };
        var events = new[]
        {
            Event(1, "session.registered", Queued.AddSeconds(30), """{"role":"implementer"}"""),
            Event(2, "request.phase_changed", Queued.AddSeconds(40), """{"phase":"execute"}"""),
            // Pi wraps every normalized body as { seq, timestamp, data }.
            Event(3, "tool.started", Queued.AddSeconds(50), """{"seq":3,"data":{"toolName":"read_file"}}"""),
            Event(4, "tool.progress", Queued.AddSeconds(70), """{"seq":4,"data":{"toolName":"read_file"}}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            sessions,
            events,
            Queued.AddSeconds(100));

        Assert.True(progress.IsRunning);
        Assert.False(progress.IsTerminal);
        Assert.Equal("execute", progress.Phase);
        Assert.Equal("Executing", progress.Status);
        // Anchored at the first session start, running to the observation time.
        Assert.Equal(TimeSpan.FromSeconds(70), progress.Elapsed);
        Assert.Equal(Queued.AddSeconds(70), progress.LastActivityAt);
        Assert.Equal(4, progress.EventCount);
        Assert.Equal(1, progress.ToolCallCount);

        var operation = Assert.Single(progress.Operations);
        Assert.Equal("sess-root", operation.SessionId);
        Assert.Equal(AgentActivity.RunningTool, operation.Activity);
        Assert.Equal("read_file", operation.Operation);

        // Newest first, and the nested tool name is read out of Pi's `data` object.
        Assert.Equal("read_file reported progress", progress.Facts[0].Label);
        Assert.Equal("Started read_file", progress.Facts[1].Label);
        Assert.Equal("Phase changed to execute", progress.Facts[2].Label);
        Assert.Equal("Agent session registered \u2014 implementer", progress.Facts[3].Label);
    }

    [Fact]
    public void A_terminal_request_freezes_its_duration_at_the_last_durable_fact()
    {
        var completedAt = Queued.AddMinutes(5);
        var request = Request(WorkRequestStatus.Completed, updatedAt: completedAt);
        var sessions = new[]
        {
            Session(
                "sess-root",
                AgentActivity.Idle,
                startedAt: Queued.AddMinutes(1),
                currentOperation: null,
                statusReason: "Session completed",
                liveness: AgentLiveness.Exited,
                workState: AgentWorkState.Completed,
                endedAt: completedAt),
        };
        var events = new[]
        {
            Event(1, "request.completed", completedAt, "{}"),
        };

        var soon = RequestExecutionProgressReader.Read(request, sessions, events, completedAt.AddSeconds(5));
        var muchLater = RequestExecutionProgressReader.Read(request, sessions, events, completedAt.AddDays(3));

        Assert.False(soon.IsRunning);
        Assert.True(soon.IsTerminal);
        Assert.Equal(TimeSpan.FromMinutes(4), soon.Elapsed);
        Assert.Equal(soon.Elapsed, muchLater.Elapsed);
        Assert.Equal("4m 00s", soon.ElapsedText);
        // A terminal request retains its final state and reports no live operation.
        Assert.Empty(soon.Operations);
        Assert.Equal("Request completed", Assert.Single(soon.Facts).Label);
    }

    [Fact]
    public void Only_the_newest_five_narratable_facts_survive_in_newest_first_order()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(2));
        var events = new List<SessionEventDto>
        {
            Event(1, "request.claimed", Queued.AddSeconds(1), "{}"),
            Event(2, "session.heartbeat", Queued.AddSeconds(2), "{}"),
            Event(3, "turn.started", Queued.AddSeconds(3), "{}"),
            Event(4, "tool.started", Queued.AddSeconds(4), """{"tool":"grep"}"""),
            Event(5, "tool.completed", Queued.AddSeconds(5), """{"tool":"grep"}"""),
            Event(6, "child.started", Queued.AddSeconds(6), """{"role":"reviewer"}"""),
            Event(7, "verification.failed", Queued.AddSeconds(7), """{"profileId":"build"}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(10));

        Assert.Equal(RequestExecutionProgressReader.FactCap, progress.Facts.Count);
        Assert.Equal(
            new[]
            {
                "Verification failed \u2014 build",
                "Child agent started \u2014 reviewer",
                "grep completed",
                "Started grep",
                "Turn started",
            },
            progress.Facts.Select(fact => fact.Label).ToArray());
        // Heartbeats are not narrated, but every persisted event is still counted.
        Assert.Equal(7, progress.EventCount);
        // No session yet: elapsed is measured from the moment the request was queued.
        Assert.Equal(TimeSpan.FromSeconds(10), progress.Elapsed);
        Assert.Equal(Queued, progress.ElapsedSince);
    }

    [Fact]
    public void Response_and_thinking_text_never_reaches_the_progress_facts()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(1));
        var events = new[]
        {
            Event(
                1,
                "message.delta",
                Queued.AddSeconds(10),
                """{"seq":1,"data":{"textDelta":"secret answer","thinkingDelta":"private chain"}}"""),
            Event(
                2,
                "message.started",
                Queued.AddSeconds(11),
                """{"seq":2,"data":{"text":"secret answer","message":"private chain"}}"""),
            Event(3, "message.completed", Queued.AddSeconds(12), """{"data":{"text":"secret answer"}}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(20));

        Assert.Equal(
            new[] { "Agent finished a response", "Agent began composing a response" },
            progress.Facts.Select(fact => fact.Label).ToArray());
        foreach (var fact in progress.Facts)
        {
            Assert.DoesNotContain("secret answer", fact.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private chain", fact.Label, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("{", fact.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Malformed_unicode_payload_scalars_and_names_are_treated_as_absent()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(1));
        var events = new[]
        {
            Event(1, "request.phase_changed", Queued.AddSeconds(10), """{"phase":"\uD800"}"""),
            Event(
                2,
                "tool.started",
                Queued.AddSeconds(11),
                """{"\uD800":"leaked","tool":"build"}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(20));

        Assert.Equal("Executing", progress.Phase);
        Assert.Contains(progress.Facts, fact => fact.Label.Contains("build", StringComparison.Ordinal));
        foreach (var fact in progress.Facts)
        {
            Assert.DoesNotContain("\uD800", fact.Label, StringComparison.Ordinal);
            Assert.DoesNotContain("leaked", fact.Label, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_verification_event_is_narrated_and_none_of_them_moves_the_phase()
    {
        var request = Request(WorkRequestStatus.Executing, updatedAt: Queued.AddMinutes(2));
        var events = new[]
        {
            Event(1, "request.phase_changed", Queued.AddSeconds(1), """{"phase":"Implementing"}"""),
            Event(
                2,
                "verification.rejected",
                Queued.AddSeconds(2),
                """{"data":{"summary":"An active source mutation blocks verification.","errorCode":"active_source_mutation"}}"""),
            Event(
                3,
                "verification.intermediate",
                Queued.AddSeconds(3),
                """{"summary":"Project checks passed.","decision":"Passed"}"""),
            Event(4, "verification.cancelled", Queued.AddSeconds(4), """{"summary":"Verification was cancelled."}"""),
        };

        var progress = RequestExecutionProgressReader.Read(
            request,
            Array.Empty<AgentSessionDto>(),
            events,
            Queued.AddSeconds(10));

        // A rejected precondition and an intermediate check are narrated, but the phase stays the
        // one the control plane last recorded.
        Assert.Equal("Implementing", progress.Phase);
        Assert.Equal(
            new[]
            {
                "Verification cancelled \u2014 Verification was cancelled.",
                "Intermediate project checks ran \u2014 Project checks passed.",
                "Verification did not start; the phase is unchanged \u2014 An active source mutation blocks verification.",
                "Phase changed to Implementing",
            },
            progress.Facts.Select(fact => fact.Label).ToArray());
    }

    [Fact]
    public void Run_kinds_separate_and_a_superseded_green_attempt_reads_as_history()
    {
        var runs = new[]
        {
            Run("repository-integrity", VerificationRunStatus.Passed, mandatory: true, fingerprint: "sha256:old"),
            Run("dotnet-test", VerificationRunStatus.Passed, mandatory: true, fingerprint: "sha256:old", kind: VerificationRunKind.ProjectCheck),
            Run("dotnet-test", VerificationRunStatus.Failed, mandatory: true, kind: VerificationRunKind.Intermediate, startedAt: Queued.AddMinutes(2)),
            Run("repository-integrity", VerificationRunStatus.Running, mandatory: true, startedAt: Queued.AddMinutes(3)),
        };

        var view = RequestVerificationViewReader.Read(runs, Array.Empty<AgentSessionDto>());

        Assert.Equal("sha256:new", view.Fingerprint);
        Assert.Equal(["repository-integrity"], view.Baseline.Select(row => row.Run.CommandId));
        Assert.Empty(view.ProjectChecks);
        // The old attempt was all green; it is history, so it produces no success sentence.
        Assert.Null(view.BaselineSuccess);
        Assert.Null(view.ProjectChecksSuccess);
        Assert.Equal(2, view.History.Count);
        Assert.DoesNotContain(view.History, row => row.IsCurrent);
        // The intermediate failure is neither a current row nor history of the final policy.
        var intermediate = Assert.Single(view.Intermediate);
        Assert.Equal(VerificationRunKind.Intermediate, intermediate.Run.RunKind);
        Assert.False(intermediate.IsBlocking);
        Assert.False(view.HasBlockingFailure);
        Assert.Equal("repository-integrity", view.Running?.Run.CommandId);
    }

    [Fact]
    public void Success_copy_names_the_baseline_and_the_passed_project_commands()
    {
        var runs = new[]
        {
            Run("repository-integrity", VerificationRunStatus.Passed, mandatory: true),
            Run("whitespace", VerificationRunStatus.Failed, mandatory: false),
            Run("dotnet-test", VerificationRunStatus.Passed, mandatory: true, kind: VerificationRunKind.ProjectCheck),
            Run("runtime-test", VerificationRunStatus.Passed, mandatory: true, kind: VerificationRunKind.ProjectCheck),
        };

        var view = RequestVerificationViewReader.Read(runs, Array.Empty<AgentSessionDto>());

        Assert.Equal("Baseline checks passed.", view.BaselineSuccess);
        Assert.Equal("Project checks passed: dotnet-test, runtime-test.", view.ProjectChecksSuccess);
        // The optional whitespace failure warns; it never blocks and never reads as failed.
        Assert.Equal(1, view.WarningCount);
        Assert.False(view.HasBlockingFailure);
        Assert.True(view.Baseline.Single(row => row.Run.CommandId == "whitespace").IsWarning);
        Assert.False(view.Baseline.Single(row => row.Run.CommandId == "whitespace").IsBlocking);
    }

    [Fact]
    public void A_mandatory_final_failure_is_the_only_verification_run_that_needs_attention()
    {
        var request = Request(WorkRequestStatus.Blocked, updatedAt: Queued.AddMinutes(5));
        var runs = new[]
        {
            Run("whitespace", VerificationRunStatus.Failed, mandatory: false),
            Run("dotnet-test", VerificationRunStatus.Failed, mandatory: true, kind: VerificationRunKind.Intermediate),
            Run("legacy-check", VerificationRunStatus.TimedOut, mandatory: true, fingerprint: "sha256:old", kind: VerificationRunKind.ProjectCheck),
            Run("repository-integrity", VerificationRunStatus.TimedOut, mandatory: true),
        };

        var signals = new List<AttentionSignal>();
        AttentionScanner.Scan(
            signals,
            request,
            "Fleet",
            Array.Empty<AgentSessionDto>(),
            Array.Empty<ReservationLeaseDto>(),
            runs,
            RequestInsights.Empty,
            Queued.AddMinutes(6));

        var signal = Assert.Single(signals);
        Assert.Equal(AttentionKind.VerificationFailed, signal.Kind);
        Assert.Equal(AttentionSeverity.Error, signal.Severity);
        Assert.Equal("Verification repository-integrity timed out", signal.Title);
        Assert.Contains("Mandatory baseline command", signal.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_newer_admitted_start_makes_old_green_rows_history_and_nonblocking()
    {
        var request = Request(WorkRequestStatus.Verifying, updatedAt: Queued.AddMinutes(5));
        var runs = new[]
        {
            Run("repository-integrity", VerificationRunStatus.Passed, mandatory: true, fingerprint: "sha256:old"),
            Run("dotnet-test", VerificationRunStatus.Passed, mandatory: true, fingerprint: "sha256:old", kind: VerificationRunKind.ProjectCheck),
            Run("legacy-check", VerificationRunStatus.Failed, mandatory: true, fingerprint: "sha256:old", kind: VerificationRunKind.ProjectCheck),
        };
        var events = new[]
        {
            Event(
                8,
                "verification.started",
                Queued.AddMinutes(4),
                """{"fingerprint":"sha256:new","policyRevision":"policy-1"}"""),
        };

        var view = RequestVerificationViewReader.Read(runs, Array.Empty<AgentSessionDto>(), events);

        Assert.Equal("sha256:new", view.Fingerprint);
        Assert.Equal("policy-1", view.PolicyRevision);
        Assert.True(view.IsAdmittedInProgress);
        Assert.True(view.Admitted!.IsOpen);
        Assert.Equal(Queued.AddMinutes(4), view.Admitted.StartedAt);
        Assert.Empty(view.Baseline);
        Assert.Empty(view.ProjectChecks);
        Assert.Equal(3, view.History.Count);
        Assert.DoesNotContain(view.History, row => row.IsCurrent);
        Assert.Null(view.BaselineSuccess);
        Assert.Null(view.ProjectChecksSuccess);
        Assert.False(view.HasBlockingFailure);
        Assert.Null(view.Running);

        var signals = new List<AttentionSignal>();
        AttentionScanner.Scan(
            signals,
            request,
            "Fleet",
            Array.Empty<AgentSessionDto>(),
            Array.Empty<ReservationLeaseDto>(),
            runs,
            RequestInsights.Empty,
            Queued.AddMinutes(6),
            events);

        Assert.Empty(signals);
    }

    [Fact]
    public void Command_progress_shows_current_command_and_clears_on_matching_terminal()
    {
        var started = Queued.AddMinutes(4);
        var commandAt = started.AddSeconds(2);
        var completed = started.AddSeconds(20);
        var open = new[]
        {
            Event(
                8,
                "verification.started",
                started,
                """{"fingerprint":"sha256:new","policyRevision":"policy-1"}"""),
            Event(
                9,
                "verification.command.started",
                commandAt,
                """{"fingerprint":"sha256:new","policyRevision":"policy-1","commandId":"repository-integrity","runKind":"Baseline","mandatory":true,"timeoutSeconds":900,"startedAt":"2026-09-06T12:04:02+00:00","eventTime":"2026-09-06T12:04:02+00:00"}"""),
        };

        var view = RequestVerificationViewReader.Read(
            Array.Empty<VerificationRunDto>(),
            Array.Empty<AgentSessionDto>(),
            open);

        Assert.True(view.IsAdmittedInProgress);
        Assert.NotNull(view.Admitted!.Command);
        var command = view.Admitted.Command;
        Assert.Equal("repository-integrity", command.CommandId);
        Assert.Equal(900, command.TimeoutSeconds);
        Assert.True(command.Mandatory);
        Assert.Equal("Baseline", command.RunKind);

        var closed = open.Concat(
        [
            Event(
                10,
                "verification.completed",
                completed,
                """{"fingerprint":"sha256:new","policyRevision":"policy-1"}"""),
        ]).ToArray();

        var after = RequestVerificationViewReader.Read(
            Array.Empty<VerificationRunDto>(),
            Array.Empty<AgentSessionDto>(),
            closed);

        Assert.False(after.IsAdmittedInProgress);
        Assert.Null(after.Admitted!.Command);
    }


    [Fact]
    public void Assigned_project_check_copy_ignores_a_later_live_Project_selection()
    {
        var assignment = new ExecutionAssignmentProjectionDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "/repos/devfleet",
            "main",
            BindingValidationRevisionSnapshot: 1,
            ExecutionAssignmentState.Running,
            Queued,
            Queued.AddMinutes(5),
            LastRenewedAt: null,
            LastReconciledAt: null,
            TerminalAt: null,
            VerificationPolicyRevision: "policy-snap",
            BaselineVersion: "baseline-1",
            TrustedVerificationProfileId: "ci",
            TrustedVerificationProfileRevision: "rev-assigned",
            MandatoryCommandIdsJson: "[\"dotnet-test\"]");

        var copy = RequestVerificationPolicyCopy.ProjectChecksMeta(
            assignment,
            liveProfileId: "nightly",
            liveProfileRevision: "rev-live",
            currentProjectChecks: Array.Empty<VerificationRow>(),
            isAssigned: true,
            isTerminal: false);

        Assert.Equal("ci \u00b7 revision rev-assigned \u00b7 assigned snapshot", copy);
        Assert.DoesNotContain("nightly", copy, StringComparison.Ordinal);
        Assert.DoesNotContain("current Project selection", copy, StringComparison.Ordinal);
    }

    private static WorkRequestDto Request(WorkRequestStatus status, DateTimeOffset updatedAt) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            0,
            "Feature",
            1,
            "Normal",
            1,
            "Low",
            (int)status,
            status.ToString(),
            null,
            null,
            "Add progress surface",
            "Show honest progress",
            Queued,
            updatedAt,
            Version: 4);

    private static AgentSessionDto Session(
        string id,
        AgentActivity activity,
        DateTimeOffset startedAt,
        string? currentOperation,
        string statusReason,
        AgentLiveness liveness = AgentLiveness.Online,
        AgentWorkState workState = AgentWorkState.Executing,
        DateTimeOffset? endedAt = null) =>
        new(
            id,
            Guid.NewGuid(),
            Guid.NewGuid(),
            null,
            "AgentOne",
            "implementer",
            "pi",
            "codex/gpt-5.6-sol",
            "prov-1",
            liveness,
            activity,
            AgentAttention.None,
            workState,
            statusReason,
            currentOperation,
            4242,
            startedAt,
            LastHeartbeatAt: null,
            endedAt);

    private static SessionEventDto Event(
        long sequence,
        string type,
        DateTimeOffset occurredAt,
        string payloadJson) =>
        new($"evt-{sequence}", "sess-root", sequence, type, occurredAt, payloadJson);

    private static VerificationRunDto Run(
        string commandId,
        VerificationRunStatus status,
        bool mandatory,
        string fingerprint = "sha256:new",
        VerificationRunKind kind = VerificationRunKind.Baseline,
        DateTimeOffset? startedAt = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            kind == VerificationRunKind.Baseline ? "devfleet-baseline" : "default",
            commandId,
            status,
            status == VerificationRunStatus.Passed ? 0 : 1,
            startedAt ?? Queued.AddMinutes(1),
            status == VerificationRunStatus.Running ? null : (startedAt ?? Queued.AddMinutes(1)).AddSeconds(3),
            OutputSummary: null,
            OutputArtifactPath: null,
            mandatory,
            fingerprint,
            PolicyRevision: "policy-1",
            kind,
            AttemptId: Guid.NewGuid());
}
