using System.Runtime.InteropServices;
using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Repository;
using PiCommandCenter.Node.Verification;

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
    public void Only_fixed_read_only_argv_is_allowed()
    {
        GitArgvPolicy.EnsureReadOnly(["status", "--porcelain=v1", "-z"]);
        GitArgvPolicy.EnsureReadOnly(["rev-parse", "HEAD"]);
        GitArgvPolicy.EnsureReadOnly(["rev-parse", "--show-toplevel"]);
        GitArgvPolicy.EnsureReadOnly(
            ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", "HEAD", "--"]);
        GitArgvPolicy.EnsureReadOnly(
            ["diff", "--no-ext-diff", "--no-textconv", "--check", "HEAD", "--"]);
        GitArgvPolicy.EnsureReadOnly(
            [
                "diff", "--no-ext-diff", "--no-textconv", "--no-index", "--check", "--",
                GitArgvPolicy.EmptyFilePath, "new.txt",
            ]);
        GitArgvPolicy.EnsureReadOnly(["ls-files", "--others", "--exclude-standard", "-z"]);
        GitArgvPolicy.EnsureReadOnly(["ls-files", "--cached", "-z"]);
        GitArgvPolicy.EnsureReadOnly(["ls-files", "--stage", "-z"]);
        GitArgvPolicy.EnsureReadOnly(["ls-files", "-u", "-z"]);
    }

    [Theory]
    [InlineData("status", "--short")]
    [InlineData("diff", "--check", "HEAD")]
    [InlineData("diff", "--no-ext-diff", "--check", "HEAD", "--")]
    [InlineData("diff", "--no-ext-diff", "--no-textconv", "--check", "--output=result", "--")]
    [InlineData("ls-files", "--stage")]
    [InlineData("hash-object", "tracked.txt")]
    public void Read_only_near_misses_are_rejected(params string[] arguments)
    {
        Assert.Throws<InvalidOperationException>(() => GitArgvPolicy.EnsureReadOnly(arguments));
    }

    [Fact]
    public void Git_process_argv_disables_optional_index_writes_and_hooks()
    {
        var arguments = GitArgvPolicy.AddProcessSafetyOptions(
            ["diff", "--no-ext-diff", "--no-textconv", "--check", "HEAD", "--"]);

        Assert.Equal("--no-optional-locks", arguments[0]);
        Assert.Equal("-c", arguments[1]);
        Assert.StartsWith("core.hooksPath=", arguments[2], StringComparison.Ordinal);
        Assert.Equal("-c", arguments[3]);
        Assert.Equal("core.fsmonitor=false", arguments[4]);
        Assert.Equal(
            ["diff", "--no-ext-diff", "--no-textconv", "--check", "HEAD", "--"],
            arguments.Skip(5));
    }
}

public class RepositoryInspectorTests : IDisposable
{
    private const int WriteOnly = 1;
    private const int NonBlocking = 0x800;

    private readonly Guid _requestId = Guid.NewGuid();
    private readonly Guid _workspaceBindingId = Guid.NewGuid();
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

    [Fact]
    public void Verification_paths_cannot_leave_the_workspace_or_target_git_metadata()
    {
        Assert.Equal(
            "src/file.cs",
            RepositoryInspector.EnsureSafeRepositoryPath(_repo, "src/file.cs"));
        Assert.Throws<InvalidOperationException>(
            () => RepositoryInspector.EnsureSafeRepositoryPath(_repo, "../outside.txt"));
        Assert.Throws<InvalidOperationException>(
            () => RepositoryInspector.EnsureSafeRepositoryPath(_repo, ".git/config"));
        Assert.Throws<InvalidOperationException>(
            () => RepositoryInspector.EnsureSafeRepositoryPath(_repo, ".GIT/config"));
        if (!OperatingSystem.IsWindows())
        {
            var target = Directory.CreateDirectory(Path.Combine(_repo, "real")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(_repo, "linked"), target);
            Assert.Throws<InvalidOperationException>(
                () => RepositoryInspector.EnsureSafeRepositoryPath(_repo, "linked/file.txt"));
            Assert.Equal(
                @"a\b.txt",
                RepositoryInspector.EnsureSafeRepositoryPath(_repo, @"a\b.txt"));
        }
    }

