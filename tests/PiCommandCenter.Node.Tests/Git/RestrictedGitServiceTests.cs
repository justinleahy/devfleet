using PiCommandCenter.Application.Git;
using PiCommandCenter.Node.Git;
using Xunit;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Proves the supervisor's trusted Git seam exposes only workspace preparation, request-branch
/// creation, and checkpoint commits while isolating repository hooks and ambient configuration.
/// </summary>
public sealed class RestrictedGitServiceTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    private readonly RestrictedGitService _service = new("git", TestTimeout);
    private readonly string _repo = CreateRepo();

    public void Dispose()
    {
        Directory.Delete(Path.GetDirectoryName(_repo)!, recursive: true);
        foreach (var root in _tempRoots)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private readonly System.Collections.Generic.List<string> _tempRoots = new();

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
    public async Task CreateRequestBranch_retry_for_the_same_request_is_idempotent()
    {
        var requestId = Guid.NewGuid();
        var first = await _service.CreateRequestBranchAsync(new RequestBranchRequest(
            requestId, _repo, "main", "request/dup"));
        _ = await GitAsync("checkout", "main");

        // A retry after an interrupted start finds the branch still at the default branch tip.
        var second = await _service.CreateRequestBranchAsync(new RequestBranchRequest(
            requestId, _repo, "main", "request/dup"));

        Assert.Equal(first.BranchName, second.BranchName);
        Assert.Equal(first.BaseCommitId, second.BaseCommitId);
        var currentBranch = await GitAsync("branch", "--show-current");
        Assert.Equal("request/dup", currentBranch);
        var count = await GitAsync("rev-list", "--count", "main");
        Assert.Equal("1", count);
    }

    [Fact]
    public async Task CreateRequestBranch_rejects_a_divergent_preexisting_branch()
    {
        await _service.CreateRequestBranchAsync(new RequestBranchRequest(
            Guid.NewGuid(), _repo, "main", "request/divergent"));
        await File.WriteAllTextAsync(Path.Combine(_repo, "extra.txt"), "diverged");
        await _service.CreateCheckpointCommitAsync(new CheckpointCommitRequest(
            Guid.NewGuid(), _repo, "request/divergent", "advance", ["extra.txt"]));
        await GitAsync("checkout", "main");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.CreateRequestBranchAsync(
            new RequestBranchRequest(Guid.NewGuid(), _repo, "main", "request/divergent")));
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

    [Fact]
    public async Task PrepareWorkspace_initializes_an_empty_ordinary_directory()
    {
        var dir = NewTempDirectory("empty");

        var prepared = await _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), dir, "main"));

        Assert.Equal("main", prepared.Branch);
        Assert.Equal("main", await GitInAsync(dir, "symbolic-ref", "--short", "HEAD"));
        Assert.Equal(prepared.BaselineCommitId, await GitInAsync(dir, "rev-parse", "HEAD"));
        Assert.Equal("1", await GitInAsync(dir, "rev-list", "--count", "HEAD"));
        Assert.Equal(
            RestrictedGitService.BaselineCommitMessage,
            await GitInAsync(dir, "log", "-1", "--format=%s"));
        Assert.Equal("DevFleet Supervisor", await GitInAsync(dir, "log", "-1", "--format=%an"));
        Assert.Equal("devfleet@localhost", await GitInAsync(dir, "log", "-1", "--format=%ae"));
        // Empty directory: the baseline commit still exists but tracks nothing.
        Assert.Equal(string.Empty, await GitInAsync(dir, "ls-tree", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task PrepareWorkspace_stages_only_nonignored_contents_of_an_ordinary_directory()
    {
        var dir = NewTempDirectory("nonempty");
        await File.WriteAllTextAsync(Path.Combine(dir, "app.cs"), "class App {}");
        await File.WriteAllTextAsync(Path.Combine(dir, ".gitignore"), "bin/\n*.log\n");
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        await File.WriteAllTextAsync(Path.Combine(dir, "bin", "app.dll"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(dir, "debug.log"), "ignored");

        var prepared = await _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), dir, "main"));

        var tracked = (await GitInAsync(dir, "ls-tree", "-r", "--name-only", "HEAD"))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal([".gitignore", "app.cs"], tracked);
        Assert.Equal(string.Empty, await GitInAsync(dir, "status", "--porcelain"));
        Assert.NotEmpty(prepared.BaselineCommitId);
    }

    [Fact]
    public async Task PrepareWorkspace_completes_the_baseline_for_an_unborn_repository()
    {
        var dir = NewTempDirectory("unborn");
        Git(dir, "init", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(dir, "seed.txt"), "seed");

        var prepared = await _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), dir, "main"));

        Assert.Equal("1", await GitInAsync(dir, "rev-list", "--count", "HEAD"));
        Assert.Equal(
            RestrictedGitService.BaselineCommitMessage,
            await GitInAsync(dir, "log", "-1", "--format=%s"));
        Assert.Equal("seed.txt", await GitInAsync(dir, "ls-tree", "--name-only", "HEAD"));
        Assert.Equal(prepared.BaselineCommitId, await GitInAsync(dir, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task PrepareWorkspace_leaves_existing_history_unchanged()
    {
        var headBefore = await GitAsync("rev-parse", "HEAD");

        var prepared = await _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), _repo, "main"));

        Assert.Equal(headBefore, prepared.BaselineCommitId);
        Assert.Equal("1", await GitAsync("rev-list", "--count", "HEAD"));
        Assert.Equal("initial", await GitAsync("log", "-1", "--format=%s"));
        Assert.Equal("main", prepared.Branch);
    }

    [Fact]
    public async Task PrepareWorkspace_retry_after_interrupted_initialization_creates_one_commit()
    {
        // Simulates a retry after init succeeded but the baseline commit did not.
        var dir = NewTempDirectory("interrupted");
        Git(dir, "init", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(dir, "work.txt"), "work");

        var first = await _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), dir, "main"));
        var second = await _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), dir, "main"));

        Assert.Equal(first.BaselineCommitId, second.BaselineCommitId);
        Assert.Equal("1", await GitInAsync(dir, "rev-list", "--count", "HEAD"));
    }

    [Fact]
    public async Task PrepareWorkspace_refuses_a_branch_mismatch()
    {
        await GitAsync("checkout", "-b", "other");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), _repo, "main")));
    }

    [Fact]
    public async Task PrepareWorkspace_refuses_a_dirty_committed_repository()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "README.md"), "modified\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), _repo, "main")));
    }

    [Fact]
    public async Task PrepareWorkspace_refuses_a_directory_nested_inside_another_repository()
    {
        var nested = Path.Combine(_repo, "nested-dir");
        Directory.CreateDirectory(nested);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), nested, "main")));
    }

    [Fact]
    public async Task PrepareWorkspace_refuses_a_linked_worktree()
    {
        var root = Directory.CreateTempSubdirectory("pi-cc-git").FullName;
        _tempRoots.Add(root);
        var worktree = Path.Combine(root, "worktree");
        await GitAsync("worktree", "add", worktree, "-b", "wt");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PrepareWorkspaceAsync(
            new WorkspacePreparationRequest(Guid.NewGuid(), worktree, "main")));
    }

    [Fact]
    public async Task Checkpoint_commit_never_executes_repository_hooks()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var branch = "request/no-hooks";
        await _service.CreateRequestBranchAsync(
            new RequestBranchRequest(Guid.NewGuid(), _repo, "main", branch));
        var hook = Path.Combine(_repo, ".git", "hooks", "pre-commit");
        await File.WriteAllTextAsync(hook, "#!/bin/sh\nexit 91\n");
        File.SetUnixFileMode(
            hook,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await File.WriteAllTextAsync(Path.Combine(_repo, "safe.txt"), "safe");

        var committed = await _service.CreateCheckpointCommitAsync(
            new CheckpointCommitRequest(Guid.NewGuid(), _repo, branch, "safe", ["safe.txt"]));

        Assert.Equal(committed.CommitId, await GitAsync("rev-parse", "HEAD"));
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

    private string NewTempDirectory(string name)
    {
        var root = Directory.CreateTempSubdirectory("pi-cc-git").FullName;
        _tempRoots.Add(root);
        return Directory.CreateDirectory(Path.Combine(root, name)).FullName;
    }

    private Task<string> GitInAsync(string repo, params string[] arguments) =>
        Task.Run(() => Git([repo, .. arguments]));

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
