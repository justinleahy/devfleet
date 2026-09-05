namespace PiCommandCenter.Application.Reservations;

/// <summary>
/// The Control Plane reservation authority: the single strict, atomic store of file
/// reservation leases. All grant/renew/expand/transfer/release operations are atomic and
/// deterministic; every mutation must be authorized here first.
/// </summary>
public interface IReservationService
{
    /// <summary>Atomically acquires all requested scopes or denies with every conflict.</summary>
    Task<ReservationLeaseDto> AcquireAsync(
        AcquireReservationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Extends the lease deadline; rejects stale tokens and non-owners.</summary>
    Task<ReservationLeaseDto> RenewAsync(
        RenewReservationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically adds scopes; on any conflict nothing changes.</summary>
    Task<ReservationLeaseDto> ExpandAsync(
        ExpandReservationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Releases the lease owned by the session.</summary>
    Task<ReservationLeaseDto> ReleaseAsync(
        ReleaseReservationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Atomically transfers ownership and issues a fresh fencing token.</summary>
    Task<ReservationLeaseDto> TransferAsync(
        TransferReservationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Authorizes one mutation against lease, token, session, and scope coverage.</summary>
    Task AuthorizeAsync(
        MutationAuthorizationCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Lists leases for a project (active and recovery-required by default).</summary>
    Task<IReadOnlyList<ReservationLeaseDto>> ListAsync(
        Guid projectId,
        bool includeReleased = false,
        CancellationToken cancellationToken = default);

    /// <summary>Flags an expired lease for expired-ownership recovery inspection (audit fact recorded).</summary>
    Task<ReservationLeaseDto> MarkRecoveryRequiredAsync(
        MarkRecoveryRequiredCommand command,
        CancellationToken cancellationToken = default);

    /// <summary>Human-only forced release with reason, snapshot, audit fact, and token increment.</summary>
    Task<ReservationLeaseDto> ForceReleaseAsync(
        ForceReleaseReservationCommand command,
        CancellationToken cancellationToken = default);
}
