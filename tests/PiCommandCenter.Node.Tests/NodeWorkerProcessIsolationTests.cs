using System.Diagnostics;
using PiCommandCenter.Node.Runtime;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeWorkerProcessIsolationTests
{
    [Fact]
    public void Parses_proc_stat_when_comm_contains_spaces()
    {
        const string stat =
            "4242 (sleep worker) S 1 4242 4242 0 -1 4194304 100 0 0 0 0 0 0 0 20 0 1 0 987654 123 0 0 0 0 0 0 0 0 0 0 0 0 17 3 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0";

        Assert.True(AssignmentProcessIsolation.TryParseStat(stat, "pi-worker", out var identity));
        Assert.Equal(4242, identity.ProcessId);
        Assert.Equal(4242, identity.ProcessGroupId);
        Assert.Equal(4242, identity.SessionId);
        Assert.Equal(987654L, identity.StartTimeTicks);
        Assert.Equal("pi-worker", identity.ScopeName);
    }

    [Fact]
    public void Parse_rejects_truncated_stat()
    {
        Assert.False(AssignmentProcessIsolation.TryParseStat("1 (x) S 1 1", null, out _));
    }

    [Fact]
    public async Task Linux_worker_identity_is_observable_and_stop_is_idempotent()
    {
        if (!AssignmentProcessIsolation.IsLinuxIsolationAvailable)
        {
            return;
        }

        var factory = new NodeWorkerProcessFactory();
        var process = factory.Start("/bin/sleep", "8", Directory.GetCurrentDirectory());
        await using var _ = process;
        var isolation = Assert.IsAssignableFrom<IAssignmentProcessIsolation>(process);
        Assert.NotNull(isolation.Identity);
        Assert.True(isolation.Identity.ProcessId > 0);
        Assert.True(isolation.Identity.SessionId > 0);
        Assert.True(isolation.Identity.StartTimeTicks > 0);
        Assert.True(AssignmentProcessIsolation.MatchesLiveProcess(isolation.Identity));

        var first = await isolation.StopIsolatedAsync(CancellationToken.None);
        Assert.True(first.Proven);
        Assert.Equal(string.Empty, first.BlockerCode);

        var second = await isolation.StopIsolatedAsync(CancellationToken.None);
        Assert.True(second.Proven);
        await process.KillTreeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Linux_stop_kills_inherited_session_children()
    {
        if (!AssignmentProcessIsolation.IsLinuxIsolationAvailable)
        {
            return;
        }

        var script = Path.Combine(Path.GetTempPath(), "pcc-iso-" + Guid.NewGuid().ToString("N") + ".sh");
        await File.WriteAllTextAsync(script, "#!/bin/sh\nsleep 30\n");

        try
        {
            var factory = new NodeWorkerProcessFactory();
            var process = factory.Start("/bin/sh", script, Directory.GetCurrentDirectory());
            await using var _ = process;
            var isolation = Assert.IsAssignableFrom<IAssignmentProcessIsolation>(process);
            Assert.NotNull(isolation.Identity);

            AssignmentProcessIdentity? child = null;
            for (var i = 0; i < 50 && child is null; i++)
            {
                child = AssignmentProcessIsolation
                    .EnumerateSession(isolation.Identity.SessionId, isolation.Identity.ScopeName)
                    .FirstOrDefault(id => id.ProcessId != isolation.Identity.ProcessId);
                if (child is null)
                {
                    await Task.Delay(20);
                }
            }

            Assert.NotNull(child);
            Assert.True(AssignmentProcessIsolation.MatchesLiveProcess(child));

            var result = await isolation.StopIsolatedAsync(CancellationToken.None);
            Assert.True(result.Proven);
            Assert.False(AssignmentProcessIsolation.MatchesLiveProcess(isolation.Identity));
            Assert.False(AssignmentProcessIsolation.MatchesLiveProcess(child));
        }
        finally
        {
            try
            {
                File.Delete(script);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public async Task Linux_group_stop_does_not_kill_unrelated_processes()
    {
        if (!AssignmentProcessIsolation.IsLinuxIsolationAvailable)
        {
            return;
        }

        using var unrelated = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sleep",
            ArgumentList = { "30" },
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        Assert.NotNull(unrelated);

        try
        {
            Assert.True(AssignmentProcessIsolation.TryReadIdentity(
                unrelated.Id,
                scopeName: null,
                out var unrelatedIdentity));

            var factory = new NodeWorkerProcessFactory();
            var process = factory.Start("/bin/sleep", "8", Directory.GetCurrentDirectory());
            await using var _ = process;
            var isolation = Assert.IsAssignableFrom<IAssignmentProcessIsolation>(process);
            Assert.NotNull(isolation.Identity);
            Assert.NotEqual(unrelatedIdentity.SessionId, isolation.Identity.SessionId);

            var result = await isolation.StopIsolatedAsync(CancellationToken.None);
            Assert.True(result.Proven);
            Assert.DoesNotContain(result.DiscoveredProcesses, id => id.ProcessId == unrelatedIdentity.ProcessId);
            Assert.True(AssignmentProcessIsolation.MatchesLiveProcess(unrelatedIdentity));
            Assert.False(unrelated.HasExited);
        }
        finally
        {
            try
            {
                if (!unrelated.HasExited)
                {
                    unrelated.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task Non_linux_stop_is_never_proven_isolation()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var factory = new NodeWorkerProcessFactory();
        var process = factory.Start(
            Environment.ProcessPath ?? "dotnet",
            "--version",
            Directory.GetCurrentDirectory());
        await using var _ = process;
        if (process is IAssignmentProcessIsolation isolation)
        {
            var result = await isolation.StopIsolatedAsync(CancellationToken.None);
            Assert.False(result.Proven);
            Assert.Equal(AssignmentProcessStopResult.ProcessStopUnproven, result.BlockerCode);
        }
    }
}
