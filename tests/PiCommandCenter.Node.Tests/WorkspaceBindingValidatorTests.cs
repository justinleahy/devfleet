using System.Diagnostics;
using Microsoft.Extensions.Options;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Projects;

namespace PiCommandCenter.Node.Tests;

public sealed class WorkspaceBindingValidatorTests : IDisposable
{
    private readonly string _testRoot = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "pi-cc-workspace-validation", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _approvedRoot;

    public WorkspaceBindingValidatorTests()
    {
        _approvedRoot = Directory.CreateDirectory(Path.Combine(_testRoot, "approved")).FullName;
    }

    [Fact]
    public async Task Valid_repository_returns_canonical_path_and_echoes_request_identity()
    {
        var repositoryPath = CreateRepository(_approvedRoot);
        var request = Request(repositoryPath + Path.DirectorySeparatorChar + ".");

        var result = await CreateValidator().ValidateAsync(request);

        Assert.Equal(request.BindingId, result.BindingId);
        Assert.Equal(request.ProjectId, result.ProjectId);
        Assert.Equal(request.Revision, result.Revision);
        Assert.Equal(WorkspaceValidationStatuses.Valid, result.Status);
        Assert.Equal(WorkspaceValidationCodes.Valid, result.ValidationCode);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath)), result.CanonicalRepositoryPath);
        Assert.NotEmpty(result.Detail);
    }

    [Fact]
    public async Task Missing_path_is_reported_without_a_canonical_path()
    {
        var result = await CreateValidator().ValidateAsync(Request(Path.Combine(_approvedRoot, "missing")));

        AssertInvalid(result, WorkspaceValidationCodes.PathMissing);
    }

    [Fact]
    public async Task File_path_is_not_accepted_as_a_directory()
    {
        var file = Path.Combine(_approvedRoot, "repository.txt");
        File.WriteAllText(file, "not a directory");

        var result = await CreateValidator().ValidateAsync(Request(file));

        AssertInvalid(result, WorkspaceValidationCodes.PathNotDirectory);
    }

    [Fact]
    public async Task Repository_outside_approved_roots_is_rejected()
    {
        var outsideRoot = Directory.CreateDirectory(Path.Combine(_testRoot, "outside")).FullName;
        var repositoryPath = CreateRepository(outsideRoot);

        var result = await CreateValidator().ValidateAsync(Request(repositoryPath));

        AssertInvalid(result, WorkspaceValidationCodes.PathOutsideApprovedRoot);
    }

    [Fact]
    public async Task Ordinary_directory_is_valid_and_requires_repository_initialization()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_approvedRoot, "ordinary-directory")).FullName;

        var result = await CreateValidator().ValidateAsync(Request(directory));

        AssertValid(result, WorkspaceValidationCodes.RepositoryInitializationRequired, directory);
    }

    [Fact]
    public async Task Unborn_repository_is_valid_and_requires_a_baseline_commit()
    {
        var repositoryPath = Directory.CreateDirectory(Path.Combine(_approvedRoot, "unborn")).FullName;
        RunGit(repositoryPath, "init", "--initial-branch=main");

        var result = await CreateValidator().ValidateAsync(Request(repositoryPath));

        AssertValid(result, WorkspaceValidationCodes.BaselineCommitRequired, repositoryPath);
    }

    [Fact]
    public async Task Directory_nested_inside_a_repository_is_rejected()
    {
        var repositoryPath = CreateRepository(_approvedRoot);
        var nested = Directory.CreateDirectory(Path.Combine(repositoryPath, "nested")).FullName;

        var result = await CreateValidator().ValidateAsync(Request(nested));

        AssertInvalid(result, WorkspaceValidationCodes.NestedInParentRepository);
    }

    [Fact]
    public async Task Directory_not_writable_by_the_node_is_rejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var directory = Directory.CreateDirectory(Path.Combine(_approvedRoot, "read-only")).FullName;
        var originalMode = File.GetUnixFileMode(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await CreateValidator().ValidateAsync(Request(directory));

            AssertInvalid(result, WorkspaceValidationCodes.PathNotWritable);
        }
        finally
        {
            File.SetUnixFileMode(directory, originalMode);
        }
    }

    [Fact]
    public async Task Symlinked_repository_path_is_rejected()
    {
        var repositoryPath = CreateRepository(_approvedRoot);
        var aliasPath = Path.Combine(_approvedRoot, "repository-alias");
        Directory.CreateSymbolicLink(aliasPath, repositoryPath);

        var result = await CreateValidator().ValidateAsync(Request(aliasPath));

        AssertInvalid(result, WorkspaceValidationCodes.PathSymlink);
    }

    [Fact]
    public async Task Missing_default_branch_is_reported()
    {
        var repositoryPath = CreateRepository(_approvedRoot);

        var result = await CreateValidator().ValidateAsync(Request(repositoryPath, defaultBranch: "missing"));

        AssertInvalid(result, WorkspaceValidationCodes.DefaultBranchMissing);
    }

    [Fact]
    public async Task Git_start_failure_is_reported_without_process_diagnostics()
    {
        var repositoryPath = CreateRepository(_approvedRoot);
        var validator = new WorkspaceBindingValidator(
            Options.Create(new WorkspaceValidationOptions { ApprovedRoots = [_approvedRoot] }),
            Path.Combine(_testRoot, "missing-git-executable"));

        var result = await validator.ValidateAsync(Request(repositoryPath));

        AssertInvalid(result, WorkspaceValidationCodes.GitUnavailable);
        Assert.DoesNotContain(_testRoot, result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_identity_and_revision_are_rejected_at_the_boundary()
    {
        var repositoryPath = CreateRepository(_approvedRoot);
        var validator = CreateValidator();
        var requests = new[]
        {
            Request(repositoryPath) with { BindingId = Guid.Empty },
            Request(repositoryPath) with { ProjectId = Guid.Empty },
            Request(repositoryPath) with { Revision = 0 },
        };

        foreach (var request in requests)
        {
            var result = await validator.ValidateAsync(request);

            AssertInvalid(result, WorkspaceValidationCodes.InvalidRequest);
            Assert.Equal(request.BindingId, result.BindingId);
            Assert.Equal(request.ProjectId, result.ProjectId);
            Assert.Equal(request.Revision, result.Revision);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_testRoot, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private WorkspaceBindingValidator CreateValidator() => new(
        Options.Create(new WorkspaceValidationOptions { ApprovedRoots = [_approvedRoot] }));

    private static WorkspaceBindingValidationRequestMessage Request(
        string repositoryPath,
        string defaultBranch = "main") => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        Revision: 7,
        repositoryPath,
        defaultBranch);

    private static void AssertValid(
        WorkspaceBindingValidationResultMessage result,
        string expectedCode,
        string expectedPath)
    {
        Assert.Equal(WorkspaceValidationStatuses.Valid, result.Status);
        Assert.Equal(expectedCode, result.ValidationCode);
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedPath)),
            result.CanonicalRepositoryPath);
        Assert.NotEmpty(result.Detail);
    }

    private static void AssertInvalid(
        WorkspaceBindingValidationResultMessage result,
        string expectedCode)
    {
        Assert.Equal(WorkspaceValidationStatuses.Invalid, result.Status);
        Assert.Equal(expectedCode, result.ValidationCode);
        Assert.Null(result.CanonicalRepositoryPath);
        Assert.NotEmpty(result.Detail);
        Assert.True(result.Detail.Length <= 512);
    }

    private static string CreateRepository(string parent)
    {
        var repositoryPath = Directory.CreateDirectory(
            Path.Combine(parent, "repo-" + Guid.NewGuid().ToString("N"))).FullName;
        RunGit(repositoryPath, "init", "-b", "main");
        RunGit(repositoryPath, "config", "user.email", "workspace-validator@example.com");
        RunGit(repositoryPath, "config", "user.name", "Workspace Validator Tests");
        File.WriteAllText(Path.Combine(repositoryPath, "tracked.txt"), "tracked\n");
        RunGit(repositoryPath, "add", "tracked.txt");
        RunGit(repositoryPath, "commit", "-m", "Initial commit");
        return repositoryPath;
    }

    private static void RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Git failed to start while arranging the test repository.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Git test setup failed with exit code {process.ExitCode}: {standardOutput}{standardError}");
        }
    }
}
