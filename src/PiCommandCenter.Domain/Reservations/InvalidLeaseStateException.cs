namespace PiCommandCenter.Domain.Reservations;

/// <summary>A lease operation violated the lease's current state (e.g. recovery on a live lease).</summary>
public sealed class InvalidLeaseStateException(string message) : Exception(message);
