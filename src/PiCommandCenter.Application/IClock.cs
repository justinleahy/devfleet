namespace PiCommandCenter.Application;

/// <summary>
/// Abstraction over the system clock so application logic can be tested deterministically.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
