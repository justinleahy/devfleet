using PiCommandCenter.Domain.Completion;
using PiCommandCenter.Domain.Requests;

namespace PiCommandCenter.Domain.Tests.Completion;

public class RequestResultTests
{
    [Fact]
    public void Create_rejects_blank_summary()
    {
        Assert.Throws<ArgumentException>(() => RequestResult.Create(
            WorkRequestId.New(),
            "  ",
            "[]",
            "[]",
            "{}",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Create_defaults_empty_json_documents()
    {
        var result = RequestResult.Create(
            WorkRequestId.New(),
            "Done",
            "  ",
            "",
            null!,
            new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal("[]", result.ChangedFilesJson);
        Assert.Equal("[]", result.ReviewFindingsJson);
        Assert.Equal("{}", result.VerificationSummaryJson);
        Assert.Equal("Done", result.SummaryMarkdown);
    }
}
