using PiCommandCenter.Application.Runtime;
using PiCommandCenter.Node.Runtime;
using PiCommandCenter.Node.Runtime.Claude;


namespace PiCommandCenter.Node.Child;

/// <summary>Terminal result of one child agent.</summary>
public sealed record ChildTerminal(string Status, string Reason);

/// <summary>
/// Supervisor-side state for one running child agent: identity and parent link, the worker
/// session, a monotonic spool sequence, and the terminal completion source that
/// <c>agent.await</c> waits on.
/// </summary>
public sealed class ChildAgent
{
    private long _sequence;
    private int _closed;

    public ChildAgent(
        string sessionId,
        string agentName,
        string role,
        string runtimeProfile,
        string parentSessionId,
        string requestId,
        string projectId,
        string nodeId,
        string repositoryRoot,
        DateTimeOffset startedAt)
    {
        SessionId = sessionId;
        AgentName = agentName;
        Role = role;
        RuntimeProfile = runtimeProfile;
        ParentSessionId = parentSessionId;
        RequestId = requestId;
        ProjectId = projectId;
        NodeId = nodeId;
        RepositoryRoot = repositoryRoot;
        StartedAt = startedAt;
    }

    public string SessionId { get; }

    public string AgentName { get; }

    public string Role { get; }

    public string RuntimeProfile { get; }

    public string ParentSessionId { get; }

    public string RequestId { get; }

    public string ProjectId { get; }

    public string NodeId { get; }

    public string RepositoryRoot { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Lease acquired from the spawn's requested write scopes, when granted.</summary>
    public Guid? LeaseId { get; set; }

    public long? FencingToken { get; set; }

    /// <summary>Adapter that owns the child process, set once start completes.</summary>
    public IAgentRuntimeAdapter? Adapter { get; set; }

    /// <summary>True once a cancel was requested by <c>agent.cancel</c>.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>Current status label for <c>agent.status</c>.</summary>
    public string Status { get; private set; } = ChildAgentStatus.Running;

    /// <summary>Completes exactly once, with the terminal status and reason.</summary>
    public TaskCompletionSource<ChildTerminal> Terminal { get; }
        = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsTerminal => Terminal.Task.IsCompleted;

    public void MarkStarted() => Status = ChildAgentStatus.Running;

    public void RequestCancel() => CancelRequested = true;

    /// <summary>Cancels and disposes the runtime session exactly once.</summary>
    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        var adapter = Adapter;
        if (adapter is null)
        {
            return;
        }

        try
        {
            if (adapter is PiRuntimeAdapter pi)
            {
                await pi.CloseSessionAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
            }
            else if (adapter is ClaudeCodeRuntimeAdapter claude)
            {
                await claude.CloseSessionAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await adapter.CancelAsync(SessionId, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Terminal close is best-effort; watch/cancel already recorded the outcome.
        }
    }

    /// <summary>Monotonic spool sequence for this child's event stream.</summary>
    public long AllocateSequence() => Interlocked.Increment(ref _sequence);

    /// <summary>Unused for adapter-backed children; retained for diagnostics.</summary>
    public Task EmitAsync(
        string type,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}
