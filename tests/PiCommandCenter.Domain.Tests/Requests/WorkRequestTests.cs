using PiCommandCenter.Domain.Projects;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Tests;

public class WorkRequestTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 4, 8, 0, 0, TimeSpan.Zero);
    private static readonly ProjectId Project = ProjectId.New();

    private static WorkRequest Enqueue(
        WorkRequestKind kind = WorkRequestKind.Development,
        RequestPriority priority = RequestPriority.Normal,
        RiskLevel risk = RiskLevel.Standard,
        string title = " Fix bug ",
        string prompt = "  Make the failing test pass  ") =>
        WorkRequest.Enqueue(Project, kind, priority, risk, title, prompt, Now);

    [Fact]
    public void Enqueue_always_starts_queued_at_version_one()
    {
        var request = Enqueue();

        Assert.NotEqual(Guid.Empty, request.Id.Value);
        Assert.Equal(Project, request.ProjectId);
        Assert.Equal(WorkRequestStatus.Queued, request.Status);
        Assert.Null(request.BlockedPhase);
        Assert.Equal(1, request.Version);
        Assert.Equal(Now, request.CreatedAt);
        Assert.Equal(Now, request.UpdatedAt);
    }

    [Fact]
    public void Enqueue_normalizes_title_and_prompt()
    {
        var request = Enqueue();

        Assert.Equal("Fix bug", request.Title);
        Assert.Equal("Make the failing test pass", request.Prompt);
    }

    [Fact]
    public void Enqueue_keeps_the_requested_classification()
    {
        var request = Enqueue(WorkRequestKind.Analysis, RequestPriority.Urgent, RiskLevel.High);

        Assert.Equal(WorkRequestKind.Analysis, request.Kind);
        Assert.Equal(RequestPriority.Urgent, request.Priority);
        Assert.Equal(RiskLevel.High, request.RiskLevel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Enqueue_rejects_blank_titles(string? title)
    {
        Assert.Throws<ArgumentException>(
            () => WorkRequest.Enqueue(Project, WorkRequestKind.Development, RequestPriority.Normal, RiskLevel.Standard, title!, "prompt", Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void Enqueue_rejects_blank_prompts(string? prompt)
    {
        Assert.Throws<ArgumentException>(
            () => WorkRequest.Enqueue(Project, WorkRequestKind.Development, RequestPriority.Normal, RiskLevel.Standard, "title", prompt!, Now));
    }

    [Fact]
    public void Full_lifecycle_walks_each_phase_in_order()
    {
        var request = Enqueue();
        var at = Now;

        request.Start(at = at.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Starting, request.Status);

        request.BeginPlanning(at = at.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Planning, request.Status);

        request.BeginExecuting(at = at.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Executing, request.Status);

        request.BeginReviewing(at = at.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Reviewing, request.Status);

        request.BeginVerifying(at = at.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Verifying, request.Status);

        request.Complete(at = at.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Completed, request.Status);

        Assert.Equal(Now.AddMinutes(6), request.UpdatedAt);
        Assert.Equal(7, request.Version);
        Assert.Null(request.BlockedPhase);
    }

    [Fact]
    public void Phase_transitions_reject_out_of_order_statuses()
    {
        var request = Enqueue();

        Assert.Throws<InvalidOperationException>(() => request.BeginPlanning(Now));
        request.Start(Now);
        Assert.Throws<InvalidOperationException>(() => request.BeginExecuting(Now));
        request.BeginPlanning(Now);
        Assert.Throws<InvalidOperationException>(() => request.Complete(Now));
    }

    [Fact]
    public void TryCatchUpTo_is_idempotent_and_walks_missing_phases()
    {
        var request = Enqueue();
        request.Start(Now);

        Assert.True(request.TryCatchUpTo(WorkRequestStatus.Verifying, Now));
        Assert.Equal(WorkRequestStatus.Verifying, request.Status);
        Assert.True(request.TryCatchUpTo(WorkRequestStatus.Verifying, Now));
        Assert.False(request.TryCatchUpTo(WorkRequestStatus.Planning, Now));
        Assert.Equal(WorkRequestStatus.Verifying, request.Status);

        Assert.True(request.TryCatchUpTo(WorkRequestStatus.Completed, Now));
        Assert.Equal(WorkRequestStatus.Completed, request.Status);
        Assert.True(request.TryCatchUpTo(WorkRequestStatus.Completed, Now));
        Assert.False(request.TryCatchUpTo(WorkRequestStatus.Planning, Now));
        Assert.Equal(WorkRequestStatus.Completed, request.Status);
    }
    [Fact]
    public void Block_preserves_the_current_phase_and_unblock_resumes_it()
    {
        var request = Enqueue();
        request.Start(Now);
        request.BeginPlanning(Now);

        request.Block(Now.AddMinutes(1));
        Assert.Equal(WorkRequestStatus.Blocked, request.Status);
        Assert.Equal(WorkRequestStatus.Planning, request.BlockedPhase);

        request.Unblock(Now.AddMinutes(2));
        Assert.Equal(WorkRequestStatus.Planning, request.Status);
        Assert.Null(request.BlockedPhase);

        // The resumed phase is still valid for the next transition.
        request.BeginExecuting(Now.AddMinutes(3));
        Assert.Equal(WorkRequestStatus.Executing, request.Status);
    }

    [Fact]
    public void Fail_and_Block_are_allowed_from_queued_and_any_in_flight_phase_but_not_from_terminal()
    {
        var queued = Enqueue();
        queued.Fail(Now);
        Assert.Equal(WorkRequestStatus.Failed, queued.Status);

        var blocked = Enqueue();
        blocked.Block(Now);
        Assert.Equal(WorkRequestStatus.Blocked, blocked.Status);
        Assert.Equal(WorkRequestStatus.Queued, blocked.BlockedPhase);
        blocked.Unblock(Now);
        Assert.Equal(WorkRequestStatus.Queued, blocked.Status);

        var completed = Enqueue();
        completed.Start(Now);
        completed.BeginPlanning(Now);
        completed.BeginExecuting(Now);
        completed.BeginReviewing(Now);
        completed.BeginVerifying(Now);
        completed.Complete(Now);
        Assert.Throws<InvalidOperationException>(() => completed.Fail(Now));
        Assert.Throws<InvalidOperationException>(() => completed.Block(Now));
    }
    [Fact]
    public void Unblock_requires_a_blocked_request_with_a_preserved_phase()
    {
        var queued = Enqueue();
        Assert.Throws<InvalidOperationException>(() => queued.Unblock(Now));
    }

    [Fact]
    public void Cancel_is_allowed_until_a_terminal_state_is_reached()
    {
        var queued = Enqueue();
        queued.Cancel(Now);
        Assert.Equal(WorkRequestStatus.Cancelled, queued.Status);
        Assert.Throws<InvalidOperationException>(() => queued.Cancel(Now));

        var blocked = Enqueue();
        blocked.Start(Now);
        blocked.Block(Now);
        blocked.Cancel(Now);
        Assert.Equal(WorkRequestStatus.Cancelled, blocked.Status);
        Assert.Null(blocked.BlockedPhase);

        var completed = Enqueue();
        completed.Start(Now);
        completed.BeginPlanning(Now);
        completed.BeginExecuting(Now);
        completed.BeginReviewing(Now);
        completed.BeginVerifying(Now);
        completed.Complete(Now);
        Assert.Throws<InvalidOperationException>(() => completed.Cancel(Now));
    }
    [Fact]
    public void Rehydrate_round_trips_state_and_version()
    {
        var id = WorkRequestId.New();
        var createdAt = new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero);
        var updatedAt = createdAt.AddHours(4);

        var request = WorkRequest.Rehydrate(
            id,
            Project,
            WorkRequestKind.Review,
            RequestPriority.High,
            RiskLevel.Small,
            "Review diff",
            "Review the changes",
            WorkRequestStatus.Executing,
            blockedPhase: null,
            createdAt,
            updatedAt,
            version: 9);

        Assert.Equal(id, request.Id);
        Assert.Equal(WorkRequestStatus.Executing, request.Status);
        Assert.Null(request.BlockedPhase);
        Assert.Equal(createdAt, request.CreatedAt);
        Assert.Equal(updatedAt, request.UpdatedAt);
        Assert.Equal(9, request.Version);
    }

    [Fact]
    public void Rehydrate_of_a_blocked_request_requires_a_preserved_phase()
    {
        Assert.Throws<ArgumentException>(() => WorkRequest.Rehydrate(
            WorkRequestId.New(),
            Project,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "t",
            "p",
            WorkRequestStatus.Blocked,
            blockedPhase: null,
            Now,
            Now,
            version: 1));
    }

    [Fact]
    public void Rehydrate_of_a_non_blocked_request_rejects_a_blocked_phase()
    {
        Assert.Throws<ArgumentException>(() => WorkRequest.Rehydrate(
            WorkRequestId.New(),
            Project,
            WorkRequestKind.Development,
            RequestPriority.Normal,
            RiskLevel.Standard,
            "t",
            "p",
            WorkRequestStatus.Queued,
            blockedPhase: WorkRequestStatus.Planning,
            Now,
            Now,
            version: 1));
    }
}
