namespace PiCommandCenter.Application.Live;

/// <summary>How widely a projection change is felt (SPEC §22.3, §31).</summary>
public enum ProjectionScope
{
    /// <summary>Fleet-wide counters changed: node registry, project catalog.</summary>
    Fleet,

    /// <summary>One project's requests, reservations, or registration changed.</summary>
    Project,

    /// <summary>One request's sessions, events, mail, verification, or result changed.</summary>
    Request,
}

/// <summary>
/// One durable write that invalidated a read projection. Ids are empty when the scope does not
/// carry them, so a fleet change needs no project or request identity.
/// </summary>
public readonly record struct ProjectionChange(ProjectionScope Scope, Guid ProjectId, Guid RequestId)
{
    /// <summary>The fleet-wide change: node registry or project catalog.</summary>
    public static ProjectionChange Fleet() => new(ProjectionScope.Fleet, Guid.Empty, Guid.Empty);

    /// <summary>A change to one project's queue, reservations, or registration.</summary>
    public static ProjectionChange Project(Guid projectId) =>
        new(ProjectionScope.Project, projectId, Guid.Empty);

    /// <summary>A change inside one request: sessions, events, mail, verification, result.</summary>
    public static ProjectionChange Request(Guid projectId, Guid requestId) =>
        new(ProjectionScope.Request, projectId, requestId);

    /// <summary>
    /// True when a view of the fleet must re-read. Every change moves at least one fleet counter,
    /// so this is always true; it exists so callers read intent rather than a constant.
    /// </summary>
    public bool AffectsFleet => true;

    /// <summary>True when a view of <paramref name="projectId"/> must re-read.</summary>
    public bool AffectsProject(Guid projectId) =>
        Scope == ProjectionScope.Fleet || ProjectId == projectId;

    /// <summary>True when a view of <paramref name="requestId"/> must re-read.</summary>
    public bool AffectsRequest(Guid requestId) =>
        Scope != ProjectionScope.Request || RequestId == requestId;
}

/// <summary>
/// In-process fan-out from durable writes to live browser views. Publishers call
/// <see cref="Publish"/> after the write commits; Blazor circuits subscribe and refresh instead of
/// polling. This is deliberately not a browser SignalR hub: Interactive Server pages already own a
/// circuit, and a second transport would duplicate it.
/// </summary>
/// <remarks>
/// Registered as a singleton. Publishing is non-blocking and must never throw into the write path:
/// a faulted subscriber is dropped from the notification, not propagated to the writer.
/// </remarks>
public interface IProjectionNotifier
{
    /// <summary>Announces a committed change. Safe to call from any thread.</summary>
    void Publish(ProjectionChange change);

    /// <summary>
    /// Subscribes until the returned handle is disposed. The handler runs on the publisher's
    /// thread, so subscribers must return promptly and marshal their own UI work.
    /// </summary>
    IDisposable Subscribe(Action<ProjectionChange> handler);
}
