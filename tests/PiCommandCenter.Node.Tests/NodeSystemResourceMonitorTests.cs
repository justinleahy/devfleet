using PiCommandCenter.Contracts.NodeTransport;
using PiCommandCenter.Node.SystemResources;

namespace PiCommandCenter.Node.Tests;

public sealed class NodeSystemResourceMonitorTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "devfleet-resource-" + Guid.NewGuid().ToString("N"));
    private readonly MutableTimeProvider _clock = new(T0);

    public NodeSystemResourceMonitorTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "proc", "self"));
        Directory.CreateDirectory(Path.Combine(_root, "sys", "fs", "cgroup"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Host_procfs_parses_memory_load_uptime_and_cpu_delta()
    {
        WriteHostCgroup();
        WriteMeminfo(memTotalKb: 8192, memAvailableKb: 2048);
        WriteLoadavg("0.61 0.40 0.30 1/100 42");
        WriteUptime("86400.12 12345.67");
        WriteStat(user: 1000, system: 1000, idle: 8000, iowait: 0);

        var monitor = CreateMonitor(disk: (Ready: true, Total: 100L, Available: 40L));

        var first = monitor.Capture();
        Assert.Equal(T0, first.ObservedAt);
        Assert.Null(first.CpuUsagePercent);
        Assert.Equal(8192L * 1024, first.MemoryTotalBytes);
        Assert.Equal((8192L - 2048L) * 1024, first.MemoryUsedBytes);
        Assert.Equal(60L, first.DiskUsedBytes);
        Assert.Equal(100L, first.DiskTotalBytes);
        Assert.Equal(0.61, first.LoadAverageOneMinute);
        Assert.Equal(86400.12, first.UptimeSeconds);

        WriteStat(user: 1500, system: 1500, idle: 8500, iowait: 0);
        _clock.Advance(TimeSpan.FromSeconds(1));

        var second = monitor.Capture();
        Assert.Equal(T0.AddSeconds(1), second.ObservedAt);
        // dIdle=500, dTotal=1500 → 100 * (1 - 500/1500) = 66.6...
        Assert.Equal(100.0 * (1.0 - 500.0 / 1500.0), second.CpuUsagePercent);
        Assert.Equal((8192L - 2048L) * 1024, second.MemoryUsedBytes);
    }

    [Fact]
    public void Failed_cpu_sample_breaks_the_host_sampling_interval()
    {
        WriteHostCgroup();
        WriteStat(user: 100, system: 0, idle: 900, iowait: 0);

        var monitor = CreateMonitor(disk: (Ready: false, Total: 0L, Available: 0L));
        Assert.Null(monitor.Capture().CpuUsagePercent);

        File.WriteAllText(Path.Combine(_root, "proc", "stat"), "cpu malformed\n");
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(monitor.Capture().CpuUsagePercent);

        WriteStat(user: 300, system: 0, idle: 1100, iowait: 0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(monitor.Capture().CpuUsagePercent);

        WriteStat(user: 400, system: 0, idle: 1200, iowait: 0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(50.0, monitor.Capture().CpuUsagePercent);
    }

    [Fact]
    public void Finite_cgroup_memory_and_cpu_quota_are_selected_over_host()
    {
        WriteCgroupMembership("/docker/limited");
        var cgroup = Path.Combine(_root, "sys", "fs", "cgroup", "docker", "limited");
        Directory.CreateDirectory(cgroup);
        File.WriteAllText(Path.Combine(cgroup, "memory.current"), "1000\n");
        File.WriteAllText(Path.Combine(cgroup, "memory.max"), "4000\n");
        File.WriteAllText(Path.Combine(cgroup, "cpu.max"), "20000 100000\n");
        File.WriteAllText(Path.Combine(cgroup, "cpu.stat"), "usage_usec 1000000\nuser_usec 1\nsystem_usec 1\n");
        WriteMeminfo(memTotalKb: 999999, memAvailableKb: 1);
        WriteLoadavg("1.5 0 0 0/0 1");
        WriteUptime("10 1");
        WriteStat(user: 0, system: 0, idle: 100, iowait: 0);

        var monitor = CreateMonitor(disk: (Ready: true, Total: 10L, Available: 10L));
        Assert.Null(monitor.Capture().CpuUsagePercent);

        File.WriteAllText(Path.Combine(cgroup, "cpu.stat"), "usage_usec 1100000\nuser_usec 1\nsystem_usec 1\n");
        _clock.Advance(TimeSpan.FromSeconds(1));

        var second = monitor.Capture();
        Assert.Equal(1000L, second.MemoryUsedBytes);
        Assert.Equal(4000L, second.MemoryTotalBytes);
        // quotaCpus = 20000/100000 = 0.2; dUsage=100000; dWall=1e6 → 100 * 100000 / (1e6 * 0.2) = 50
        Assert.Equal(50.0, second.CpuUsagePercent);
    }

    [Fact]
    public void First_cpu_sample_is_null_then_host_delta_when_cgroup_cpu_is_unlimited()
    {
        WriteCgroupMembership("/docker/open");
        var cgroup = Path.Combine(_root, "sys", "fs", "cgroup", "docker", "open");
        Directory.CreateDirectory(cgroup);
        File.WriteAllText(Path.Combine(cgroup, "cpu.max"), "max 100000\n");
        File.WriteAllText(Path.Combine(cgroup, "cpu.stat"), "usage_usec 1\n");
        File.WriteAllText(Path.Combine(cgroup, "memory.current"), "50\n");
        File.WriteAllText(Path.Combine(cgroup, "memory.max"), "max\n");
        WriteMeminfo(memTotalKb: 4096, memAvailableKb: 1024);
        WriteLoadavg("0 0 0 0/0 1");
        WriteUptime("1 0");
        WriteStat(user: 200, system: 0, idle: 800, iowait: 0);

        var monitor = CreateMonitor(disk: (Ready: false, Total: 0L, Available: 0L));
        var first = monitor.Capture();
        Assert.Null(first.CpuUsagePercent);
        Assert.Equal(50L, first.MemoryUsedBytes);
        Assert.Equal(4096L * 1024, first.MemoryTotalBytes);
        Assert.Null(first.DiskUsedBytes);
        Assert.Null(first.DiskTotalBytes);

        WriteStat(user: 300, system: 0, idle: 900, iowait: 0);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = monitor.Capture();
        // dIdle=100, dTotal=200 → 50%
        Assert.Equal(50.0, second.CpuUsagePercent);
    }

    [Fact]
    public void Malformed_or_missing_fields_become_null_without_stale_values()
    {
        WriteHostCgroup();
        WriteMeminfo(memTotalKb: 1024, memAvailableKb: 512);
        WriteLoadavg("2.25 0 0 0/0 1");
        WriteUptime("9.5 1");
        WriteStat(user: 10, system: 10, idle: 80, iowait: 0);

        var monitor = CreateMonitor(disk: (Ready: true, Total: 8L, Available: 3L));
        var good = monitor.Capture();
        Assert.Equal(512L * 1024, good.MemoryUsedBytes);
        Assert.Equal(2.25, good.LoadAverageOneMinute);
        Assert.Equal(9.5, good.UptimeSeconds);
        Assert.Equal(5L, good.DiskUsedBytes);

        File.WriteAllText(Path.Combine(_root, "proc", "meminfo"), "MemTotal: not-a-number kB\n");
        File.WriteAllText(Path.Combine(_root, "proc", "loadavg"), "nope\n");
        File.WriteAllText(Path.Combine(_root, "proc", "uptime"), "");
        File.WriteAllText(Path.Combine(_root, "proc", "stat"), "cpu garbage\n");
        _clock.Advance(TimeSpan.FromSeconds(1));

        var bad = monitor.Capture();
        Assert.Null(bad.CpuUsagePercent);
        Assert.Null(bad.MemoryUsedBytes);
        Assert.Null(bad.MemoryTotalBytes);
        Assert.Null(bad.LoadAverageOneMinute);
        Assert.Null(bad.UptimeSeconds);
        Assert.Equal(5L, bad.DiskUsedBytes);
        Assert.Equal(8L, bad.DiskTotalBytes);
    }

    private NodeSystemResourceMonitor CreateMonitor((bool Ready, long Total, long Available) disk)
        => new(
            _clock,
            procFileSystemRoot: Path.Combine(_root, "proc"),
            cgroupFileSystemRoot: Path.Combine(_root, "sys", "fs", "cgroup"),
            readRootDrive: () => (disk.Ready, disk.Total, disk.Available));

    private void WriteHostCgroup()
        => File.WriteAllText(Path.Combine(_root, "proc", "self", "cgroup"), "0::/\n");

    private void WriteCgroupMembership(string path)
        => File.WriteAllText(Path.Combine(_root, "proc", "self", "cgroup"), $"0::{path}\n");

    private void WriteMeminfo(long memTotalKb, long memAvailableKb)
        => File.WriteAllText(
            Path.Combine(_root, "proc", "meminfo"),
            $"MemTotal:       {memTotalKb} kB\nMemAvailable:   {memAvailableKb} kB\nMemFree:        1 kB\n");

    private void WriteLoadavg(string contents)
        => File.WriteAllText(Path.Combine(_root, "proc", "loadavg"), contents + (contents.EndsWith('\n') ? "" : "\n"));

    private void WriteUptime(string contents)
        => File.WriteAllText(Path.Combine(_root, "proc", "uptime"), contents + (contents.EndsWith('\n') ? "" : "\n"));

    private void WriteStat(long user, long system, long idle, long iowait)
        => File.WriteAllText(
            Path.Combine(_root, "proc", "stat"),
            $"cpu  {user} 0 {system} {idle} {iowait} 0 0 0 0 0\ncpu0 {user} 0 {system} {idle} {iowait} 0 0 0 0 0\n");
}
