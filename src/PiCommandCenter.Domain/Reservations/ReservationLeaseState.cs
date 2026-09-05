namespace PiCommandCenter.Domain.Reservations;

/// <summary>
/// Lifecycle states of a reservation lease group.
/// </summary>
public enum ReservationLeaseState
{
    /// <summary>Active: the owner may mutate covered paths with the current fencing token.</summary>
    Active = 0,

    /// <summary>
    /// Recovery required: the lease expired and expired-ownership recovery inspection must
    /// complete (or an administrator force-releases) before the scope may be re-granted.
    /// </summary>
    RecoveryRequired = 1,

    /// <summary>Released: the lease no longer grants any ownership.</summary>
    Released = 2,
}
