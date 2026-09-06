using System.Globalization;
using System.Runtime.InteropServices;

namespace PiCommandCenter.Node.Runtime;

/// <summary>
/// Durable OS identity for an assignment worker process. Start time is
/// <c>/proc/&lt;pid&gt;/stat</c> field 22 (clock ticks since boot), not a wall clock.
/// </summary>
public sealed record AssignmentProcessIdentity(
    int ProcessId,
    long StartTimeTicks,
    int ProcessGroupId,
    int SessionId,
    string? ScopeName);

/// <summary>
/// Outcome of an isolated stop. <see cref="Proven"/> is true only when every
/// discovered identity is known to have exited. Unknown or unreadable evidence
/// uses <see cref="ProcessStopUnproven"/>.
/// </summary>
public sealed record AssignmentProcessStopResult(
    bool Proven,
    string BlockerCode,
    IReadOnlyList<AssignmentProcessIdentity> DiscoveredProcesses)
{
    public const string ProcessStopUnproven = "process_stop_unproven";

    public static AssignmentProcessStopResult Unproven(
        IReadOnlyList<AssignmentProcessIdentity>? discovered = null) =>
        new(false, ProcessStopUnproven, discovered ?? []);

    public static AssignmentProcessStopResult Stopped(
        IReadOnlyList<AssignmentProcessIdentity> discovered) =>
        new(true, string.Empty, discovered);
}

/// <summary>
/// Optional capability on a concrete worker process. Not part of
/// <see cref="IPiWorkerProcess"/>.
/// </summary>
public interface IAssignmentProcessIsolation
{
    AssignmentProcessIdentity? Identity { get; }

    Task<AssignmentProcessStopResult> StopIsolatedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Linux <c>/proc</c> identity and process-group stop. Non-Linux callers get
/// unproven results; they must not treat tree kill as isolation proof.
/// </summary>
public static class AssignmentProcessIsolation
{
    public const int SignalTerm = 15;
    public const int SignalKill = 9;

    public static bool IsLinuxIsolationAvailable =>
        OperatingSystem.IsLinux() && SetsidExecutable is not null;

    public static string? SetsidExecutable
    {
        get
        {
            foreach (var candidate in new[] { "/usr/bin/setsid", "/bin/setsid" })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static bool TryReadIdentity(int processId, string? scopeName, out AssignmentProcessIdentity identity)
    {
        identity = null!;
        if (!OperatingSystem.IsLinux() || processId <= 0)
        {
            return false;
        }

        return TryReadIdentityFromStatFile(
            Path.Combine("/proc", processId.ToString(CultureInfo.InvariantCulture), "stat"),
            scopeName,
            out identity);
    }

    public static bool TryParseStat(string stat, string? scopeName, out AssignmentProcessIdentity identity)
    {
        identity = null!;
        if (string.IsNullOrWhiteSpace(stat))
        {
            return false;
        }

        var closeParen = stat.LastIndexOf(')');
        var openParen = stat.IndexOf('(');
        if (openParen < 1 || closeParen <= openParen)
        {
            return false;
        }

        var pidSpan = stat.AsSpan(0, openParen).Trim();
        if (!int.TryParse(pidSpan, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            return false;
        }

        var rest = stat.AsSpan(closeParen + 1).Trim();
        if (rest.IsEmpty)
        {
            return false;
        }

        // After comm: state ppid pgrp session tty_nr tpgid flags minflt cminflt
        // majflt cmajflt utime stime cutime cstime priority nice num_threads
        // itrealvalue starttime (field 22 of the whole stat record; index 19 here).
        Span<Range> fields = stackalloc Range[24];
        var count = rest.Split(fields, ' ', StringSplitOptions.RemoveEmptyEntries);
        if (count < 20)
        {
            return false;
        }

        var pgrpText = rest[fields[2]];
        var sessionText = rest[fields[3]];
        var startText = rest[fields[19]];
        if (!int.TryParse(pgrpText, CultureInfo.InvariantCulture, out var pgrp)
            || !int.TryParse(sessionText, CultureInfo.InvariantCulture, out var session)
            || !long.TryParse(startText, CultureInfo.InvariantCulture, out var startTicks))
        {
            return false;
        }

        identity = new AssignmentProcessIdentity(pid, startTicks, pgrp, session, scopeName);
        return true;
    }

    public static IReadOnlyList<AssignmentProcessIdentity> EnumerateSession(
        int sessionId,
        string? scopeName)
    {
        if (!OperatingSystem.IsLinux() || sessionId <= 0)
        {
            return [];
        }

        var matches = new List<AssignmentProcessIdentity>();
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories("/proc");
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                continue;
            }

            if (!TryReadIdentityFromStatFile(Path.Combine(dir, "stat"), scopeName, out var identity))
            {
                continue;
            }

            if (identity.SessionId == sessionId)
            {
                matches.Add(identity);
            }
        }

        return matches;
    }

    public static AssignmentProcessIdentity? WaitForSessionChild(
        int parentProcessId,
        string? scopeName,
        TimeSpan timeout)
    {
        if (!OperatingSystem.IsLinux() || parentProcessId <= 0)
        {
            return null;
        }

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow <= deadline)
        {
            foreach (var childPid in ReadChildPids(parentProcessId))
            {
                if (TryReadIdentity(childPid, scopeName, out var fromChildren)
                    && fromChildren.SessionId > 0)
                {
                    return fromChildren;
                }
            }

            foreach (var identity in EnumerateDescendants(parentProcessId, scopeName))
            {
                if (identity.ProcessId != parentProcessId
                    && identity.SessionId == identity.ProcessGroupId
                    && identity.SessionId > 0)
                {
                    return identity;
                }
            }

            Thread.Sleep(10);
        }

        return EnumerateDescendants(parentProcessId, scopeName)
            .FirstOrDefault(id => id.ProcessId != parentProcessId);
    }

