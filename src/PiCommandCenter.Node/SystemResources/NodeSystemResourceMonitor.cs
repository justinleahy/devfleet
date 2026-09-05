using System.Globalization;
using PiCommandCenter.Contracts.NodeTransport;

namespace PiCommandCenter.Node.SystemResources;

/// <summary>Captures a fresh, fail-closed view of the node's system resources.</summary>
public interface INodeSystemResourceMonitor
{
    NodeResourceSnapshotMessage Capture();
}

internal sealed class NodeSystemResourceMonitor : INodeSystemResourceMonitor
{
    private const string ProcFileSystemRoot = "/proc";
    private const string CgroupFileSystemRoot = "/sys/fs/cgroup";
    private const double CpuBoundaryTolerance = 1e-9;

    private readonly TimeProvider _timeProvider;
    private readonly string _procFileSystemRoot;
    private readonly string _cgroupFileSystemRoot;
    private readonly Func<(bool IsReady, long TotalSize, long AvailableFreeSpace)> _readRootDrive;
    private CpuReading? _previousCpuReading;

    public NodeSystemResourceMonitor(TimeProvider timeProvider)
        : this(timeProvider, ProcFileSystemRoot, CgroupFileSystemRoot, ReadRootDrive)
    {
    }

    internal NodeSystemResourceMonitor(
        TimeProvider timeProvider,
        string procFileSystemRoot,
        string cgroupFileSystemRoot,
        Func<(bool IsReady, long TotalSize, long AvailableFreeSpace)> readRootDrive)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(procFileSystemRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(cgroupFileSystemRoot);
        ArgumentNullException.ThrowIfNull(readRootDrive);

        _timeProvider = timeProvider;
        _procFileSystemRoot = procFileSystemRoot;
        _cgroupFileSystemRoot = cgroupFileSystemRoot;
        _readRootDrive = readRootDrive;
    }

    public NodeResourceSnapshotMessage Capture()
    {
        var observedAt = _timeProvider.GetUtcNow().ToUniversalTime();
        var cgroupPath = GetCurrentCgroupPath();
        var cpuUsagePercent = CaptureCpuUsage(observedAt, cgroupPath);
        var memory = CaptureMemory(cgroupPath);
        var disk = CaptureDisk();

        return new NodeResourceSnapshotMessage(
            observedAt,
            cpuUsagePercent,
            memory?.UsedBytes,
            memory?.TotalBytes,
            disk?.UsedBytes,
            disk?.TotalBytes,
            ReadNonNegativeDouble(Path.Combine(_procFileSystemRoot, "loadavg")),
            ReadNonNegativeDouble(Path.Combine(_procFileSystemRoot, "uptime")));
    }

    private double? CaptureCpuUsage(DateTimeOffset observedAt, string? cgroupPath)
    {
        CpuReading? current;
        if (cgroupPath is not null && TryReadCpuQuota(cgroupPath, out var quota, out var period))
        {
            current = TryReadCgroupCpuUsage(cgroupPath, out var usage)
                ? CpuReading.ForCgroup(cgroupPath, usage, quota, period, observedAt)
                : null;
        }
        else
        {
            current = TryReadHostCpu(out var idle, out var total)
                ? CpuReading.ForHost(idle, total, observedAt)
                : null;
        }

        if (current is not { } reading)
        {
            _previousCpuReading = null;
            return null;
        }

        var previous = _previousCpuReading;
        _previousCpuReading = reading;
        if (previous is not { } prior || !reading.HasSameSource(prior))
        {
            return null;
        }

        double percent;
        if (reading.Kind == CpuSourceKind.Cgroup)
        {
            if (reading.UsageMicroseconds < prior.UsageMicroseconds)
            {
                return null;
            }

            var wallMicroseconds = (reading.ObservedAt - prior.ObservedAt).TotalMilliseconds * 1000;
            if (!double.IsFinite(wallMicroseconds) || wallMicroseconds <= 0)
            {
                return null;
            }

            var quotaCpus = (double)reading.Quota / reading.Period;
            percent = 100d
                * (reading.UsageMicroseconds - prior.UsageMicroseconds)
                / (wallMicroseconds * quotaCpus);
        }
        else
        {
            if (reading.IdleTicks < prior.IdleTicks || reading.TotalTicks < prior.TotalTicks)
            {
                return null;
            }

            var idleDelta = reading.IdleTicks - prior.IdleTicks;
            var totalDelta = reading.TotalTicks - prior.TotalTicks;
            if (totalDelta == 0 || idleDelta > totalDelta)
            {
                return null;
            }

            percent = 100 * (double)(totalDelta - idleDelta) / totalDelta;
        }

        return NormalizeCpuPercent(percent);
    }

