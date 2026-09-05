namespace PiCommandCenter.Node;

/// <summary>Aggregates cancellation and heartbeat visibility for root and child sessions.</summary>
internal sealed class ChildSessionCanceller(
    Child.PiChildSessionSupervisor children,
    IRootSessionSupervisor roots) : ISessionCanceller
{
    private readonly Child.PiChildSessionSupervisor _children =
        children ?? throw new ArgumentNullException(nameof(children));
    private readonly IRootSessionSupervisor _roots =
        roots ?? throw new ArgumentNullException(nameof(roots));

    public IReadOnlyList<string> ActiveSessionIds
        => [.. _roots.ActiveSessionIds, .. _children.ActiveSessionIds];

    public async Task<bool> CancelSessionAsync(string sessionId, string reason)
    {
        if (await _children.CancelSessionAsync(sessionId, reason).ConfigureAwait(false))
        {
            return true;
        }

        return await _roots.CancelSessionAsync(sessionId, reason).ConfigureAwait(false);
    }
}
