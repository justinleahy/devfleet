using PiCommandCenter.Application;

namespace PiCommandCenter.Infrastructure;

/// <summary>
/// Production clock backed by the machine clock, always reporting UTC.
/// </summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
