using System.Diagnostics;
using System.Text.Json;
using PiCommandCenter.Node.Child;
using PiCommandCenter.Node.Runtime.Claude.Hooks;

namespace PiCommandCenter.Node.Tests;

/// <summary>
/// Host-owned Claude PreToolUse reservation hook (SPEC §26.4): fail-closed decisions,
/// Bash/PowerShell denied, app-owned settings outside the repository.
/// </summary>
public sealed class ClaudeReservationHookTests : IDisposable
{
    private readonly string _repo = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pcc-claude-hook-repo", Guid.NewGuid().ToString("N"))).FullName;
    private readonly string _data = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "pcc-claude-hook-data", Guid.NewGuid().ToString("N"))).FullName;
    private readonly FakeReservationGateway _gateway = new();
    private readonly ClaudeHookAuditLog _audit = new();
    private readonly ClaudeReservationHookEvaluator _evaluator;
    private readonly ClaudeReservationHookServer _server;
    private readonly ClaudeHookSettingsInstaller _installer;

    public ClaudeReservationHookTests()
    {
        _evaluator = new ClaudeReservationHookEvaluator(_gateway, _audit);
        _server = new ClaudeReservationHookServer(_evaluator);
        _installer = new ClaudeHookSettingsInstaller(_server, _data);
        Directory.CreateDirectory(Path.Combine(_repo, "src"));
        File.WriteAllText(Path.Combine(_repo, "src", "Foo.cs"), "class Foo {}");
    }

    public void Dispose()
    {
        _server.Dispose();
        try
        {
            Directory.Delete(_repo, recursive: true);
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ClaudeHookSessionContext Context(Guid? leaseId = null, long token = 7, string sessionId = "session-hook-1")
        => new(sessionId, leaseId ?? Guid.NewGuid(), token, _repo);

    private static string PreJson(string tool, string filePath)
        => JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["hook_event_name"] = "PreToolUse",
            ["tool_name"] = tool,
            ["tool_input"] = new Dictionary<string, string> { ["file_path"] = filePath },
        });

    private ClaudeHookInstallResult InstallWrite(ClaudeHookSessionContext context)
        => _installer.Install(ClaudeRuntimeProfiles.ReservedWrite, context);

    private static async Task<(int Exit, string Stdout, string Stderr)> RunHookAsync(
        ClaudeHookInstallResult install,
        string mode,
        string stdin,
        string sessionId)
    {
        var start = new ProcessStartInfo
        {
            FileName = install.HookPath,
            ArgumentList = { mode, install.ValidatorUrl, sessionId },
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("hook failed to start");
        await process.StandardInput.WriteAsync(stdin);
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }

    private static void AssertDenied(int exit, string stdout, string stderr)
    {
        var blocked = exit == 2
            || stdout.Contains("\"permissionDecision\":\"deny\"", StringComparison.Ordinal)
            || stderr.Contains("\"permissionDecision\":\"deny\"", StringComparison.Ordinal);
        Assert.True(blocked, $"exit={exit} stdout={stdout} stderr={stderr}");
    }

    [Fact]
    public async Task Allow_write_inside_a_granted_lease()
    {
        var lease = _gateway.GrantLease();
        var context = Context(lease.LeaseId, lease.FencingToken);
        var decision = await _evaluator.EvaluatePreAsync(
            PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")), context);
        Assert.True(decision.Allow);
        Assert.Equal(0, decision.SuggestedExitCode);
        Assert.Contains("\"permissionDecision\":\"allow\"", decision.StdoutJson, StringComparison.Ordinal);
        Assert.Contains(_gateway.Authorizations, a => a.Path == "src/Foo.cs" && a.Operation == "write");
    }

    [Fact]
    public async Task Deny_when_the_gateway_rejects_the_mutation()
    {
        var lease = _gateway.GrantLease();
        _gateway.OnAuthorize = (_, _) => new MutationAuthorizationResult(
            false, new GatewayError("conflict", "held by another session"));
        var decision = await _evaluator.EvaluatePreAsync(
            PreJson("Edit", Path.Combine(_repo, "src", "Foo.cs")),
            Context(lease.LeaseId, lease.FencingToken));
        Assert.False(decision.Allow);
        Assert.Contains("conflict", decision.Reason, StringComparison.Ordinal);
        Assert.Contains("\"permissionDecision\":\"deny\"", decision.StdoutJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deny_stale_fencing_token()
    {
        var lease = _gateway.GrantLease();
        var decision = await _evaluator.EvaluatePreAsync(
            PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")),
            Context(lease.LeaseId, lease.FencingToken + 99));
        Assert.False(decision.Allow);
        Assert.Contains("invalid_fencing_token", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deny_path_outside_the_repository()
    {
        var lease = _gateway.GrantLease();
        var decision = await _evaluator.EvaluatePreAsync(
            PreJson("Write", "/etc/passwd"),
            Context(lease.LeaseId, lease.FencingToken));
        Assert.False(decision.Allow);
        Assert.Contains("outside", decision.Reason, StringComparison.Ordinal);
        Assert.Empty(_gateway.Authorizations);
    }

    [Fact]
    public async Task Deny_malformed_stdin()
    {
        var decision = await _evaluator.EvaluatePreAsync("{not-json", Context());
        Assert.False(decision.Allow);
        Assert.Equal(2, decision.SuggestedExitCode);
        Assert.Contains("malformed", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Deny_missing_trusted_context()
    {
        var decision = await _evaluator.EvaluatePreAsync(
            PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")),
            context: null);
        Assert.False(decision.Allow);
        Assert.Contains("missing trusted session context", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bash_is_hard_denied()
    {
        var decision = await _evaluator.EvaluatePreAsync(
            "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"rm -rf /\"}}",
            Context());
        Assert.False(decision.Allow);
        Assert.Equal(2, decision.SuggestedExitCode);
    }

    [Fact]
    public async Task Read_inside_the_repository_is_allowed_without_a_lease()
    {
        var decision = await _evaluator.EvaluatePreAsync(
            PreJson("Read", Path.Combine(_repo, "src", "Foo.cs")),
            Context(Guid.Empty, 0));
        Assert.True(decision.Allow);
        Assert.Empty(_gateway.Authorizations);
    }

    [Fact]
    public async Task Read_Glob_and_Grep_cannot_leave_the_repository()
    {
        var context = Context(Guid.Empty, 0);
        var read = await _evaluator.EvaluatePreAsync(PreJson("Read", "/etc/passwd"), context);
        Assert.False(read.Allow);
        Assert.Contains("outside", read.Reason, StringComparison.Ordinal);

        var glob = await _evaluator.EvaluatePreAsync(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tool_name"] = "Glob",
                ["tool_input"] = new Dictionary<string, string>
                {
                    ["pattern"] = "**/*.cs",
                    ["path"] = "/tmp",
                },
            }),
            context);
        Assert.False(glob.Allow);

        var grep = await _evaluator.EvaluatePreAsync(
            JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tool_name"] = "Grep",
                ["tool_input"] = new Dictionary<string, string>
                {
                    ["pattern"] = "secret",
                    ["path"] = "../../etc/passwd",
                },
            }),
            context);
        Assert.False(grep.Allow);
    }

    [Fact]
    public async Task Read_denies_a_symlink_that_escapes_the_repository()
    {
        var outside = Path.Combine(Path.GetTempPath(), "pcc-hook-out-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(outside, "secret");
        var link = Path.Combine(_repo, "src", "escape.cs");
        File.CreateSymbolicLink(link, outside);
        try
        {
            var decision = await _evaluator.EvaluatePreAsync(PreJson("Read", link), Context(Guid.Empty, 0));
            Assert.False(decision.Allow);
            Assert.Contains("outside", decision.Reason, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task Timeout_is_fail_closed()
    {
        var timedOut = new ClaudeReservationHookEvaluator(
            _gateway,
            _audit,
            timeoutFactory: _ =>
            {
                var cts = new CancellationTokenSource();
                cts.Cancel();
                return cts;
            });
        var lease = _gateway.GrantLease();
        _gateway.OnAuthorize = (_, _) => throw new OperationCanceledException();
        var decision = await timedOut.EvaluatePreAsync(
            PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")),
            Context(lease.LeaseId, lease.FencingToken));
        Assert.False(decision.Allow);
        Assert.Contains("timed out", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hook_process_allows_and_denies_over_http()
    {
        var lease = _gateway.GrantLease();
        var context = Context(lease.LeaseId, lease.FencingToken);
        var install = InstallWrite(context);

        var allow = await RunHookAsync(
            install, "pre", PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")), context.SessionId);
        Assert.Equal(0, allow.Exit);
        Assert.Contains("allow", allow.Stdout, StringComparison.Ordinal);

        var bash = await RunHookAsync(
            install,
            "pre",
            "{\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"ls\"}}",
            context.SessionId);
        AssertDenied(bash.Exit, bash.Stdout, bash.Stderr);

        var missing = await RunHookAsync(
            install, "pre", PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")), "unknown-session");
        AssertDenied(missing.Exit, missing.Stdout, missing.Stderr);

        var malformed = await RunHookAsync(install, "pre", "not-json", context.SessionId);
        AssertDenied(malformed.Exit, malformed.Stdout, malformed.Stderr);

        var outside = await RunHookAsync(
            install, "pre", PreJson("Write", "/etc/passwd"), context.SessionId);
        AssertDenied(outside.Exit, outside.Stdout, outside.Stderr);

        _gateway.OnAuthorize = (_, _) => new MutationAuthorizationResult(
            false, new GatewayError("invalid_fencing_token", "stale"));
        var stale = await RunHookAsync(
            install, "pre", PreJson("Edit", Path.Combine(_repo, "src", "Foo.cs")), context.SessionId);
        AssertDenied(stale.Exit, stale.Stdout, stale.Stderr);

        await RunHookAsync(
            install, "post", PreJson("Write", Path.Combine(_repo, "src", "Foo.cs")), context.SessionId);
        Assert.Contains(_audit.Snapshot(), e => e.Operation == "write");
    }

    [Fact]
    public void Settings_deny_shell_and_only_permit_expected_tools()
    {
        var write = InstallWrite(Context());
        using var doc = JsonDocument.Parse(File.ReadAllText(write.SettingsPath));
        var permissions = doc.RootElement.GetProperty("permissions");
        var deny = permissions.GetProperty("deny").EnumerateArray().Select(e => e.GetString()).ToArray();
        var allow = permissions.GetProperty("allow").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("Bash", deny);
        Assert.Contains("PowerShell", deny);
        Assert.Equal(new[] { "Read", "Glob", "Grep", "Edit", "Write" }, allow);
        Assert.Equal("dontAsk", permissions.GetProperty("defaultMode").GetString());

        var readonlyInstall = _installer.Install(
            ClaudeRuntimeProfiles.ReadOnly,
            Context(sessionId: "session-readonly"));
        using var readDoc = JsonDocument.Parse(File.ReadAllText(readonlyInstall.SettingsPath));
        var readAllow = readDoc.RootElement.GetProperty("permissions").GetProperty("allow")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "Read", "Glob", "Grep" }, readAllow);
        Assert.DoesNotContain("Edit", readAllow);
        Assert.DoesNotContain("Write", readAllow);
    }

    [Fact]
    public void App_owned_paths_are_outside_the_repo_with_private_permissions()
    {
        var install = InstallWrite(Context());
        Assert.False(install.RootDirectory.StartsWith(_repo, StringComparison.Ordinal));
        if (OperatingSystem.IsLinux())
        {
            AssertPrivateUnixModes(install);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    [System.Runtime.Versioning.SupportedOSPlatform("freebsd")]
    private static void AssertPrivateUnixModes(ClaudeHookInstallResult install)
    {
        var settingsMode = File.GetUnixFileMode(install.SettingsPath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, settingsMode);
        var hookMode = File.GetUnixFileMode(install.HookPath);
        Assert.True(hookMode.HasFlag(UnixFileMode.UserExecute));
        Assert.False(hookMode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(hookMode.HasFlag(UnixFileMode.OtherRead));
        var dirMode = File.GetUnixFileMode(install.RootDirectory);
        Assert.False(dirMode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(dirMode.HasFlag(UnixFileMode.OtherRead));
    }
}
