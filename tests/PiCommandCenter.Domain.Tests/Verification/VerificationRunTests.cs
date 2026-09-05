using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;

namespace PiCommandCenter.Domain.Tests.Verification;

public class VerificationRunTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Record_rejects_empty_command_id()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "default",
            " ",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start,
            "ok",
            null,
            mandatory: true));
    }

    [Fact]
    public void Record_rejects_completed_before_started()
    {
        Assert.Throws<ArgumentException>(() => VerificationRun.Record(
            WorkRequestId.New(),
            "default",
            "dotnet-test",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start.AddSeconds(-1),
            "ok",
            null,
            mandatory: true));
    }

    [Fact]
    public void Passed_run_is_green()
    {
        var run = VerificationRun.Record(
            WorkRequestId.New(),
            "default",
            "dotnet-test",
            VerificationRunStatus.Passed,
            0,
            Start,
            Start.AddMinutes(1),
            "ok",
            "/tmp/out.txt",
            mandatory: true);

        Assert.True(run.IsGreen);
        Assert.True(run.Mandatory);
    }
}
