namespace WireHQ.Application.Abstractions;

/// <summary>
/// Abstracts "now" so time-dependent logic (lockouts, token expiry) is deterministically
/// testable. Always UTC.
/// </summary>
public interface IDateTimeProvider
{
    DateTimeOffset UtcNow { get; }
}