    private bool TryReadCpuQuota(string cgroupPath, out ulong quota, out ulong period)
    {
        quota = 0;
        period = 0;
        if (!TryReadText(Path.Combine(cgroupPath, "cpu.max"), out var text))
        {
            return false;
        }

        var fields = SplitFields(text);
        return fields.Length == 2
            && !fields[0].Equals("max", StringComparison.Ordinal)
            && ulong.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out quota)
            && ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out period)
            && quota > 0
            && period > 0
            && double.IsFinite((double)quota / period);
    }

    private bool TryReadCgroupCpuUsage(string cgroupPath, out ulong usage)
    {
        usage = 0;
        if (!TryReadText(Path.Combine(cgroupPath, "cpu.stat"), out var text))
        {
            return false;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = SplitFields(line);
            if (fields.Length == 2
                && fields[0].Equals("usage_usec", StringComparison.Ordinal)
                && ulong.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out usage))
            {
                return true;
            }
        }

        return false;
    }

    private bool TryReadHostCpu(out ulong idle, out ulong total)
    {
        idle = 0;
        total = 0;
        if (!TryReadText(Path.Combine(_procFileSystemRoot, "stat"), out var text))
        {
            return false;
        }

        var firstLineEnd = text.IndexOf('\n');
        var cpuLine = firstLineEnd >= 0 ? text[..firstLineEnd] : text;
        var fields = SplitFields(cpuLine);
        if (fields.Length < 9 || !fields[0].Equals("cpu", StringComparison.Ordinal))
        {
            return false;
        }

        Span<ulong> counters = stackalloc ulong[8];
        for (var index = 0; index < counters.Length; index++)
        {
            if (!ulong.TryParse(fields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out counters[index]))
            {
                return false;
            }
        }

        try
        {
            idle = checked(counters[3] + counters[4]);
            var nonIdle = checked(
                counters[0]
                + counters[1]
                + counters[2]
                + counters[5]
                + counters[6]
                + counters[7]);
            total = checked(idle + nonIdle);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private BytePair? CaptureMemory(string? cgroupPath)
    {
        if (cgroupPath is null)
        {
            return ReadHostMemory();
        }

        var currentPath = Path.Combine(cgroupPath, "memory.current");
        if (!File.Exists(currentPath))
        {
            return ReadHostMemory();
        }

        if (!TryReadNonNegativeInt64(currentPath, out var used))
        {
            return null;
        }

        var maximumPath = Path.Combine(cgroupPath, "memory.max");
        long total;
        if (!TryReadText(maximumPath, out var maximumText)
            || maximumText.Trim().Equals("max", StringComparison.Ordinal))
        {
            if (ReadHostMemoryTotal() is not { } hostTotal)
            {
                return null;
            }

            total = hostTotal;
        }
        else if (!long.TryParse(
                     maximumText.Trim(),
                     NumberStyles.None,
                     CultureInfo.InvariantCulture,
                     out total)
                 || total < 0)
        {
            return null;
        }

        return CreateBytePair(used, total);
    }

    private BytePair? ReadHostMemory()
    {
        if (!TryReadMemoryInfo(out var totalKilobytes, out var availableKilobytes))
        {
            return null;
        }

        try
        {
            var total = checked(totalKilobytes * 1024);
            var available = checked(availableKilobytes * 1024);
            return CreateBytePair(Math.Max(0, total - available), total);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private long? ReadHostMemoryTotal()
    {
        if (!TryReadMemoryInfoValues(out var values)
            || !values.TryGetValue("MemTotal", out var parsedTotal)
            || parsedTotal is not { } totalKilobytes)
        {
            return null;
        }

        try
        {
            return checked(totalKilobytes * 1024);
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private bool TryReadMemoryInfo(out long totalKilobytes, out long availableKilobytes)
    {
        totalKilobytes = 0;
        availableKilobytes = 0;
        if (!TryReadMemoryInfoValues(out var values)
            || !values.TryGetValue("MemTotal", out var parsedTotal)
            || parsedTotal is not { } total)
        {
            return false;
        }

        totalKilobytes = total;
        if (values.TryGetValue("MemAvailable", out var parsedAvailable))
        {
            if (parsedAvailable is not { } available)
            {
                return false;
            }

            availableKilobytes = available;
            return true;
        }

        if (!values.TryGetValue("MemFree", out var parsedFree) || parsedFree is not { } free)
        {
            return false;
        }

        availableKilobytes = free;
        return true;
    }

    private bool TryReadMemoryInfoValues(out Dictionary<string, long?> values)
    {
        values = new Dictionary<string, long?>(StringComparer.Ordinal);
        if (!TryReadText(Path.Combine(_procFileSystemRoot, "meminfo"), out var text))
        {
            return false;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                continue;
            }

            var fields = SplitFields(line[(separator + 1)..]);
            long? kilobytes = null;
            if (fields.Length == 2
                && fields[1].Equals("kB", StringComparison.Ordinal)
                && long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                && parsed >= 0)
            {
                kilobytes = parsed;
            }

            values[line[..separator]] = kilobytes;
        }

        return true;
    }

    private BytePair? CaptureDisk()
    {
        try
        {
            var drive = _readRootDrive();
            if (!drive.IsReady || drive.TotalSize <= 0 || drive.AvailableFreeSpace < 0)
            {
                return null;
            }

            return CreateBytePair(drive.TotalSize - drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private double? ReadNonNegativeDouble(string path)
    {
        if (!TryReadText(path, out var text))
        {
            return null;
        }

        var fields = SplitFields(text);
        if (fields.Length == 0
            || !double.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || !double.IsFinite(value)
            || value < 0)
        {
            return null;
        }

        return value;
    }

    private string? GetCurrentCgroupPath()
    {
        if (!TryReadText(Path.Combine(_procFileSystemRoot, "self", "cgroup"), out var text))
        {
            return null;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("0::", StringComparison.Ordinal))
            {
                continue;
            }

            var membershipPath = line[3..];
            if (membershipPath.Length == 0 || membershipPath.Equals("/", StringComparison.Ordinal))
            {
                return null;
            }

            return Path.Combine(_cgroupFileSystemRoot, membershipPath.TrimStart('/'));
        }

        return null;
    }

    private static BytePair? CreateBytePair(long used, long total)
        => used >= 0 && total > 0 && used <= total ? new BytePair(used, total) : null;

    private static double? NormalizeCpuPercent(double value)
    {
        if (!double.IsFinite(value)
            || value < -CpuBoundaryTolerance
            || value > 100 + CpuBoundaryTolerance)
        {
            return null;
        }

        return Math.Clamp(value, 0, 100);
    }

    private static bool TryReadNonNegativeInt64(string path, out long value)
    {
        value = 0;
        return TryReadText(path, out var text)
            && long.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0;
    }

    private static bool TryReadText(string path, out string text)
    {
        try
        {
            text = File.ReadAllText(path);
            return true;
        }
        catch (Exception)
        {
            text = string.Empty;
            return false;
        }
    }

    private static string[] SplitFields(string text)
        => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static (bool IsReady, long TotalSize, long AvailableFreeSpace) ReadRootDrive()
    {
        var drive = new DriveInfo("/");
        if (!drive.IsReady)
        {
            return (false, 0, 0);
        }

        return (true, drive.TotalSize, drive.AvailableFreeSpace);
    }

    private readonly record struct BytePair(long UsedBytes, long TotalBytes);

    private enum CpuSourceKind
    {
        Host,
        Cgroup,
    }

    private readonly record struct CpuReading(
        CpuSourceKind Kind,
        string Source,
        ulong UsageMicroseconds,
        ulong IdleTicks,
        ulong TotalTicks,
        ulong Quota,
        ulong Period,
        DateTimeOffset ObservedAt)
    {
        public static CpuReading ForHost(ulong idle, ulong total, DateTimeOffset observedAt)
            => new(CpuSourceKind.Host, string.Empty, 0, idle, total, 0, 0, observedAt);

        public static CpuReading ForCgroup(
            string source,
            ulong usage,
            ulong quota,
            ulong period,
            DateTimeOffset observedAt)
            => new(CpuSourceKind.Cgroup, source, usage, 0, 0, quota, period, observedAt);

        public bool HasSameSource(CpuReading other)
            => Kind == other.Kind
                && Source.Equals(other.Source, StringComparison.Ordinal)
                && Quota == other.Quota
                && Period == other.Period;
    }
}
