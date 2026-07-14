using Microsoft.AspNetCore.Http;

namespace WireHQ.Api.Security;

/// <summary>
/// The HttpOnly refresh-token cookie. Shared by ordinary auth and impersonation so its name and options
/// never drift — both flows mint sessions whose successor tokens must round-trip through the same cookie.
/// </summary>
public static class RefreshTokenCookie
{
    public const string Name = "wh_rt";

    public static void Set(HttpResponse response, string token, bool isHttps) =>
        response.Cookies.Append(Name, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = isHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/api",
            Expires = DateTimeOffset.UtcNow.AddDays(30),
        });

    public static void Clear(HttpResponse response) =>
        response.Cookies.Delete(Name, new CookieOptions { Path = "/api" });
}
