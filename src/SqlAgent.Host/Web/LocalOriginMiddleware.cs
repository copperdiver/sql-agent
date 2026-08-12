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
        // Blazor circuit at /_blazor carrying the user's session cookie.
        //
        // The expected origin is built from this request's OWN authority, i.e. the check is
        // "Origin equals the authority the client addressed", not "Origin equals a fixed bind
        // authority". That is deliberate, and it is why the Host check above has to run first: Host
        // is already constrained to a loopback name there, and a browser sets Host itself from the
        // URL being fetched, so a page served by anything other than this listener cannot make the
        // two agree -- it would present its own scheme/port in Origin against ours in Host. Comparing
        // against a literal LoopbackUrl.Resolve(...) value instead would reject the equally valid
        // http://localhost:5099 and http://[::1]:5099 spellings of this very server, which browsers
        // will happily produce, while buying nothing against a browser-borne attacker. A non-browser
        // client can forge both headers either way, and is stopped by the launch token, not by this.
        var expectedOrigin = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }
}
