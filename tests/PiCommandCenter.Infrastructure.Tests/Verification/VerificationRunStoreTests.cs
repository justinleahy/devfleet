using PiCommandCenter.Application.Verification;
using PiCommandCenter.Domain.Requests;
using PiCommandCenter.Domain.Verification;
using PiCommandCenter.Infrastructure.Verification;
using PiCommandCenter.Application.Live;

namespace PiCommandCenter.Infrastructure.Tests.Verification;

public class VerificationRunStoreTests
{
    [Fact]
    public async Task Record_assigns_id_and_list_returns_oldest_first()
    {
        var db = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var store = new VerificationRunStore(db, new ProjectionNotifier());
        var requestId = WorkRequestId.New();
        var start = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        var firstAttempt = Guid.NewGuid();
        var secondAttempt = Guid.NewGuid();

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
            Mandatory: true,
            Fingerprint: "sha256:aaaaaaaa",
            PolicyRevision: "policy-1",
            RunKind: VerificationRunKind.Baseline,
            AttemptId: firstAttempt));
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
            Mandatory: false,
            Fingerprint: "sha256:bbbbbbbb",
            PolicyRevision: "policy-1",
            RunKind: VerificationRunKind.Intermediate,
            AttemptId: secondAttempt));

        Assert.NotEqual(Guid.Empty, first.Id);
        Assert.NotEqual(first.Id, second.Id);

        var listed = await store.ListAsync(requestId);
        Assert.Equal([first.Id, second.Id], listed.Select(r => r.Id).ToArray());
        Assert.Equal(VerificationRunStatus.Passed, listed[0].Status);
        Assert.Equal("/tmp/out.txt", listed[1].OutputArtifactPath);
        Assert.Equal("sha256:aaaaaaaa", listed[0].Fingerprint);
        Assert.Equal("policy-1", listed[0].PolicyRevision);
        Assert.Equal(VerificationRunKind.Baseline, listed[0].RunKind);
        Assert.Equal(firstAttempt, listed[0].AttemptId);
        Assert.Equal("sha256:bbbbbbbb", listed[1].Fingerprint);
        Assert.Equal("policy-1", listed[1].PolicyRevision);
        Assert.Equal(VerificationRunKind.Intermediate, listed[1].RunKind);
        Assert.Equal(secondAttempt, listed[1].AttemptId);
        Assert.NotEqual(listed[0].AttemptId, listed[1].AttemptId);

        var relisted = await store.ListAsync(requestId);
        Assert.Equal(2, relisted.Count);
    }

    [Fact]
    public async Task Record_reuses_final_identity_and_appends_intermediate()
    {
        var db = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var store = new VerificationRunStore(db, new ProjectionNotifier());
        var requestId = WorkRequestId.New();
        var start = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        VerificationRunDto Dto(
            string fingerprint,
            string policy,
            VerificationRunKind kind,
            string commandId,
            VerificationRunStatus status,
            DateTimeOffset at) =>
            new(
                Guid.Empty,
                requestId.Value,
                "default",
                commandId,
                status,
                0,
                at,
                at.AddSeconds(1),
                "ok",
                null,
                Mandatory: true,
                Fingerprint: fingerprint,
                PolicyRevision: policy,
                RunKind: kind,
                AttemptId: Guid.NewGuid());

        var first = await store.RecordAsync(Dto(
            "sha256:same", "policy-1", VerificationRunKind.Baseline, "true", VerificationRunStatus.Passed, start));
        var duplicate = await store.RecordAsync(Dto(
            "sha256:same", "policy-1", VerificationRunKind.Baseline, "true", VerificationRunStatus.Failed, start.AddSeconds(2)));
        var otherFingerprint = await store.RecordAsync(Dto(
            "sha256:other", "policy-1", VerificationRunKind.Baseline, "true", VerificationRunStatus.Passed, start.AddSeconds(3)));
        var otherPolicy = await store.RecordAsync(Dto(
            "sha256:same", "policy-2", VerificationRunKind.Baseline, "true", VerificationRunStatus.Passed, start.AddSeconds(4)));
        var projectCheck = await store.RecordAsync(Dto(
            "sha256:same", "policy-1", VerificationRunKind.ProjectCheck, "true", VerificationRunStatus.Passed, start.AddSeconds(5)));
        var intermediateA = await store.RecordAsync(Dto(
            "sha256:same", "policy-1", VerificationRunKind.Intermediate, "optional", VerificationRunStatus.Failed, start.AddSeconds(6)));
        var intermediateB = await store.RecordAsync(Dto(
            "sha256:same", "policy-1", VerificationRunKind.Intermediate, "optional", VerificationRunStatus.Failed, start.AddSeconds(7)));

        Assert.Equal(first.Id, duplicate.Id);
        Assert.Equal(VerificationRunStatus.Passed, duplicate.Status);
        Assert.NotEqual(first.Id, otherFingerprint.Id);
        Assert.NotEqual(first.Id, otherPolicy.Id);
        Assert.NotEqual(first.Id, projectCheck.Id);
        Assert.NotEqual(intermediateA.Id, intermediateB.Id);

        var listed = await store.ListAsync(requestId);
        Assert.Equal(6, listed.Count);
        Assert.Equal(
            [
                first.Id,
                otherFingerprint.Id,
                otherPolicy.Id,
                projectCheck.Id,
                intermediateA.Id,
                intermediateB.Id,
            ],
            listed.Select(r => r.Id).ToArray());
        Assert.All(listed, row =>
        {
            Assert.False(string.IsNullOrEmpty(row.Fingerprint));
            Assert.False(string.IsNullOrEmpty(row.PolicyRevision));
            Assert.NotEqual(Guid.Empty, row.AttemptId);
        });
    }

    [Fact]
    public async Task List_recent_caps_in_the_database_and_keeps_newest_fingerprint_rows()
    {
        var db = TestRepositories.CreateContext(TestRepositories.CreateSqliteFile());
        var store = new VerificationRunStore(db, new ProjectionNotifier());
        var requestId = WorkRequestId.New();
        var start = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < 5; i++)
        {
            await store.RecordAsync(new VerificationRunDto(
                Guid.Empty,
                requestId.Value,
                "default",
                "cmd-" + i,
                VerificationRunStatus.Passed,
                0,
                start.AddSeconds(i),
                start.AddSeconds(i + 1),
                "ok",
                "/secret/" + i,
                Mandatory: true,
                Fingerprint: i >= 3 ? "sha256:newest" : "sha256:old",
                PolicyRevision: i >= 3 ? "policy-new" : "policy-old",
                RunKind: i % 2 == 0 ? VerificationRunKind.Baseline : VerificationRunKind.Intermediate,
                AttemptId: Guid.NewGuid()));
        }

        var recent = await store.ListRecentAsync(requestId, maxCount: 2);
        Assert.Equal(2, recent.Count);
        Assert.All(recent, row =>
        {
            Assert.Equal("sha256:newest", row.Fingerprint);
            Assert.Equal("policy-new", row.PolicyRevision);
        });
        Assert.Equal(["cmd-4", "cmd-3"], recent.Select(r => r.CommandId).ToArray());
    }
}
