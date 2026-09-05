using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Repository;

namespace PiCommandCenter.Node.Tests;

public class GitArgvPolicyTests
{
    [Theory]
    [InlineData("reset", "--hard")]
    [InlineData("stash", "push")]
    [InlineData("clean", "-fd")]
    [InlineData("checkout", "main")]
    [InlineData("switch", "-c", "tmp")]
    [InlineData("merge", "main")]
    [InlineData("rebase", "main")]
    [InlineData("commit", "-am", "x")]
    [InlineData("add", ".")]
    public void Destructive_git_argv_is_rejected(params string[] arguments)
    {
        Assert.Throws<InvalidOperationException>(() => GitArgvPolicy.EnsureReadOnly(arguments));
    }

    [Fact]
    public void Read_only_status_is_allowed()
    {
        GitArgvPolicy.EnsureReadOnly(["status", "--porcelain=v1", "-z"]);
        GitArgvPolicy.EnsureReadOnly(["rev-parse", "HEAD"]);
        GitArgvPolicy.EnsureReadOnly(["diff", "--name-only", "-z", "HEAD"]);
        GitArgvPolicy.EnsureReadOnly(["ls-files", "--others", "--exclude-standard", "-z"]);
    }
}

public class RepositoryInspectorTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "pi-cc-git", Guid.NewGuid().ToString("N"))).FullName;

    public RepositoryInspectorTests()
    {
        RunGit("init", "-b", "main");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "one\n");
        RunGit("add", "tracked.txt");
        RunGit("commit", "-m", "init");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Dirty_start_is_rejected_when_required()
    {
        File.WriteAllText(Path.Combine(_repo, "tracked.txt"), "dirty\n");
        var inspector = new RepositoryInspector();

        var ex = await Assert.ThrowsAsync<RepositoryDirtyException>(
            () => inspector.CaptureBaselineAsync(_repo, requireCleanStart: true, allowUntrackedFiles: false, CancellationToken.None));

        Assert.Contains(ex.DirtyPaths, p => p.Contains("tracked.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Clean_start_captures_branch_and_commit()
    {
        var inspector = new RepositoryInspector();
        var baseline = await inspector.CaptureBaselineAsync(
            _repo, requireCleanStart: true, allowUntrackedFiles: false, CancellationToken.None);

        Assert.Equal("main", baseline.Branch);
        Assert.False(string.IsNullOrWhiteSpace(baseline.BaseCommit));
        Assert.True(baseline.IsClean);
    }

    [Fact]
    public async Task Diff_attributes_reserved_paths_and_flags_external_ones()
    {
        File.WriteAllText(Path.Combine(_repo, "owned.cs"), "owned\n");
        File.WriteAllText(Path.Combine(_repo, "stray.cs"), "stray\n");
        var commit = (await GitCli.RunAsync(_repo, ["rev-parse", "HEAD"], CancellationToken.None)).Trim();
        var owner = "implementer-1";
        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(),
            3,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(1),
            [new ReservationScopeSpec("file", "owned.cs")],
            owner);

        var inspector = new RepositoryInspector();
        var diff = await inspector.InspectDiffAsync(_repo, commit, [lease], CancellationToken.None);

        var owned = Assert.Single(diff.ChangedFiles, f => f.Path == "owned.cs");
        Assert.Equal(owner, owned.OwnerSessionId);
        Assert.Equal(lease.LeaseId, owned.LeaseId);
        Assert.Contains("stray.cs", diff.UnattributedPaths);

        await Assert.ThrowsAsync<ExternalRepositoryModificationException>(
            () => inspector.DetectExternalChangesAsync(_repo, commit, [lease], CancellationToken.None));
    }

    [Fact]
    public async Task Directory_lease_covers_descendants()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "A.cs"), "a\n");
        var commit = (await GitCli.RunAsync(_repo, ["rev-parse", "HEAD"], CancellationToken.None)).Trim();
        var lease = new ReservationLeaseInfo(
            Guid.NewGuid(),
            1,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(1),
            [new ReservationScopeSpec("directory", "src")],
            "impl");

        var inspector = new RepositoryInspector();
        await inspector.DetectExternalChangesAsync(_repo, commit, [lease], CancellationToken.None);
    }

    private void RunGit(params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = _repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(start)
            ?? throw new InvalidOperationException("git failed to start");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(process.StandardError.ReadToEnd());
        }
    }
}

public class RuntimeCrashRecoveryTests
{
    [Fact]
    public async Task Crash_marks_owned_active_leases_recovery_required_and_emits_a_fact()
    {
        var gateway = new FakeReservationGateway();
        var owned = new ReservationLeaseInfo(
            Guid.NewGuid(),
            9,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(5),
            [new ReservationScopeSpec("file", "src/A.cs")],
            "child-1");
        var other = new ReservationLeaseInfo(
            Guid.NewGuid(),
            2,
            "Active",
            DateTimeOffset.UtcNow.AddMinutes(5),
            [new ReservationScopeSpec("file", "src/B.cs")],
            "child-2");
        gateway.Seed(owned);
        gateway.Seed(other);
        var spool = new RecordingSpool();
        var recovery = new RuntimeCrashRecovery(gateway, spool, TimeProvider.System);

        var projectId = Guid.NewGuid();
        await recovery.MarkOwnedLeasesRecoveryRequiredAsync(
            Guid.NewGuid(),
            projectId,
            Guid.NewGuid(),
            "child-1",
            "runtime crash",
            CancellationToken.None);

        var marked = Assert.Single(gateway.Recoveries);
        Assert.Equal(owned.LeaseId, marked.LeaseId);
        Assert.Empty(gateway.Releases);
        var evt = Assert.Single(spool.Events);
        Assert.Equal(RuntimeCrashRecovery.EventType, evt.Type);
        Assert.Contains("runtime crash", evt.PayloadJson, StringComparison.Ordinal);
        var listed = await gateway.ListAsync(projectId, false, CancellationToken.None);
        Assert.Equal("Active", listed.Single(l => l.LeaseId == other.LeaseId).State);
        Assert.Equal("RecoveryRequired", listed.Single(l => l.LeaseId == owned.LeaseId).State);
    }

    private sealed class RecordingSpool : INodeEventSpool
    {
        public List<NodeEventMessage> Events { get; } = [];

        public Task AppendAsync(NodeEventMessage message, CancellationToken cancellationToken)
        {
            Events.Add(message);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NodeEventMessage>> PeekPendingAsync(int max, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<NodeEventMessage>>(Events);

        public Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
