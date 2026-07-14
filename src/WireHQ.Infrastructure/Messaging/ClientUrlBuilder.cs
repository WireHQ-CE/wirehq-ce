using Microsoft.Extensions.Configuration;
using WireHQ.Application.Abstractions;

namespace WireHQ.Infrastructure.Messaging;

/// <summary>
/// Builds web-client links from the configured base URL (<c>App:BaseUrl</c>, default the local Vite
/// origin). Tokens are URL-encoded. (docs/04-security.md)
/// </summary>
public sealed class ClientUrlBuilder(IConfiguration configuration) : IClientUrlBuilder
{
    private string BaseUrl =>
        (configuration["App:BaseUrl"] is { Length: > 0 } configured ? configured : "http://localhost:28173").TrimEnd('/');

    public string ResetPasswordUrl(string rawToken) => $"{BaseUrl}/reset-password?token={Uri.EscapeDataString(rawToken)}";

    public string VerifyEmailUrl(string rawToken) => $"{BaseUrl}/verify-email?token={Uri.EscapeDataString(rawToken)}";

    public string LoginUrl() => $"{BaseUrl}/login";

    public string MarketplacePortalUrl(string rawToken) => $"{BaseUrl}/marketplace/portal?token={Uri.EscapeDataString(rawToken)}";

    public string SiteUrl(string relativePath)
    {
        if (string.IsNullOrEmpty(relativePath) || relativePath == "/")
        {
            return $"{BaseUrl}/";
        }

        return relativePath.StartsWith('/') ? $"{BaseUrl}{relativePath}" : $"{BaseUrl}/{relativePath}";
    }
}
