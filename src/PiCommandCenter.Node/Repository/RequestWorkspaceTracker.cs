using System.Collections.Concurrent;

namespace PiCommandCenter.Node.Repository;

/// <summary>In-memory per-request git baseline captured at claim start.</summary>
public sealed class RequestWorkspaceTracker
{
    private readonly ConcurrentDictionary<Guid, RepositoryBaseline> _baselines = new();

    public void SetBaseline(Guid requestId, RepositoryBaseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        _baselines[requestId] = baseline;
    }

    public bool TryGetBaseline(Guid requestId, out RepositoryBaseline baseline)
        => _baselines.TryGetValue(requestId, out baseline!);
}
