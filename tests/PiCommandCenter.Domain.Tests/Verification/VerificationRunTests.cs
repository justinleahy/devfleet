using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;

namespace PiCommandCenter.Domain.Tests.Verification;

public class VerificationRunTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid AttemptId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private const string Fingerprint = "req:1|rev:7|commit:abc|branch:main|head:def|content:ghi|policy:1";
    private const string PolicyRevision = "1";

    [Fact]
    public void Record_rejects_empty_command_id()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "devfleet-baseline",
            " ",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start,
            "ok",
            null,
            mandatory: true,
            Fingerprint,
            PolicyRevision,
            VerificationRunKind.Baseline,
            AttemptId));
    }

    [Fact]
    public void Record_rejects_completed_before_started()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "devfleet-baseline",
            "repository-integrity",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start.AddSeconds(-1),
            "ok",
            null,
            mandatory: true,
            Fingerprint,
            PolicyRevision,
            VerificationRunKind.Baseline,
            AttemptId));
    }

    [Fact]
    public void Record_rejects_empty_fingerprint()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "devfleet-baseline",
            "repository-integrity",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start,
            "ok",
            null,
            mandatory: true,
            " ",
            PolicyRevision,
            VerificationRunKind.Baseline,
            AttemptId));
    }

    [Fact]
    public void Record_rejects_empty_policy_revision()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "devfleet-baseline",
            "repository-integrity",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start,
            "ok",
            null,
            mandatory: true,
            Fingerprint,
            " ",
            VerificationRunKind.Baseline,
            AttemptId));
    }

    [Fact]
    public void Record_rejects_empty_attempt_id()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "devfleet-baseline",
            "repository-integrity",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start,
            "ok",
            null,
            mandatory: true,
            Fingerprint,
            PolicyRevision,
            VerificationRunKind.Baseline,
            Guid.Empty));
    }

    [Fact]
    public void Passed_run_is_green()
    {
        var run = VerificationRun.Record(
            WorkRequestId.New(),
            "devfleet-baseline",
            "repository-integrity",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start.AddMinutes(1),
            "ok",
            "/tmp/out.txt",
            mandatory: true,
            Fingerprint,
            PolicyRevision,
            VerificationRunKind.Baseline,
            AttemptId);

        Assert.True(run.IsGreen);
        Assert.True(run.Mandatory);
        Assert.Equal(Fingerprint, run.Fingerprint);
        Assert.Equal(PolicyRevision, run.PolicyRevision);
        Assert.Equal(VerificationRunKind.Baseline, run.RunKind);
        Assert.Equal(AttemptId, run.AttemptId);
        Assert.Equal("devfleet-baseline", run.ProfileId);
        Assert.Equal("repository-integrity", run.CommandId);
    }
}
