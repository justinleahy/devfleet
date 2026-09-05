using PiCommandCenter.Domain.Reservations;

namespace PiCommandCenter.Application.Reservations;

/// <summary>The requested scope acquisition was denied: at least one deterministic conflict.</summary>
public sealed class ReservationConflictException(IReadOnlyList<ReservationConflictDto> conflicts)
    : Exception(BuildMessage(conflicts))
{
    public IReadOnlyList<ReservationConflictDto> Conflicts { get; } = conflicts;

    private static string BuildMessage(IReadOnlyList<ReservationConflictDto> conflicts) =>
        $"Reservation denied with {conflicts.Count} conflict(s): "
        + string.Join("; ", conflicts.Select(c => $"'{c.ScopePath}' held by {c.OwnerSessionId} (lease {c.LeaseId})."));
}

public sealed class ReservationNotFoundException(Guid leaseId)
    : Exception($"Reservation lease '{leaseId}' was not found.")
{
    public Guid LeaseId { get; } = leaseId;
}

public sealed class ReservationStateException(string message) : Exception(message)
{
}

public sealed class ReservationValidationException(string message) : Exception(message)
{
}

/// <summary>Maps a transport-level scope onto a validated, normalized domain scope.</summary>
public static class ReservationScopeMapper
{
    public static ReservationScope ToDomain(ReservationScopeDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        if (!Enum.IsDefined(typeof(ReservationScopeKind), dto.Kind))
        {
            throw new ReservationValidationException($"Unknown reservation scope kind {dto.Kind}.");
        }

        var kind = (ReservationScopeKind)dto.Kind;
        try
        {
            return ReservationScope.Create(kind, dto.Path);
        }
        catch (InvalidReservationScopeException ex)
        {
            throw new ReservationValidationException(ex.Message);
        }
    }
}
