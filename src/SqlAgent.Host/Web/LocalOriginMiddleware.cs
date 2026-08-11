namespace SqlAgent.Host.Web;

/// <summary>
/// Rejects requests whose Host or Origin is not local. A loopback port is reachable from any page the
/// user has open, so without this a hostile site could drive the configuration UI, and a name resolving
/// to 127.0.0.1 could defeat the browser's same-origin rule (DNS rebinding). Runs ahead of everything
/// else, including the Blazor WebSocket negotiation.
/// </summary>
public sealed class LocalOriginMiddleware(RequestDelegate next)
{
    private static readonly string[] LocalHosts = ["127.0.0.1", "localhost", "[::1]"];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsLocalHost(context.Request.Host.Host) || !IsLocalOrigin(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context);
    }

    private static bool IsLocalHost(string host) =>
        LocalHosts.Contains(host, StringComparer.OrdinalIgnoreCase);

    private static bool IsLocalOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin;

        // No Origin at all is normal for a top-level navigation, so absence is not a rejection.
        if (string.IsNullOrEmpty(origin)) return true;

        // Comparing only the Origin's host (e.g. against 127.0.0.1/localhost/[::1]) is not enough:
        // SameSite cookie scoping is computed from the registrable domain and ignores port, and a
        // WebSocket handshake is not subject to CORS at all. A host-only check would therefore also
        // accept any other local HTTP surface on a different port or scheme -- a dev server, an
        // Electron app, a local service reflecting attacker-controlled content -- letting it open a
        // Blazor circuit at /_blazor carrying the user's session cookie. Require the presented Origin
        // to equal this server's own scheme and authority exactly, so only the process actually bound
        // to this port is accepted.
        var expectedOrigin = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }
}
