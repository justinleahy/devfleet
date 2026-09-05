using PiCommandCenter.Application.Completion;
using PiCommandCenter.Domain.Completion;

namespace PiCommandCenter.Application.Tests.Completion;

public class CompletionContractTests
{
    [Fact]
    public void Evidence_and_decision_carry_the_gate_fields()
    {
        var evidence = new CompletionEvidence(
            "summary",
            ["src/A.cs"],
            [new ReviewFinding("1", "nits", Blocking: false, Resolved: true, UserOverridden: false)],
            "green");
        var decision = new CompletionGateDecision(false, [CompletionRequirements.PlanEvent], Result: null);

        Assert.Equal("summary", evidence.SummaryMarkdown);
        Assert.Equal(["src/A.cs"], evidence.ChangedFiles);
        Assert.False(decision.Accepted);
        Assert.Equal(CompletionRequirements.PlanEvent, Assert.Single(decision.MissingRequirements));
        Assert.Null(decision.Result);
    }

    [Fact]
    public void Request_result_dto_carries_persisted_summary()
    {
        var created = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var dto = new RequestResultDto(
            Guid.NewGuid(),
            "Done",
            ["a.cs"],
            [],
            "ok",
            created);

        Assert.Equal("Done", dto.SummaryMarkdown);
        Assert.Equal(created, dto.CreatedAt);
    }
}