    [Fact]
    public async Task Fingerprint_covers_content_paths_head_binding_and_policy_but_not_time()
    {
        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        var indexPath = Path.Combine(_repo, ".git", "index");
        var indexBefore = await File.ReadAllBytesAsync(indexPath);
        var trackedPath = Path.Combine(_repo, "tracked.txt");

        var original = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        var repeated = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        var firstTemporaryIndex = Path.Combine(_repo, ".git", "index.devfleet-first.tmp");
        var secondTemporaryIndex = Path.Combine(_repo, ".git", "index.devfleet-second.tmp");
        await File.WriteAllBytesAsync(firstTemporaryIndex, indexBefore);
        var withFirstTemporaryIndex = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        File.Move(firstTemporaryIndex, secondTemporaryIndex);
        var withSecondTemporaryIndex = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        File.Delete(secondTemporaryIndex);
        if (!OperatingSystem.IsWindows())
        {
            var backslashPath = Path.Combine(_repo, @"a\b.txt");
            await File.WriteAllTextAsync(backslashPath, "same\n");
            var withBackslashPath = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
            File.Delete(backslashPath);
            var slashDirectory = Directory.CreateDirectory(Path.Combine(_repo, "a")).FullName;
            await File.WriteAllTextAsync(Path.Combine(slashDirectory, "b.txt"), "same\n");
            var withSlashPath = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
            Directory.Delete(slashDirectory, recursive: true);
            Assert.NotEqual(withBackslashPath, withSlashPath);
        }
        File.SetLastWriteTimeUtc(trackedPath, File.GetLastWriteTimeUtc(trackedPath).AddMinutes(5));
        var afterTimestamp = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        await File.WriteAllTextAsync(trackedPath, "two\n");
        var afterTrackedContent = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        await File.WriteAllTextAsync(trackedPath, "one\n");

        var firstUntracked = Path.Combine(_repo, "first.txt");
        await File.WriteAllTextAsync(firstUntracked, "new\n");
        var afterUntrackedContent = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        var renamedUntracked = Path.Combine(_repo, "renamed.txt");
        File.Move(firstUntracked, renamedUntracked);
        var afterPathChange = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        var afterPolicyChange = await verification.CaptureFingerprintAsync(
            context with { PolicyRevision = "policy-2" },
            CancellationToken.None);
        var afterBindingChange = await verification.CaptureFingerprintAsync(
            context with { BindingValidationRevision = context.BindingValidationRevision + 1 },
            CancellationToken.None);

        Assert.True(
            original == repeated,
            $"Repeated fingerprint diverged for an unchanged repo; original={original}; repeated={repeated}");
        Assert.Equal(original, withFirstTemporaryIndex);
        Assert.Equal(original, withSecondTemporaryIndex);
        Assert.Equal(original, afterTimestamp);
        Assert.NotEqual(original, afterTrackedContent);
        Assert.NotEqual(original, afterUntrackedContent);
        Assert.NotEqual(afterUntrackedContent, afterPathChange);
        Assert.NotEqual(afterPathChange, afterPolicyChange);
        Assert.NotEqual(afterPathChange, afterBindingChange);
        Assert.Equal(indexBefore, await File.ReadAllBytesAsync(indexPath));

        File.Delete(renamedUntracked);
        await File.WriteAllTextAsync(trackedPath, "committed head\n");
        RunGit("add", "tracked.txt");
        RunGit("commit", "-m", "advance");
        var afterHeadChange = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        Assert.NotEqual(original, afterHeadChange);
    }

