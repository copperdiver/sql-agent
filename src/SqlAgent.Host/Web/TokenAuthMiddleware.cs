using System.Security.Cryptography;
using System.Text;

namespace SqlAgent.Host.Web;

/// <summary>
/// Exchanges a one-time <c>?token=</c> query parameter for a session cookie, and refuses everything else
/// with 401 — including WebSocket upgrades, so an unauthenticated caller cannot open a Blazor circuit.
/// Framework assets are exempt: the browser must be able to load blazor.web.js before it can authenticate.
/// </summary>
public sealed class TokenAuthMiddleware(RequestDelegate next, LaunchToken token)
{
    public const string CookieName = "sqlagent_session";

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Request.Path.StartsWithSegments("/_framework"))
        {
            await next(context);
            return;
        }

        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie) && Matches(cookie))
        {
            await next(context);
            return;
        }

        if (context.Request.Query.TryGetValue("token", out var presented) && Matches(presented))
        {
            context.Response.Cookies.Append(CookieName, token.Value, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,     // phase 1 is plain HTTP on loopback; phase 3 adds TLS
                IsEssential = true,
            });
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    }

    private bool Matches(string? presented)
    {
        if (string.IsNullOrEmpty(presented)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(token.Value));
    }
}
