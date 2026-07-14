namespace WireHQ.Api.Middleware;

/// <summary>
/// Adds defense-in-depth security headers to every response, centrally so nothing is missed.
/// The API serves no HTML in production — the OpenAPI reference is an auth-gated JSON endpoint and its
/// UI lives in the SPA (docs/27) — so no CSP is set here; the SPA ships its own strict CSP from Nginx.
/// (docs/04-security.md)
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        headers["Cross-Origin-Opener-Policy"] = "same-origin";

        await next(context);
    }
}
