using PiCommandCenter.Application.Git;
using PiCommandCenter.Node.Git;
using Xunit;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Proves the trusted git seam performs exactly its two whitelisted operations against a real
/// repository and refuses everything else (policy: agents never invoke git; only the supervisor
/// reaches this service, and only with branch creation / checkpoint commit requests).
/// </summary>
public sealed class RestrictedGitServiceTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly RestrictedGitService _service = new("git", TestTimeout);
    private readonly string _repo = CreateRepo();

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_repo)!, recursive: true);

    [Fact]
    public async Task CreateRequestBranch_creates_branch_from_default_branch()
    {
        var created = await _service.CreateRequestBranchAsync(new RequestBranchRequest(
            Guid.NewGuid(), _repo, "main", "request/test-branch"));

        Assert.Equal("request/test-branch", created.BranchName);
        var head = await GitAsync("rev-parse", "refs/heads/main");
        Assert.Equal(head, created.BaseCommitId);
    }

    [Fact]
    public async Task CreateRequestBranch_rejects_an_existing_branch()
    {
        await _service.CreateRequestBranchAsync(new RequestBranchRequest(
            Guid.NewGuid(), _repo, "main", "request/dup"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateRequestBranchAsync(
            new RequestBranchRequest(Guid.NewGuid(), _repo, "main", "request/dup")));
    }

    [Theory]
    [InlineData("-oProxyCommand")]
    [InlineData("bad..name")]
    [InlineData("")]
    public async Task CreateRequestBranch_rejects_unsafe_refs(string branchName)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateRequestBranchAsync(
            new RequestBranchRequest(Guid.NewGuid(), _repo, "main", branchName)));
    }

    [Fact]
    public async Task CreateCheckpointCommit_commits_exactly_the_listed_paths()
    {
        await _service.CreateRequestBranchAsync(new RequestBranchRequest(
            Guid.NewGuid(), _repo, "main", "request/checkpoint"));

        var other = Path.Combine(_repo, "untouched.txt");
        await File.WriteAllTextAsync(other, "keep me out");
        var tracked = Path.Combine(_repo, "src", "feature.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(tracked)!);
        await File.WriteAllTextAsync(tracked, "checkpointed");

        var committed = await _service.CreateCheckpointCommitAsync(new CheckpointCommitRequest(
            Guid.NewGuid(), _repo, "request/checkpoint", "Final checkpoint", ["src/feature.txt"]));

        var files = (await GitAsync("show", "--name-only", "--format=", committed.CommitId))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["src/feature.txt"], files);
        Assert.Equal("request/checkpoint", committed.BranchName);
    }

    [Fact]
    public async Task CreateCheckpointCommit_refuses_an_empty_path_list()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateCheckpointCommitAsync(
            new CheckpointCommitRequest(Guid.NewGuid(), _repo, "main", "message", [])));
    }

    [Fact]
    public async Task CreateCheckpointCommit_refuses_when_on_a_different_branch()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateCheckpointCommitAsync(
            new CheckpointCommitRequest(
                Guid.NewGuid(), _repo, "request/not-checked-out", "message", ["README.md"])));
    }

    private static string CreateRepo()
    {
        var root = Directory.CreateTempSubdirectory("pi-cc-git").FullName;
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        Git(repo, "init", "--initial-branch=main");
        Git(repo, "config", "user.email", "supervisor@pi-cc.test");
        Git(repo, "config", "user.name", "Supervisor");
        Git(repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(repo, "README.md"), "base\n");
        Git(repo, "add", "--", "README.md");
        Git(repo, "commit", "-m", "initial");
        return repo;
    }

    private static string Git(params string[] arguments)
    {
        var repo = arguments[0];
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-C");
        psi.ArgumentList.Add(repo);
        foreach (var argument in arguments.Skip(1))
        {
            psi.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("git did not start.");
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments.Skip(1))} failed.");
        }

        return stdout.Trim();
    }

    private Task<string> GitAsync(params string[] arguments) =>
        Task.Run(() => Git([_repo, .. arguments]));
}
