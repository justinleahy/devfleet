namespace PiCommandCenter.Domain.Reservations;

/// <summary>
/// Thrown when a reservation scope or path violates normalization rules: absolute paths,
/// <c>..</c> traversal, <c>.git</c> targets, empty resources, or invalid encoding.
/// </summary>
public sealed class InvalidReservationScopeException(string message) : Exception(message)
{
}
