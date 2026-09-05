using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Verification;

namespace PiCommandCenter.Infrastructure.Tests.Verification;

public class VerificationRunStoreTests
{
    [Fact]
    public async Task Record_assigns_id_and_list_returns_oldest_first()
    {
        var db = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var store = new VerificationRunStore(db);
        var requestId = WorkRequestId.New();
        var start = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        var first = await store.RecordAsync(new VerificationRunDto(
            Guid.Empty,
            requestId.Value,
            "default",
            "true",
            VerificationRunStatus.Passed,
            0,
            start,
            start.AddSeconds(1),
            "ok",
            null,
            Mandatory: true));
        var second = await store.RecordAsync(new VerificationRunDto(
            Guid.Empty,
            requestId.Value,
            "default",
            "optional",
            VerificationRunStatus.Failed,
            2,
            start.AddSeconds(5),
            start.AddSeconds(6),
            "no",
            "/tmp/out.txt",
            Mandatory: false));

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);

        var listed = await store.ListAsync(requestId);
        Assert.Equal([first.Id, second.Id], listed.Select(r => r.Id).ToArray());
        Assert.Equal(VerificationRunStatus.Passed, listed[0].Status);
        Assert.Equal("/tmp/out.txt", listed[1].OutputArtifactPath);
    }
}