    [Fact]
    public async Task Repository_integrity_fails_for_an_unmerged_index()
    {
        RunGit("checkout", "-b", "other");
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "other\n");
        RunGit("add", "tracked.txt");
        RunGit("commit", "-m", "other");
        RunGit("checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "main\n");
        RunGit("add", "tracked.txt");
        RunGit("commit", "-m", "main");
        Assert.NotEqual(0, RunGitForExitCode("merge", "other"));

        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        var fingerprint = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        var result = await verification.RunAsync(context, fingerprint, CancellationToken.None);

        Assert.Equal(1, result.RepositoryIntegrity.ExitCode);
        Assert.Contains("unmerged", result.RepositoryIntegrity.StandardError, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Repository_integrity_fails_closed_for_gitlinks()
    {
        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        RunGit(
            "update-index",
            "--add",
            "--cacheinfo",
            $"160000,{context.BaselineCommit},vendor/module");
        var fingerprint = await verification.CaptureFingerprintAsync(
            context,
            CancellationToken.None);

        var result = await verification.RunAsync(context, fingerprint, CancellationToken.None);

        Assert.Equal(1, result.RepositoryIntegrity.ExitCode);
        Assert.Contains(
            "gitlink",
            result.RepositoryIntegrity.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Fingerprint_rejects_a_fifo_without_blocking_or_reading_it()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var fifo = Path.Combine(_repo, "tracked.txt");
        File.Delete(fifo);
        Assert.Equal(0, MkFifo(fifo, Convert.ToUInt32("600", 8)));
        using var stopRelease = new CancellationTokenSource();
        var releaseBlockedReader = Task.Run(async () =>
        {
            try
            {
                while (!stopRelease.IsCancellationRequested)
                {
                    var descriptor = Open(fifo, WriteOnly | NonBlocking, 0);
                    if (descriptor >= 0)
                    {
                        _ = Close(descriptor);
                        return;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(25), stopRelease.Token);
                }
            }
            catch (OperationCanceledException) when (stopRelease.IsCancellationRequested)
            {
            }
        });
        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        BaselineVerificationResult result;
        try
        {
            var fingerprint = await verification.CaptureFingerprintAsync(
                    context,
                    CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
            result = await verification.RunAsync(context, fingerprint, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await stopRelease.CancelAsync();
            await releaseBlockedReader.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.True(
            result.RepositoryIntegrity.ExitCode is null,
            $"Expected fail-closed FIFO integrity; exit={result.RepositoryIntegrity.ExitCode?.ToString() ?? "null"}; error={result.RepositoryIntegrity.StandardError}");
        Assert.True(result.RepositoryIntegrity.Crashed);
        Assert.Contains(
            "regular file",
            result.RepositoryIntegrity.StandardError,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Baseline_returns_mandatory_integrity_and_optional_untracked_whitespace_warning()
    {
        await File.WriteAllTextAsync(Path.Combine(_repo, "warning.txt"), "trailing space \n");
        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        var fingerprint = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        var result = await verification.RunAsync(context, fingerprint, CancellationToken.None);

        Assert.Equal(fingerprint, result.Fingerprint);
        Assert.Equal(IBaselineVerification.RepositoryIntegrityCommandId, result.RepositoryIntegrity.CommandId);
        Assert.True(result.RepositoryIntegrity.Mandatory);
        Assert.True(
            result.RepositoryIntegrity.ExitCode == 0,
            $"Expected integrity exit 0; actual={result.RepositoryIntegrity.ExitCode?.ToString() ?? "null"}; error={result.RepositoryIntegrity.StandardError}");
        Assert.Equal(IBaselineVerification.WhitespaceCommandId, result.Whitespace.CommandId);
        Assert.False(result.Whitespace.Mandatory);
        Assert.True(
            result.Whitespace.ExitCode == 1,
            $"Expected whitespace exit 1; actual={result.Whitespace.ExitCode?.ToString() ?? "null"}; error={result.Whitespace.StandardError}");
        Assert.False(result.Whitespace.TimedOut);
        Assert.False(result.Whitespace.Cancelled);
        Assert.False(result.Whitespace.Crashed);
        Assert.Contains("trailing whitespace", result.Whitespace.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Filter_driver_scan_fails_closed_for_fifo_git_config_without_hanging()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var config = Path.Combine(_repo, ".git", "config");
        var backup = config + ".bak";
        File.Move(config, backup);
        Assert.Equal(0, MkFifo(config, Convert.ToUInt32("600", 8)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepositoryInspector.FindRepositoryFilterDriversAsync(_repo, timeout.Token));

        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task Filter_driver_scan_fails_closed_for_fifo_commondir_without_hanging()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var commondir = Path.Combine(_repo, ".git", "commondir");
        Assert.Equal(0, MkFifo(commondir, Convert.ToUInt32("600", 8)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepositoryInspector.FindRepositoryFilterDriversAsync(_repo, timeout.Token));

        Assert.Contains("metadata", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(timeout.IsCancellationRequested);
    }

    [Fact]
    public async Task Filter_driver_scan_fails_closed_for_symlink_git_config()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var config = Path.Combine(_repo, ".git", "config");
        var target = Path.Combine(_repo, "outside-config");
        await File.WriteAllTextAsync(target, "[core]\n");
        File.Delete(config);
        File.CreateSymbolicLink(config, target);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepositoryInspector.FindRepositoryFilterDriversAsync(_repo, CancellationToken.None));
    }

    [Fact]
    public async Task Git_overlay_rejects_nested_symlink_ref_directory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var externalRefs = Directory.CreateDirectory(Path.Combine(_repo, "external-refs")).FullName;
        await File.WriteAllTextAsync(Path.Combine(externalRefs, "leaked"), "0123456789012345678901234567890123456789\n");
        Directory.CreateSymbolicLink(Path.Combine(_repo, ".git", "refs", "linked"), externalRefs);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepositoryInspector.RunGitReadOnlyAsync(
                _repo,
                ["rev-parse", "HEAD"],
                [],
                CancellationToken.None));
    }

    [Fact]
    public async Task Baseline_does_not_execute_locally_configured_content_filters()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(_repo, "filter-ran");
        RunGit("config", "filter.untrusted.clean", $"touch {marker}; cat");
        RunGit("config", "filter.untrusted.process", $"touch {marker}; cat");
        await File.WriteAllTextAsync(Path.Combine(_repo, ".gitattributes"), "*.txt filter=untrusted\n");
        RunGit("add", ".gitattributes");
        RunGit("commit", "-m", "attributes");
        File.Delete(Path.Combine(_repo, ".gitattributes"));
        File.Delete(marker);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "changed\n");

        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        var fingerprint = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        _ = await verification.RunAsync(context, fingerprint, CancellationToken.None);

        Assert.False(File.Exists(marker), $"Content filter marker was present at {marker} after baseline.");
    }

    [Fact]
    public async Task Git_read_ignores_live_filter_config_when_scan_did_not_observe_the_driver()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var marker = Path.Combine(_repo, "filter-ran-race");
        RunGit("config", "filter.untrusted.clean", $"touch {marker}; cat");
        RunGit("config", "filter.untrusted.process", $"touch {marker}; cat");
        await File.WriteAllTextAsync(Path.Combine(_repo, ".gitattributes"), "*.txt filter=untrusted\n");
        File.Delete(marker);
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "changed-for-filter\n");

        _ = await RepositoryInspector.RunGitReadOnlyAsync(
            _repo,
            ["diff", "--no-ext-diff", "--no-textconv", "--name-only", "-z", "HEAD", "--"],
            [],
            CancellationToken.None);

        Assert.False(
            File.Exists(marker),
            $"Live Git config filter ran despite an empty scanned driver overlay; marker={marker}.");
    }

    [Fact]
    public async Task Fingerprint_changes_when_a_regular_file_becomes_executable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        var path = Path.Combine(_repo, "tracked.txt");
        var before = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var afterChmod = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(3));
        var afterTimestamp = await verification.CaptureFingerprintAsync(context, CancellationToken.None);

        Assert.NotEqual(before, afterChmod);
        Assert.Equal(afterChmod, afterTimestamp);
    }

    [Fact]
    public async Task Baseline_integrity_times_out_the_whole_command_not_caller_cancel()
    {
        var verification = new BaselineVerification(TimeSpan.FromTicks(1));
        var context = await CreateBaselineContextAsync();

        var fingerprint = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(fingerprint));

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => verification.CaptureFingerprintAsync(context, cancelled.Token));

        var timedOut = await verification.RunAsync(context, fingerprint, CancellationToken.None);
        Assert.True(timedOut.RepositoryIntegrity.TimedOut);
        Assert.False(timedOut.RepositoryIntegrity.Cancelled);
        Assert.True(timedOut.RepositoryIntegrity.Mandatory);
        Assert.Null(timedOut.RepositoryIntegrity.ExitCode);

        var cancelledRun = await verification.RunAsync(context, fingerprint, cancelled.Token);
        Assert.True(cancelledRun.RepositoryIntegrity.Cancelled);
        Assert.False(cancelledRun.RepositoryIntegrity.TimedOut);
    }

