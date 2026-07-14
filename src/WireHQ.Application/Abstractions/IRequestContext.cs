namespace WireHQ.Application.Abstractions;

/// <summary>
/// Ambient request metadata captured for audit/observability (source IP, user agent,
/// correlation id). Implemented in the API over <c>HttpContext</c>.
/// </summary>
public interface IRequestContext
{
    string? IpAddress { get; }

    string? UserAgent { get; }

    string? RequestId { get; }
}
