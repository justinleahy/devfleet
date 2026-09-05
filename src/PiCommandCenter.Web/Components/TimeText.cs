namespace PiCommandCenter.Web.Components;

/// <summary>
/// Operator-facing time formatting shared by the fleet views. Every projection timestamp is
/// rendered twice — absolute UTC for the audit trail and a coarse relative form for glanceable
/// freshness — so this formatting lives in one place.
/// </summary>
public static class TimeText
{
    /// <summary>Coarse "how long ago" text for a past instant; future instants read as "0s ago".</summary>
    public static string Relative(DateTimeOffset instant, DateTimeOffset now)
    {
        var delta = now - instant;
        if (delta < TimeSpan.Zero)
        {
            delta = TimeSpan.Zero;
        }

        return Coarse(delta) + " ago";
    }

    /// <summary>
    /// Countdown text for a deadline: "in 45s" while it is ahead, "45s ago" once it has passed.
    /// </summary>
    public static string Deadline(DateTimeOffset deadline, DateTimeOffset now)
    {
        var remaining = deadline - now;
        return remaining > TimeSpan.Zero
            ? "in " + Coarse(remaining)
            : Coarse(-remaining) + " ago";
    }

    private static string Coarse(TimeSpan delta)
    {
        if (delta.TotalSeconds < 60)
        {
            return $"{(int)delta.TotalSeconds}s";
        }

        if (delta.TotalMinutes < 60)
        {
            return $"{(int)delta.TotalMinutes}m";
        }

        if (delta.TotalHours < 24)
        {
            return $"{(int)delta.TotalHours}h";
        }

        return $"{(int)delta.TotalDays}d";
    }
}
