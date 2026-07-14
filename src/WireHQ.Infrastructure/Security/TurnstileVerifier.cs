using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using WireHQ.Application.Abstractions.Security;

namespace WireHQ.Infrastructure.Security;

/// <summary>
/// Calls Cloudflare's Turnstile <c>siteverify</c> endpoint over a typed <see cref="HttpClient"/>.
/// Any missing input, transport error, or non-success verdict resolves to <c>false</c>. The endpoint
/// is configurable (<c>Turnstile:VerifyUrl</c>) so it can be pointed at a stub in non-prod.
/// (docs/04-security.md)
/// </summary>
public sealed class TurnstileVerifier(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<TurnstileVerifier> logger)
    : ITurnstileVerifier
{
    private const string DefaultVerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    public async Task<bool> VerifyAsync(string secret, string? token, string? remoteIp, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var url = configuration["Turnstile:VerifyUrl"] is { Length: > 0 } configured ? configured : DefaultVerifyUrl;

        var form = new Dictionary<string, string>
        {
            ["secret"] = secret,
            ["response"] = token,
        };
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            form["remoteip"] = remoteIp;
        }

        try
        {
            using var response = await httpClient.PostAsync(url, new FormUrlEncodedContent(form), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Turnstile siteverify returned HTTP {Status}.", (int)response.StatusCode);
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<SiteVerifyResponse>(cancellationToken);
            return result?.Success ?? false;
        }
        catch (Exception ex)
        {
            // Never throw out of the auth pipeline on a CAPTCHA transport failure — deny this attempt.
            logger.LogWarning(ex, "Turnstile siteverify call failed.");
            return false;
        }
    }

    private sealed record SiteVerifyResponse(bool Success);
}
