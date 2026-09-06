namespace PiCommandCenter.Node;

/// <summary>Provides child-only cancellation and combined root/child heartbeat visibility.</summary>
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

    public Task<bool> CancelChildSessionAsync(string sessionId, string reason)
        => _children.CancelSessionAsync(sessionId, reason);
}