    public static async Task<AssignmentProcessStopResult> StopSessionAsync(
        AssignmentProcessIdentity? leader,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return AssignmentProcessStopResult.Unproven();
        }

        if (leader is null || leader.SessionId <= 0 || leader.ProcessGroupId <= 0)
        {
            return AssignmentProcessStopResult.Unproven();
        }

        IReadOnlyList<AssignmentProcessIdentity> discovered;
        try
        {
            discovered = EnumerateSession(leader.SessionId, leader.ScopeName);
        }
        catch (IOException)
        {
            return AssignmentProcessStopResult.Unproven();
        }
        catch (UnauthorizedAccessException)
        {
            return AssignmentProcessStopResult.Unproven();
        }

        if (discovered.Count == 0)
        {
            if (!TryReadIdentity(leader.ProcessId, leader.ScopeName, out var still))
            {
                return AssignmentProcessStopResult.Stopped([leader]);
            }

            if (still.StartTimeTicks != leader.StartTimeTicks)
            {
                return AssignmentProcessStopResult.Stopped([leader]);
            }

            if (still.SessionId != leader.SessionId)
            {
                return AssignmentProcessStopResult.Unproven([still]);
            }

            discovered = [still];
        }

        if (HasEscapedSession(leader, discovered))
        {
            return AssignmentProcessStopResult.Unproven(discovered);
        }

        SignalMatchingGroup(leader, discovered, SignalTerm);
        if (!await WaitUntilGoneAsync(leader, discovered, TimeSpan.FromMilliseconds(400), cancellationToken)
                .ConfigureAwait(false))
        {
            SignalMatchingGroup(leader, discovered, SignalKill);
        }

        if (!await WaitUntilGoneAsync(leader, discovered, TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false))
        {
            var remaining = EnumerateSession(leader.SessionId, leader.ScopeName);
            return AssignmentProcessStopResult.Unproven(remaining.Count == 0 ? discovered : remaining);
        }

        return AssignmentProcessStopResult.Stopped(discovered);
    }

    public static bool MatchesLiveProcess(AssignmentProcessIdentity expected)
    {
        if (!TryReadIdentity(expected.ProcessId, expected.ScopeName, out var live))
        {
            return false;
        }

        return live.StartTimeTicks == expected.StartTimeTicks;
    }

