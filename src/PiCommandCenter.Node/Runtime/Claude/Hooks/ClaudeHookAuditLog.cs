using System.Collections.Concurrent;

namespace PiCommandCenter.Node.Runtime.Claude.Hooks;

/// <summary>Bounded in-memory PostToolUse audit trail (oldest events dropped).</summary>
public sealed class ClaudeHookAuditLog
{
    public const int Capacity = 256;

    private readonly ConcurrentQueue<ClaudeHookAuditEvent> _events = new();
    private int _count;

    public void Record(ClaudeHookAuditEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _events.Enqueue(entry);
        if (Interlocked.Increment(ref _count) > Capacity)
        {
            if (_events.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _count);
            }
        }
    }

    public IReadOnlyList<ClaudeHookAuditEvent> Snapshot() => [.. _events];
}

/// <summary>One bounded PostToolUse mutation audit record.</summary>
public sealed record ClaudeHookAuditEvent(
    DateTimeOffset At,
    string SessionId,
    string ToolName,
    string Path,
    string Operation);
