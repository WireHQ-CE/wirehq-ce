namespace WireHQ.Application.Abstractions.Security;

/// <summary>
/// Verifies a Cloudflare Turnstile response token against the siteverify API. A thin HTTP wrapper —
/// the caller (the <c>CaptchaBehavior</c>) owns the enable check + the (decrypted) secret, so this
/// stays free of persistence and is trivially faked in tests. Implemented in Infrastructure over a
/// typed <c>HttpClient</c>. (docs/04-security.md)
/// </summary>
public interface ITurnstileVerifier
{
    /// <summary>True iff Cloudflare confirms the token. Any missing input, network error, or non-success
    /// verdict yields false (fail-closed for the verification itself).</summary>
    Task<bool> VerifyAsync(string secret, string? token, string? remoteIp, CancellationToken cancellationToken = default);
}