    [Fact]
    public async Task Overlay_honours_allowlisted_core_whitespace_and_keeps_hostile_config_inert()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        RunGit("config", "core.whitespace", "-trailing-space");
        RunGit("config", "filter.untrusted.clean", "touch filter-ran; cat");
        await File.WriteAllTextAsync(Path.Combine(_repo, "tracked.txt"), "trailing space \n");
        var marker = Path.Combine(_repo, "filter-ran");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }

        var verification = new BaselineVerification();
        var context = await CreateBaselineContextAsync();
        var fingerprint = await verification.CaptureFingerprintAsync(context, CancellationToken.None);
        var result = await verification.RunAsync(context, fingerprint, CancellationToken.None);

        Assert.Equal(0, result.Whitespace.ExitCode);
        Assert.False(File.Exists(marker));

        var config = Path.Combine(_repo, ".git", "config");
        await File.AppendAllTextAsync(config, "\n[include]\n\tpath = /tmp/hostile\n");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RepositoryInspector.ReadRepositoryGitSafetyConfigAsync(_repo, CancellationToken.None));
    }

    private async Task<BaselineVerificationContext> CreateBaselineContextAsync()
    {
        var head = (await GitCli.RunAsync(_repo, ["rev-parse", "HEAD"], CancellationToken.None)).Trim();
        return new BaselineVerificationContext(
            RequestId: _requestId,
            WorkspaceBindingId: _workspaceBindingId,
            BindingValidationRevision: 1,
            RepositoryRoot: _repo,
            BaselineCommit: head,
            CurrentBranchOrHead: "main",
            PolicyRevision: "policy-1");
    }

    private void RunGit(params string[] arguments)
    {
        var (exitCode, standardError) = RunGitForResult(arguments);
        if (exitCode != 0)
        {
            throw new InvalidOperationException(standardError);
        }
    }

    private int RunGitForExitCode(params string[] arguments) =>
        RunGitForResult(arguments).ExitCode;

    private (int ExitCode, string StandardError) RunGitForResult(IReadOnlyList<string> arguments)
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
        return (process.ExitCode, process.StandardError.ReadToEnd());
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MkFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        int mode);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

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
        var projectId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var credentials = new NodeAssignmentCredentialSource();
        credentials.Track(new NodeAssignmentCredential(
            requestId,
            projectId,
            "runtime-crash-recovery-test-token"));
        var recovery = new RuntimeCrashRecovery(
            gateway,
            spool,
            credentials,
            TimeProvider.System);

        await recovery.MarkOwnedLeasesRecoveryRequiredAsync(
            Guid.NewGuid(),
            projectId,
            requestId,
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

        public Task<int> CountPendingForRequestAsync(Guid requestId, CancellationToken cancellationToken)
            => Task.FromResult(Events.Count(e => e.RequestId == requestId));


        public Task DeleteAsync(IReadOnlyCollection<string> eventIds, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
