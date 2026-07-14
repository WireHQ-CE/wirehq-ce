namespace WireHQ.Application.Abstractions;

/// <summary>
/// Builds absolute links into the web client for transactional emails (reset, verify, invite). The base
/// URL is an Infrastructure/config concern (<c>App:BaseUrl</c>), so handlers never hardcode an origin.
/// </summary>
public interface IClientUrlBuilder
{
    /// <summary>Set-/reset-password page, used for both forgot-password and accepting an invite.</summary>
    string ResetPasswordUrl(string rawToken);

    /// <summary>Email-verification page that confirms a registration.</summary>
    string VerifyEmailUrl(string rawToken);

    /// <summary>The sign-in page (e.g. for notifying an existing user they've been added to an org).</summary>
    string LoginUrl();

    /// <summary>An absolute URL to a public site path (e.g. <c>/about</c>, or <c>/</c>) — used by the sitemap.</summary>
    string SiteUrl(string relativePath);

    /// <summary>The marketplace buyer-portal magic-link (docs/19 §4, D-5) — a sign-in that carries the token.</summary>
    string MarketplacePortalUrl(string rawToken);
}