    public static int KillProcess(int pidOrGroup, int signal)
    {
        if (!OperatingSystem.IsLinux())
        {
            return -1;
        }

        return NativeKill(pidOrGroup, signal);
    }

    private static bool TryReadIdentityFromStatFile(
        string path,
        string? scopeName,
        out AssignmentProcessIdentity identity)
    {
        identity = null!;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return TryParseStat(text, scopeName, out identity);
    }

    private static IReadOnlyList<int> ReadChildPids(int parentProcessId)
    {
        var path = Path.Combine(
            "/proc",
            parentProcessId.ToString(CultureInfo.InvariantCulture),
            "task",
            parentProcessId.ToString(CultureInfo.InvariantCulture),
            "children");
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var pids = new List<int>();
        foreach (var part in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, CultureInfo.InvariantCulture, out var pid) && pid > 0)
            {
                pids.Add(pid);
            }
        }

        return pids;
    }

    private static IEnumerable<AssignmentProcessIdentity> EnumerateDescendants(
        int parentProcessId,
        string? scopeName)
    {
        string[] dirs;
        try
        {
            dirs = Directory.GetDirectories("/proc");
        }
        catch (IOException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            if (!int.TryParse(name, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                continue;
            }

            if (!TryReadIdentityFromStatFile(Path.Combine(dir, "stat"), scopeName, out var identity))
            {
                continue;
            }

            if (!TryReadPpid(Path.Combine(dir, "stat"), out var ppid))
            {
                continue;
            }

            if (ppid == parentProcessId || pid == parentProcessId)
            {
                yield return identity;
            }
        }
    }

    private static bool TryReadPpid(string path, out int ppid)
    {
        ppid = 0;
        string stat;
        try
        {
            stat = File.ReadAllText(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        var closeParen = stat.LastIndexOf(')');
        if (closeParen < 0 || closeParen + 1 >= stat.Length)
        {
            return false;
        }

        var rest = stat.AsSpan(closeParen + 1).Trim();
        Span<Range> fields = stackalloc Range[8];
        var count = rest.Split(fields, ' ', StringSplitOptions.RemoveEmptyEntries);
        if (count < 2)
        {
            return false;
        }

        return int.TryParse(rest[fields[1]], CultureInfo.InvariantCulture, out ppid);
    }

    private static bool HasEscapedSession(
        AssignmentProcessIdentity leader,
        IReadOnlyList<AssignmentProcessIdentity> discovered)
    {
        foreach (var identity in discovered)
        {
            if (identity.SessionId != leader.SessionId)
            {
                return true;
            }
        }

        if (!TryReadIdentity(leader.ProcessId, leader.ScopeName, out var live))
        {
            return false;
        }

        return live.StartTimeTicks == leader.StartTimeTicks
            && live.SessionId != leader.SessionId;
    }

    private static void SignalMatchingGroup(
        AssignmentProcessIdentity leader,
        IReadOnlyList<AssignmentProcessIdentity> discovered,
        int signal)
    {
        foreach (var identity in discovered)
        {
            if (!MatchesLiveProcess(identity))
            {
                continue;
            }

            if (identity.SessionId != leader.SessionId)
            {
                continue;
            }

            NativeKill(identity.ProcessId, signal);
        }

        if (leader.ProcessGroupId > 0)
        {
            var groupStillOurs = discovered.Any(id =>
                id.ProcessGroupId == leader.ProcessGroupId
                && MatchesLiveProcess(id)
                && id.SessionId == leader.SessionId);
            if (groupStillOurs)
            {
                NativeKill(-leader.ProcessGroupId, signal);
            }
        }
    }

    private static async Task<bool> WaitUntilGoneAsync(
        AssignmentProcessIdentity leader,
        IReadOnlyList<AssignmentProcessIdentity> discovered,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow <= deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discovered.All(id => !MatchesLiveProcess(id))
                && EnumerateSession(leader.SessionId, leader.ScopeName).Count == 0)
            {
                return true;
            }

            try
            {
                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        return discovered.All(id => !MatchesLiveProcess(id))
            && EnumerateSession(leader.SessionId, leader.ScopeName).Count == 0;
    }

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int NativeKill(int pid, int sig);
}
