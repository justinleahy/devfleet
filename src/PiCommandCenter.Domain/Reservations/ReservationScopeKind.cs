namespace PiCommandCenter.Domain.Reservations;

/// <summary>
/// The deterministic, non-glob reservation scope kinds supported by the PoC.
/// </summary>
public enum ReservationScopeKind
{
    /// <summary>Exactly one file path.</summary>
    File = 0,

    /// <summary>A directory prefix: the directory and all descendants.</summary>
    Directory = 1,

    /// <summary>A named shared resource (for example <c>project-build</c>).</summary>
    Resource = 2,
}
